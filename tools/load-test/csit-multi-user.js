import http from 'k6/http';
import crypto from 'k6/crypto';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const baseUrl = __ENV.CSIT_LOAD_BASE_URL || 'http://127.0.0.1:5088';
const runId = __ENV.CSIT_LOAD_RUN_ID;
const secret = __ENV.CSIT_LOAD_IDENTITY_SECRET;
const level = Number(__ENV.CSIT_LOAD_USERS || 5);
const workload = __ENV.CSIT_LOAD_WORKLOAD || 'mixed';
const output = __ENV.CSIT_LOAD_OUTPUT || 'artifacts/load-test/level-5';
if (baseUrl !== 'http://127.0.0.1:5088') throw new Error(`Refusing non-isolated target: ${baseUrl}`);
if (!/^[a-z0-9]{6,32}$/.test(runId || '') || !secret || secret.length < 32) throw new Error('Missing isolated run identity.');
if (![5, 20, 50].includes(level)) throw new Error(`Unsupported user level: ${level}`);
if (!['mixed', 'documents', 'reservations', 'concurrency'].includes(workload)) throw new Error(`Unsupported workload: ${workload}`);

export const options = {
  summaryTrendStats: ['count', 'avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  stages: [
    { duration: __ENV.CSIT_LOAD_WARMUP || '5s', target: Math.min(level, 5) },
    { duration: __ENV.CSIT_LOAD_STEADY || '20s', target: level },
    { duration: '5s', target: 0 },
  ],
  thresholds: {
    checks: ['rate==1'],
    system_failure: ['rate==0'],
    authorization_violation: ['rate==0'],
    correctness_violation: ['rate==0'],
  },
};

const systemFailure = new Rate('system_failure');
const authorizationViolation = new Rate('authorization_violation');
const correctnessViolation = new Rate('correctness_violation');
const businessConflict = new Counter('business_conflict');
const operations = {};
for (const name of ['read', 'edit', 'verification_attachment', 'verification_submit',
  'verification_approval', 'bypass_admin', 'bypass_teamleader', 'upload_equipment',
  'upload_equipment_pdf', 'upload_equipment_png', 'upload_equipment_jpg', 'upload_vendor',
  'upload_document', 'approval', 'reservation', 'reservation_approval', 'download', 'delete']) {
  operations[name] = { success: new Rate(`op_${name}_success`), duration: new Trend(`op_${name}_duration`, true) };
}

function identity(role, index = 1, scope = 'CsitStaff', access = 'Editor') {
  const roleKey = role.toLowerCase();
  const account = `load-${runId}-${roleKey}-${index}`;
  const canonical = [account, role, scope, access, 'DA40', `${role} ${index}`, runId].join('|');
  const signature = crypto.hmac('sha256', secret, canonical, 'hex');
  return { headers: { 'X-CSIT-Load-Identity': `${canonical}|${signature}` } };
}

function jsonParams(who) {
  return { headers: { ...who.headers, 'Content-Type': 'application/json' } };
}

function observe(name, response, accepted, expectedConflict = false) {
  return observeMany([name], response, accepted, expectedConflict);
}

function observeMany(names, response, accepted, expectedConflict = false) {
  const ok = accepted.includes(response.status);
  for (const name of names) {
    operations[name].duration.add(response.timings.duration);
    operations[name].success.add(ok);
  }
  systemFailure.add(response.status >= 500);
  authorizationViolation.add(response.status === 401 || response.status === 403);
  if (response.status === 409 && expectedConflict) businessConflict.add(1);
  correctnessViolation.add(!ok && response.status < 500 && response.status !== 401 && response.status !== 403);
  check(response, { [`${names.join('+')} returned an allowed status`]: () => ok });
  return ok;
}

export function setup() {
  const admin = identity('Admin');
  const signedIdentity = admin.headers['X-CSIT-Load-Identity'];
  const invalidSignature = `${signedIdentity.slice(0, -1)}${signedIdentity.endsWith('0') ? '1' : '0'}`;
  const missingIdentity = http.get(`${baseUrl}/api/load-test-control/health`);
  const invalidIdentity = http.get(`${baseUrl}/api/load-test-control/health`, {
    headers: { 'X-CSIT-Load-Identity': invalidSignature },
  });
  if (!check([missingIdentity, invalidIdentity], {
    'load-test authentication fails closed': responses => responses.every(r => r.status === 401),
  })) throw new Error(`Load-test authentication accepted an unsigned or invalid identity: ${missingIdentity.status},${invalidIdentity.status}`);
  const health = http.get(`${baseUrl}/api/load-test-control/health`, admin);
  if (!check(health, { 'isolated host attested': r => r.status === 200 && r.json('isolated') === true && r.json('kmDispatch') === false }))
    throw new Error(`Isolation attestation failed: ${health.status} ${health.body}`);
  const boot = http.post(`${baseUrl}/api/load-test-control/bootstrap`, null, admin);
  if (boot.status !== 200) throw new Error(`Bootstrap failed: ${boot.status} ${boot.body}`);
  const data = boot.json();

  // Fail closed if a read-only identity can mutate.
  const readOnly = identity('ReadOnly', 1, 'CsitStaff', 'ReadOnly');
  const forbidden = http.post(`${baseUrl}/api/vendors`, JSON.stringify({ name: `forbidden-${runId}`, types: ['Supplier'] }), jsonParams(readOnly));
  if (!check(forbidden, { 'read-only policy enforced': r => r.status === 403 }))
    throw new Error(`BusinessWrite policy bypass: ${forbidden.status}`);

  // Two writers use the same xmin. Exactly one may win; silent last-write-wins fails the run.
  const detail = http.get(`${baseUrl}/api/vendors/${data.vendorId}`, admin);
  if (detail.status !== 200) throw new Error(`Vendor concurrency seed unavailable: ${detail.status}`);
  const rowVersion = detail.json('rowVersion');
  const request = JSON.stringify({ name: `LOAD-${runId}-Vendor`, types: ['Manufacturer'], isActive: true,
    rowVersion, notes: 'concurrency-probe' });
  const edits = http.batch([
    ['PUT', `${baseUrl}/api/vendors/${data.vendorId}`, request, jsonParams(admin)],
    ['PUT', `${baseUrl}/api/vendors/${data.vendorId}`, request, jsonParams(identity('TeamLeader'))],
  ]);
  const editSuccess = edits.filter(r => r.status === 204).length;
  const editConflict = edits.filter(r => r.status === 409).length;
  for (const response of edits) {
    operations.edit.duration.add(response.timings.duration);
    operations.edit.success.add(response.status === 204 || response.status === 409);
    systemFailure.add(response.status >= 500);
    if (response.status === 409) businessConflict.add(1);
  }
  if (!check(edits, { 'stale edit cannot silently overwrite': () => editSuccess === 1 && editConflict === 1 }))
    throw new Error(`Concurrency guard failed; statuses=${edits.map(r => r.status).join(',')}`);
  return data;
}

function actor() {
  const bucket = (__VU - 1) % 5;
  if (bucket === 2) return { role: 'TeamLeader', scope: 'CsitStaff', auth: identity('TeamLeader', __VU) };
  if (bucket === 3) return { role: 'Admin', scope: 'CsitStaff', auth: identity('Admin', __VU) };
  if (bucket === 4) return { role: 'ReadOnly', scope: 'CsitStaff', auth: identity('ReadOnly', __VU, 'CsitStaff', 'ReadOnly') };
  const scope = __VU % 2 ? 'CsitStaff' : 'RdApplicant';
  return { role: 'Employee', scope, auth: identity('Employee', __VU, scope) };
}

function reads(data, who) {
  const requests = [['GET', `${baseUrl}/api/reservations?active=true`, null, who.auth]];
  if (who.scope === 'CsitStaff') requests.push(
    ['GET', `${baseUrl}/api/vendors?includeDisabled=true`, null, who.auth],
    ['GET', `${baseUrl}/api/assets/equipment`, null, who.auth],
    ['GET', `${baseUrl}/api/document-library/folders`, null, who.auth],
    ['GET', `${baseUrl}/api/vendors/${data.vendorId}`, null, who.auth]);
  for (const response of http.batch(requests)) observe('read', response, [200]);
}

function verificationScenario(who) {
  let flow;
  if (who.role === 'Employee' && who.scope === 'CsitStaff' && __ITER % 10 === 0)
    flow = { role: 'Employee', auth: who.auth };
  else if (who.role === 'TeamLeader' && __ITER % 20 === 0)
    flow = { role: 'TeamLeader', auth: identity('TeamLeader') };
  else if (who.role === 'Admin' && __ITER % 20 === 0)
    flow = { role: 'Admin', auth: identity('Admin') };
  else return;

  const draft = http.post(`${baseUrl}/api/load-test-control/verification-applications/drafts`, null, flow.auth);
  if (draft.status !== 200) {
    observe('verification_submit', draft, [200]);
    return;
  }
  const applicationId = draft.json('id');
  const attachment = http.post(`${baseUrl}/api/verification-applications/${applicationId}/files`,
    { files: http.file(`verification-${__VU}-${__ITER}`, 'same-name-verification.pdf', 'application/pdf') }, flow.auth);
  const attachmentOk = observe('verification_attachment', attachment, [200]) &&
    attachment.json('results.0.success') === true && !!attachment.json('results.0.file.id');
  correctnessViolation.add(!attachmentOk);
  check(attachment, { 'verification attachment persisted': () => attachmentOk });
  if (!attachmentOk) return;
  const fileId = attachment.json('results.0.file.id');
  observe('download', http.get(`${baseUrl}/api/verification-application-files/${fileId}/download`, flow.auth), [200]);

  const submitted = http.post(`${baseUrl}/api/load-test-control/verification-applications/${applicationId}/submit`, null, flow.auth);
  const submitMetrics = ['verification_submit'];
  if (flow.role === 'Admin') submitMetrics.push('bypass_admin');
  if (flow.role === 'TeamLeader') submitMetrics.push('bypass_teamleader');
  if (!observeMany(submitMetrics, submitted, [200])) return;

  if (flow.role !== 'Employee') {
    const bypassed = submitted.json('accepted') === true;
    correctnessViolation.add(!bypassed);
    check(submitted, { [`${flow.role} verification review bypassed`]: () => bypassed });
    return;
  }

  const reviews = http.batch([
    ['POST', `${baseUrl}/api/load-test-control/verification-applications/${applicationId}/accept`, null, identity('TeamLeader')],
    ['POST', `${baseUrl}/api/load-test-control/verification-applications/${applicationId}/accept`, null, identity('TeamLeader')],
  ]);
  for (const response of reviews) observe('verification_approval', response, [200, 409], true);
  const accepted = reviews.filter(r => r.status === 200).length;
  const conflict = reviews.filter(r => r.status === 409).length;
  const exactlyOnce = accepted === 1 && conflict === 1;
  correctnessViolation.add(!exactlyOnce);
  check(reviews, { 'verification accepted only once': () => exactlyOnce });
}

function reservation(data, who) {
  if (who.role !== 'Employee' || __ITER % 3 !== 0) return;
  const slot = (__VU - 1) * 8 + (__ITER % 8);
  const start = new Date(Date.now() + 7 * 86400000 + slot * 2 * 3600000);
  start.setUTCMinutes(0, 0, 0);
  const end = new Date(start.getTime() + 30 * 60000);
  const body = JSON.stringify({ mode: 0, purpose: 'Synthetic concurrency reservation', startTime: start.toISOString(),
    endTime: end.toISOString(), items: [{ apparatusId: data.equipmentId }], applicantExtension: '9999',
    productModelName: 'LOAD-MODEL', customer: 'Synthetic', projectSubPu: '1A',
    applicantAgentName: 'Load Agent', applicantAgentExtension: '9998', applicantAgentEmail: 'agent@load.invalid' });
  const created = http.post(`${baseUrl}/api/reservations`, body, jsonParams(who.auth));
  if (!observe('reservation', created, [201, 409], true) || created.status === 409) return;
  const id = created.json('id');
  const submitted = http.post(`${baseUrl}/api/reservations/${id}/submit`, null, who.auth);
  observe('reservation', submitted, [200, 409], true);
  if (submitted.status === 200) {
    const leader = identity('TeamLeader');
    const approvals = http.batch([
      ['POST', `${baseUrl}/api/reservations/${id}/approve`, null, leader],
      ['POST', `${baseUrl}/api/reservations/${id}/approve`, null, leader],
    ]);
    for (const response of approvals) observe('reservation_approval', response, [200, 409], true);
    const success = approvals.filter(r => r.status === 200).length;
    const conflict = approvals.filter(r => r.status === 409).length;
    correctnessViolation.add(!(success === 1 && conflict === 1));
    check(approvals, { 'reservation approved only once': () => success === 1 && conflict === 1 });
  }
}

function equipmentUpload(data, who) {
  if (who.role !== 'Employee' || who.scope !== 'CsitStaff' || __ITER % 5 !== 0) return;
  const ext = ['pdf', 'png', 'jpg'][(__VU + __ITER) % 3];
  const types = { pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg' };
  const body = { files: http.file(`CSIT ${ext} synthetic ${__VU}-${__ITER}`, `same-name.${ext}`, types[ext]) };
  const upload = http.post(`${baseUrl}/api/assets/equipment/${data.equipmentId}/files`, body, who.auth);
  if (observeMany(['upload_equipment', `upload_equipment_${ext}`], upload, [202])) {
    const approve = http.post(`${baseUrl}/api/load-test-control/approvals/approve-next?contextId=${data.equipmentId}`, null,
      identity('TeamLeader'));
    if (observe('approval', approve, [200, 409], true) && approve.status === 200) {
      const fileId = approve.json('fileId');
      if (fileId) observe('download', http.get(`${baseUrl}/api/assets/files/${fileId}/content`, who.auth), [200]);
      else correctnessViolation.add(true);
    }
  }
}

function vendorUploadDelete(data, who) {
  if (who.role !== 'Admin' || __ITER % 4 !== 0) return;
  const upload = http.post(`${baseUrl}/api/vendors/${data.vendorId}/files`,
    { files: http.file(`vendor-${__VU}-${__ITER}`, 'same-name.pdf', 'application/pdf') }, who.auth);
  if (!observeMany(['upload_vendor', 'bypass_admin'], upload, [200])) return;
  const ids = upload.json('operationIds') || [];
  if (!ids.length) { correctnessViolation.add(true); return; }
  if (__ITER % 8 !== 0) return;
  const id = ids[0];
  const results = http.batch([
    ['GET', `${baseUrl}/api/vendors/files/${id}/content`, null, who.auth],
    ['DELETE', `${baseUrl}/api/vendors/files/${id}`, null, who.auth],
  ]);
  observe('download', results[0], [200, 404, 409]);
  observe('delete', results[1], [202, 204, 409]);
}

function documentUpload(data, who) {
  if (who.role !== 'TeamLeader' || __ITER % 5 !== 0) return;
  const routedLeader = identity('TeamLeader');
  const upload = http.post(`${baseUrl}/api/document-library/folders/${data.folderId}/files`,
    { files: http.file(`document-${__VU}-${__ITER}`, 'same-name.png', 'image/png') }, routedLeader);
  observeMany(['upload_document', 'bypass_teamleader'], upload, [200]);
}

export default function (data) {
  const who = actor();
  reads(data, who);
  if (who.role === 'ReadOnly') { sleep(0.1); return; }
  if (workload === 'mixed' || workload === 'concurrency') verificationScenario(who);
  if (workload === 'mixed' || workload === 'reservations' || workload === 'concurrency') reservation(data, who);
  if (workload === 'mixed' || workload === 'documents') {
    equipmentUpload(data, who); vendorUploadDelete(data, who); documentUpload(data, who);
  }
  sleep(0.1);
}

export function teardown() {
  const response = http.get(`${baseUrl}/api/load-test-control/invariants`, identity('Admin'));
  correctnessViolation.add(response.status !== 200 || response.json('passed') !== true);
  check(response, { 'database and file invariants hold': r => r.status === 200 && r.json('passed') === true });
}

export function handleSummary(data) {
  data.csit = { runId, level, workload, target: baseUrl, generatedAt: new Date().toISOString() };
  return { [`${output}/k6-summary.json`]: JSON.stringify(data, null, 2), stdout: JSON.stringify(data.csit) + '\n' };
}

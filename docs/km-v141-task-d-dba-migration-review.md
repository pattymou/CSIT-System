# CSIT × KM v1.4.1 Task D — DBA migration review

## Gate and exact scope

- Migration: `20261108070415_AddKmTaskDDocumentDeletionV141`
- Required predecessor: `20261106074818_AddKmTaskCIntegrationV141`
- Review SQL: `docs/km-v141-task-d-migration.sql`
- SQL SHA256: `8C95CDFAA777CECF9AACA8B2EDB8EE83BDA1A31564029CF5BAD991B63FA9D8E1`
- Authorized target after separate protected approval: `sit_department_system_dev` only.
- This review package does not itself approve or apply the migration, create a marker, deploy,
  enable KM delivery, or modify Production.

The protected approval record must bind the pushed commit, migration ID, SQL hash, DEV database,
approval time, and operator. A marker for any other tuple is invalid.

## Schema and data changes

The migration contains only Task D deletion-lifecycle changes.

1. `equipment_file_deletion_jobs`
   - Adds nullable `canonical_ingestion_event_id uuid` and required `reason_code varchar(40)`.
   - Converts the persisted external source identifier from draft `ApparatusFile` to formal
     `EquipmentAttachment`.
   - Backfills `reason_code='SourceDeleted'` for existing jobs and, where the matching ingestion
     Outbox exists, copies its canonical `event_id`.
   - Replaces only the identity CHECK so version 1 and the three approved v1.4.1 reason codes are
     enforced.
2. `vendor_file_deletion_jobs`
   - Adds the durable Vendor deletion request, immutable resource correlation, retry/lease state,
     KM job identity, KM verification time, and local cleanup recovery evidence.
   - Keeps the job after the source attachment is removed, providing a durable anti-revival hold.
   - Adds unique Vendor-file and filtered KM-job indexes plus status/identity CHECK constraints.
3. `km_v2_document_deletion_callbacks`
   - Adds global callback-id idempotency evidence for both Vendor and Equipment v2 deletion.
   - Copies existing Equipment draft callback receipt IDs and hashes without deleting or changing
     the historical draft receipt table.

There is no historical attachment/Audit/Outbox deletion, NAS operation, trigger, Production or
Shared Identity change, Anderson KM schema, Task C version-chain rewrite, or feature enablement.

## Lock, compatibility, and capacity notes

- `equipment_file_deletion_jobs` receives a short table lock while two columns and its identity
  CHECK are changed. Existing rows are updated once; inspect row count and active states first.
- The backfill joins only the indexed source identity in `km_integration_outbox`. A missing Outbox
  is permitted for jobs that already completed local-only cleanup; an active KM-bound job without
  a canonical Outbox is a preflight failure and must be diagnosed before approval.
- Both new tables and their indexes are expected to be small and initially empty except for copied
  historical Equipment callback receipts.
- Existing TestReport, DocumentLibrary and VerificationApplicationAttachment v1 deletion tables,
  contracts, callbacks, and constraints are unchanged.
- `KmIntegration.Enabled`, `KmIntegration.VendorEnabled`,
  `KmIntegration.EquipmentEnabled`, `KmDeletion.Enabled`, `KmDeletion.VendorEnabled`, and
  `KmDeletion.EquipmentEnabled` remain false.

## DBA preflight queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261106074818_AddKmTaskCIntegrationV141',
  '20261108070415_AddKmTaskDDocumentDeletionV141'
)
ORDER BY "MigrationId";

SELECT status, count(*)
FROM equipment_file_deletion_jobs
GROUP BY status
ORDER BY status;

SELECT count(*) AS active_equipment_jobs_without_canonical_outbox
FROM equipment_file_deletion_jobs AS deletion
WHERE deletion.status NOT IN ('LocalCleanup','CleanupFailed','Completed')
  AND NOT EXISTS (
    SELECT 1 FROM km_integration_outbox AS ingestion
    WHERE ingestion.source_entity_id = deletion.apparatus_file_id
      AND ingestion.source_entity_type = 'ApparatusFile'
      AND ingestion.event_type IN ('EquipmentDocumentArchived','EquipmentAttachmentArchived')
  );

SELECT count(*) AS historical_equipment_callback_receipts
FROM equipment_file_deletion_callbacks;

SELECT relname, pg_total_relation_size(relid) AS total_bytes, n_live_tup
FROM pg_stat_user_tables
WHERE relname IN (
  'equipment_file_deletion_jobs', 'equipment_file_deletion_callbacks',
  'km_integration_outbox', 'vendor_files', 'apparatus_files'
)
ORDER BY relname;
```

The predecessor must be present, Task D must be absent, and
`active_equipment_jobs_without_canonical_outbox` must be zero.

## Post-migration validation

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141';

SELECT to_regclass('public.vendor_file_deletion_jobs'),
       to_regclass('public.km_v2_document_deletion_callbacks');

SELECT count(*) AS invalid_equipment_v2_identity
FROM equipment_file_deletion_jobs
WHERE source_entity_type <> 'EquipmentAttachment'
   OR reason_code NOT IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup');

SELECT count(*) AS unmigrated_callback_receipts
FROM equipment_file_deletion_callbacks AS old
WHERE NOT EXISTS (
  SELECT 1 FROM km_v2_document_deletion_callbacks AS current
  WHERE current.callback_id = old.callback_id
    AND current.deletion_request_id = old.deletion_job_id
    AND current.source_type = 'EquipmentAttachment'
    AND current.payload_sha256 = old.payload_sha256
);

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid IN (
  'equipment_file_deletion_jobs'::regclass,
  'vendor_file_deletion_jobs'::regclass
)
ORDER BY conname;

SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename IN (
  'vendor_file_deletion_jobs', 'km_v2_document_deletion_callbacks'
)
ORDER BY tablename, indexname;
```

The migration-history row and both tables must exist; both invalid/unmigrated counts must be zero;
constraints and indexes must exactly match the reviewed SQL.

## Isolated PostgreSQL evidence

Validated on 2026-11-08 (Asia/Taipei) against PostgreSQL 16.13 bound only to
`127.0.0.1:55435`. The cluster data directory was
`C:\CSIT_System\isolated-postgres\task-d-v141-20261108T151024\data`; neither DEV nor Production
was used.

- Migration database: `csit_task_d_v141`.
- Migration history contains exactly one
  `20261108070415_AddKmTaskDDocumentDeletionV141` row after the Task C predecessor.
- Post-migration validation found `invalid_equipment_v2_identity=0`, copied the one historical
  Equipment callback receipt, and retained the existing Task C document, Outbox, and Audit rows.
- EF reported no model changes after the Task D migration; the generated SQL SHA256 remained
  `8C95CDFAA777CECF9AACA8B2EDB8EE83BDA1A31564029CF5BAD991B63FA9D8E1`.
- Lifecycle database: `csit_task_d_flow_20261108_1545`, created inside the same isolated cluster
  from the migrated validation database.
- Real Npgsql Vendor and Equipment lifecycle tests passed 2/2, including row locks, ingestion
  lease takeover, v2 dispatch, Completed callback, duplicate callback idempotency, durable
  callback receipt, Outbox cleanup, source-row cleanup, and physical-file cleanup.
- Data validation found `invalid_vendor=0`, `invalid_equipment=0`, and every one of the three
  callback receipts correlated to a retained deletion job. Completed source rows were absent
  while one Vendor and three Equipment durable tombstones remained.
- The three Task D tables intentionally have no source-row foreign key (`fk_count=0`), so deleting
  the attachment cannot cascade away the durable job or callback idempotency evidence.
- Negative tests proved the Vendor status CHECK, Equipment reason-code CHECK, and callback primary
  key reject invalid or duplicate writes; the rejected writes left zero invalid rows.
- Release build completed with zero errors and the full regression suite passed 634/634.

## Rollback notes

- `Down` drops the Vendor job and unified v2 callback tables, destroying Task D audit and
  anti-revival evidence after activation.
- `Down` removes formal Equipment reason/canonical correlation and restores the draft source
  identifier. It is not a safe operational rollback after formal v2 deletion begins.
- Prefer a reviewed forward fix or a verified pre-migration backup. Do not run `Down` directly on
  DEV or Production.

## Required DEV sequence after protected approval

1. Review the exact pushed commit, migration source, SQL and SHA256 above.
2. Confirm the DEV preflight conditions and a maintenance window.
3. Take the formal DEV backup and verify an isolated restore.
4. Apply only `docs/km-v141-task-d-migration.sql` using `psql -X -v ON_ERROR_STOP=1` and the
   protected DBA credential flow; never use `csit_runtime_dev` for DDL.
5. Run all post-migration validation and preserve non-sensitive evidence.
6. Create the exact-SHA marker only through the existing protected Windows Administrator flow.
7. Run the DEV Ready release, watcher deployment, smoke test, and runtime-SHA verification.

Production 5010, Shared Identity, Anderson KM, historical files/Audit/Outbox, and full A–D mock E2E
are outside this review and must not be modified.

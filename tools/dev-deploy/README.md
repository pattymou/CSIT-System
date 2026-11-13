# CSIT DEV ready deployment

The `CSIT DEV Ready Watcher` Windows task runs every minute as `NT AUTHORITY\LOCAL SERVICE`. It reads public GitHub tags but has no GitHub write credential, Production credential, or Production Service control rights. Only a `dev-ready-YYYYMMDDHHMMSS-<8-character SHA>` tag pointing to the current head of `feature/km-stage-integration` can start a deployment. An ordinary commit does nothing.

The same single watcher also has an at-startup trigger. When the newest ready tag still matches
`state\current.json`, it checks the recorded PID, executable path, release manifest, DLL hash, 7296/7444
listeners, HTTPS endpoints, and OIDC redirect. A healthy runtime is left untouched. If no DEV runtime
exists, `Restart-DevCurrent.ps1` may restart only the already tested release recorded in `current.json`;
it does not fetch, build, test, publish, create a tag, or select a different SHA. Recovery requires a
healthy Production 5010 guard, the DEV database, every configured DEV storage root, Shared Identity DEV,
and no active equipment deletion job. Existing or duplicate processes fail closed. Attempts use the
deployment lock, a five-minute cooldown, and a maximum of three starts per hour; results are written to
`state\runtime-recovery.json` and `logs\runtime-recovery.log`.

For an approved configuration-only maintenance window, an Administrator may run
`Request-DevRuntimeMaintenance.ps1` with the exact SHA and PID from `state\current.json`. It writes a
short-lived, SHA/PID/path-bound request under `state\runtime-maintenance-request.json`. The existing
LOCAL SERVICE watcher consumes that request under `deploy.lock`, verifies the current release and full
HTTPS smoke checks, restarts only the exact recorded DEV executable, preserves the Production 5010 PID,
and removes the request after success. Invalid, expired, duplicate, or unhealthy requests fail closed;
the request is never exposed through a website API.

For a one-shot LOCAL SERVICE M2M credential check, an Administrator may instead run
`Request-DevM2MTokenVerification.ps1` with the exact SHA and PID from `state\current.json`. The watcher
executes the deployed application with the fixed `--verify-km-m2m-token` argument, records only safe
status/claims fields, consumes the request, and never starts the web listener or exposes a user API.

For an approved Gate 1 probe, an Administrator may run `Request-DevKmOutboxDispatch.ps1` with the exact
SHA, PID, OutboxId, and EventId. The watcher dispatches only that
`VerificationApplicationAttachmentArchived` row through the normal v2 client and lease/idempotency
state machine while global KM flags remain disabled. The short-lived request is consumed and is not a
general queue or HTTP dispatch endpoint.

For a specifically approved recovery of the recorded Gate 1 HTTP 401, an Administrator may run
`Request-DevKmOutboxRetry.ps1` with the exact SHA, PID, OutboxId, and EventId. The watcher invokes a
single LOCAL SERVICE transaction that accepts only a Failed verification-attachment row with
`HTTP_401`, no KM job, no lease, and an available retry. It revalidates formal approval, NAS content,
SHA256, and deletion holds, writes the existing manual-retry audit record, and requeues the same
outbox row without creating a second event. It does not dispatch the event; a separate, later
`Request-DevKmOutboxDispatch.ps1` request is required after KM real mode and authorization are
confirmed. Global KM flags remain disabled.

`Release-DevReady.ps1` is the release command for Codex after source review, Build/Test, commit, and push. It independently builds and tests the pushed commit in a clean checkout before pushing the ready tag. `Watch-DevReady.ps1` then invokes `Deploy-DevReady.ps1` from the protected local automation directory. The deployer clones the exact commit, builds it, runs the full test suite, and publishes the tested DLL into `C:\ProgramData\CSIT-DEV\releases\<SHA>`. A file lock permits one deployment at a time.

The deployer reads only `C:\ProgramData\CSIT-DEV\secrets\runtime.json`, which targets `sit_department_system_dev` as `csit_runtime_dev`. It never pushes secrets or artifacts to GitHub. A change involving migration files stops before activation for DEV DBA review and backup. The current release stays live during Build/Test. After activation, HTTPS, OIDC redirect, static assets, Blazor WebSocket, and 7444 guards are checked. If a subsequent release fails smoke checks, the previous successful DEV release is restarted. A failed ready tag is recorded in `state\failed.json` and is not retried every minute.

DEV formal files are restricted to `\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV`; `DocumentStaging` remains local. The legacy layout uses separate `DocumentLibrary`, `Assets`, `ThreeLevel`, `TestReports`, and `RawData` children. `Reconcile-DevNasBusinessHierarchy.ps1` converts it to exact L1/L2/L3 business folders only. Its audit and apply modes both require an explicitly confirmed `-TechnicalRoot` under `\\AI-NAS\Workspace_B` but outside the formal root. It rejects unclassified files, unknown asset classifications, non-three-level targets, duplicate targets, path escapes, missing files, and SHA256 mismatches. Apply copies and verifies every target, switches DB/runtime paths transactionally, then moves the verified legacy source trees to `TechnicalRoot\LegacySourceBackup`; it never deletes the retained sources or changes completed/failed KM jobs. Apply evidence separately seals the pre-migration runtime SHA and reviewed Apply commit SHA plus hashes and sizes for the manifest, SQL, runtime, and `current.json` snapshots. A transition fence prevents the watcher from restarting the old runtime after Apply and permits only the sealed Apply commit to deploy. `Rollback-DevNasBusinessHierarchy.ps1` additionally requires the explicitly approved current runtime SHA, accepts only either the sealed pre-deployment or post-deployment relationship, re-verifies every evidence and target hash, retains the new formal and technical targets outside their active roots, restores the legacy trees, DB paths, runtime configuration, and prior `current.json`, then leaves a rollback fence that permits recovery of only the pre-migration runtime. `Test-DevNasAccess.ps1` verifies list/write/read/delete as the actual `LOCAL SERVICE` identity for either the formal root or an approved separate technical root without changing NAS ACLs.

When AI-NAS uses a dedicated non-administrator NAS account, an Administrator runs `Set-DevNasCredential.ps1` interactively on the DEV host. The password is entered with hidden input and sent only through a one-use named pipe whose ACL permits the invoking Administrator and `LOCAL SERVICE`; it is never placed in arguments, files, settings, Git, or logs. The worker stores a `CRED_TYPE_DOMAIN_PASSWORD` for `AI-NAS` in the `LOCAL SERVICE` Windows Credential Manager. Run `Test-DevNasAccess.ps1` afterward; storage is not authorized until its controlled list/write/read/SHA256/delete probe passes. `Set-DevNasCredential.ps1 -Remove` removes this credential for rollback.

After the DBA applies and verifies a migration, an Administrator may run the protected `Approve-DevMigrationReview.ps1` for the exact 40-character release SHA and exact Migration IDs. The marker is stored only in `C:\ProgramData\CSIT-DEV\migration-reviews`; Administrators and SYSTEM can modify it while LOCAL SERVICE has read-only access. The deployer still queries `sit_department_system_dev.__EFMigrationsHistory` with the runtime read credential and requires an exact SHA/database/Migration match. Only a prior `DEV_MIGRATION_REVIEW_REQUIRED` failure becomes retryable; other failures remain stopped. `Update-DevWatcherAutomation.ps1` updates the installed protected automation scripts without changing runtime secrets, task identity, or Production. It holds both watcher and deployment locks, saves the original script bytes, hashes, sizes, ACLs, complete task XML, trigger snapshot, and protected-directory ACLs under `maintenance\automation-update-*`, verifies the backup, then stages and verifies every approved script before replacement. Any failure restores and re-verifies every script, ACL, and task definition; the evidence directory records either `Updated`, `RolledBack`, or `RollbackFailed` plus an integrity manifest.

`Install-DevWatcher.ps1` is the one-time Administrator bootstrap and is also used after a DEV credential rotation to refresh the restricted secret copy. It does not touch Production. The first move from the historical Administrator-run DEV process is a monitored handoff; later releases and rollback run automatically under `LOCAL SERVICE`.

`Initialize-DevManagedAssetStorage.ps1` is the Administrator-only, DEV-only ACL initializer for the exact enabled `AssetRootPath/equipment`. Its default mode is read-only and inventories every active equipment deletion job. Applying the inheritable `LOCAL SERVICE` Modify rule requires `-Apply` plus an exact `-ApprovedEquipmentIds` set matching all active deletion jobs, saves the original root SDDL under `C:\ProgramData\CSIT-DEV\maintenance`, refuses overlapping Asset roots or protected descendants, and never changes goods, ApprovalStaging, RawData, Stage, or Production. `-RestoreBackup` restores a saved root SDDL only when no equipment deletion job is active.

Successful deployment records its commit and UTC time in `state\current.json`. Authorized CSIT users also see the short SHA and deployment time in the DEV top bar. The fixed public URL is <https://csit-dev-da40.duckdns.org:7443/>. Source edits become visible after a completed DEV deployment and browser refresh.

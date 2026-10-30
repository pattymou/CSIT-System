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

`Release-DevReady.ps1` is the release command for Codex after source review, Build/Test, commit, and push. It independently builds and tests the pushed commit in a clean checkout before pushing the ready tag. `Watch-DevReady.ps1` then invokes `Deploy-DevReady.ps1` from the protected local automation directory. The deployer clones the exact commit, builds it, runs the full test suite, and publishes the tested DLL into `C:\ProgramData\CSIT-DEV\releases\<SHA>`. A file lock permits one deployment at a time.

The deployer reads only `C:\ProgramData\CSIT-DEV\secrets\runtime.json`, which targets `sit_department_system_dev` as `csit_runtime_dev`. It never pushes secrets or artifacts to GitHub. A change involving migration files stops before activation for DEV DBA review and backup. The current release stays live during Build/Test. After activation, HTTPS, OIDC redirect, static assets, Blazor WebSocket, and 7444 guards are checked. If a subsequent release fails smoke checks, the previous successful DEV release is restarted. A failed ready tag is recorded in `state\failed.json` and is not retried every minute.

After the DBA applies and verifies a migration, an Administrator may run the protected `Approve-DevMigrationReview.ps1` for the exact 40-character release SHA and exact Migration IDs. The marker is stored only in `C:\ProgramData\CSIT-DEV\migration-reviews`; Administrators and SYSTEM can modify it while LOCAL SERVICE has read-only access. The deployer still queries `sit_department_system_dev.__EFMigrationsHistory` with the runtime read credential and requires an exact SHA/database/Migration match. Only a prior `DEV_MIGRATION_REVIEW_REQUIRED` failure becomes retryable; other failures remain stopped. `Update-DevWatcherAutomation.ps1` updates the installed protected automation scripts without changing runtime secrets, task identity, or Production.

`Install-DevWatcher.ps1` is the one-time Administrator bootstrap and is also used after a DEV credential rotation to refresh the restricted secret copy. It does not touch Production. The first move from the historical Administrator-run DEV process is a monitored handoff; later releases and rollback run automatically under `LOCAL SERVICE`.

`Initialize-DevManagedAssetStorage.ps1` is the Administrator-only, DEV-only ACL initializer for the exact enabled `AssetRootPath/equipment`. Its default mode is read-only and inventories every active equipment deletion job. Applying the inheritable `LOCAL SERVICE` Modify rule requires `-Apply` plus an exact `-ApprovedEquipmentIds` set matching all active deletion jobs, saves the original root SDDL under `C:\ProgramData\CSIT-DEV\maintenance`, refuses overlapping Asset roots or protected descendants, and never changes goods, ApprovalStaging, RawData, Stage, or Production. `-RestoreBackup` restores a saved root SDDL only when no equipment deletion job is active.

Successful deployment records its commit and UTC time in `state\current.json`. Authorized CSIT users also see the short SHA and deployment time in the DEV top bar. The fixed public URL is <https://csit-dev-da40.duckdns.org:7443/>. Source edits become visible after a completed DEV deployment and browser refresh.

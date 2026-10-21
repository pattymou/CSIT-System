# CSIT DEV ready deployment

The `CSIT DEV Ready Watcher` Windows task runs every minute as `NT AUTHORITY\LOCAL SERVICE`. It reads public GitHub tags but has no GitHub write credential, Production credential, or Production Service control rights. Only a `dev-ready-YYYYMMDDHHMMSS-<8-character SHA>` tag pointing to the current head of `feature/km-stage-integration` can start a deployment. An ordinary commit does nothing.

`Release-DevReady.ps1` is the release command for Codex after source review, Build/Test, commit, and push. It independently builds and tests the pushed commit in a clean checkout before pushing the ready tag. `Watch-DevReady.ps1` then invokes `Deploy-DevReady.ps1` from the protected local automation directory. The deployer clones the exact commit, builds it, runs the full test suite, and publishes the tested DLL into `C:\ProgramData\CSIT-DEV\releases\<SHA>`. A file lock permits one deployment at a time.

The deployer reads only `C:\ProgramData\CSIT-DEV\secrets\runtime.json`, which targets `sit_department_system_dev` as `csit_runtime_dev`. It never pushes secrets or artifacts to GitHub. A change involving migration files stops before activation for DEV DBA review and backup. The current release stays live during Build/Test. After activation, HTTPS, OIDC redirect, static assets, Blazor WebSocket, and 7444 guards are checked. If a subsequent release fails smoke checks, the previous successful DEV release is restarted. A failed ready tag is recorded in `state\failed.json` and is not retried every minute.

`Install-DevWatcher.ps1` is the one-time Administrator bootstrap and is also used after a DEV credential rotation to refresh the restricted secret copy. It does not touch Production. The first move from the historical Administrator-run DEV process is a monitored handoff; later releases and rollback run automatically under `LOCAL SERVICE`.

Successful deployment records its commit and UTC time in `state\current.json`. Authorized CSIT users also see the short SHA and deployment time in the DEV top bar. The fixed public URL is <https://csit-dev-da40.duckdns.org:7443/>. Source edits become visible after a completed DEV deployment and browser refresh.

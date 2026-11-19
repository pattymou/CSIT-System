# CSIT DEV delivery

長期專案背景、Owner 架構決議、CSIT／Anderson KM 責任分界、已驗證證據與待確認事項，請先閱讀 [`docs/CSIT-PROJECT-CONTEXT.md`](docs/CSIT-PROJECT-CONTEXT.md)。本指引的 DEV Ready、Migration、Production 與資料安全規範仍完整有效。

For a completed, deliverable CSIT change on `feature/km-stage-integration`, unless the latest user request forbids deployment:

1. Review the diff and exclude credentials, runtime output, and local configuration.
2. Build and run the relevant regression tests in an isolated checkout while DEV is running.
3. Commit and push the reviewed source to the designated branch.
4. If the protected deployment automation changed, run `tools/dev-deploy/Update-DevWatcherAutomation.ps1` from the clean, pushed branch and verify its transaction evidence.
5. Run `tools/dev-deploy/Release-DevReady.ps1`. It repeats Build and the full test suite on the pushed commit, checks that the protected watcher automation matches the reviewed source, then pushes a `dev-ready-*` tag only on success.
6. The `CSIT DEV Ready Watcher` task performs the deployment. Wait for `C:\ProgramData\CSIT-DEV\state\current.json` to record the exact commit and check the deployment log and smoke results before reporting `DEV DEPLOY SUCCESS`.

The DEV deployment uses `LOCAL SERVICE`, `C:\ProgramData\CSIT-DEV`, and the DEV PostgreSQL database. Never run `run-site.ps1`, `run-site1.ps1`, or `Properties/PublishProfiles/FolderProfile.pubxml` for DEV. Never restart or modify Production 5010. A migration change stops automatic deployment for separate DEV DBA review.

Every AI releasing CSIT DEV must use the same `Release-DevReady.ps1` / protected `Deploy-DevReady.ps1` gate. Report `DEPLOY PASS` and `OIDC STRUCTURAL SMOKE PASS` only when the exact runtime SHA and both gates are recorded. HTTP 302 establishes only the OIDC redirect structure. Report `REAL LOGIN VERIFIED` only after an approved real DEV identity completes the callback, receives a CSIT cookie, and reaches a protected page on that release; otherwise report `REAL LOGIN NOT VERIFIED`. Never use a Production administrator account or silently create or bind a test identity.

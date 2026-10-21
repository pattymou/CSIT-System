# CSIT DEV delivery

For a completed, deliverable CSIT change on `feature/km-stage-integration`, unless the latest user request forbids deployment:

1. Review the diff and exclude credentials, runtime output, and local configuration.
2. Build and run the relevant regression tests in an isolated checkout while DEV is running.
3. Commit and push the reviewed source to the designated branch.
4. Run `tools/dev-deploy/Release-DevReady.ps1`. It repeats Build and the full test suite on the pushed commit, then pushes a `dev-ready-*` tag only on success.
5. The `CSIT DEV Ready Watcher` task performs the deployment. Wait for `C:\ProgramData\CSIT-DEV\state\current.json` to record the exact commit and check the deployment log and smoke results before reporting `DEV DEPLOY SUCCESS`.

The DEV deployment uses `LOCAL SERVICE`, `C:\ProgramData\CSIT-DEV`, and the DEV PostgreSQL database. Never run `run-site.ps1`, `run-site1.ps1`, or `Properties/PublishProfiles/FolderProfile.pubxml` for DEV. Never restart or modify Production 5010. A migration change stops automatic deployment for separate DEV DBA review.

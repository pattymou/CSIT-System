# CSIT Multi-User Load Test

`CSIT Multi-User Load Test` is an isolated k6 + ASP.NET Core + PostgreSQL 16 acceptance Gate. It never targets an arbitrary URL: the workload hard-codes `http://127.0.0.1:5088`, and application startup fails unless the database, cluster identity, storage marker, synthetic identities, disabled KM flags, and loopback host all match the current run.

## GitHub Actions

The workflow is intentionally separate from DEV Ready and the KM A-D Mock E2E. Its pull-request trigger is path-limited to the load-test system. After the workflow-only bootstrap is present on the default branch, use **Actions → CSIT Multi-User Load Test → Run workflow** and select the feature ref, user level, and workload.

- `all` runs 5, 20, and 50 users in order.
- `mixed` covers reads, stale-row edits, verification submissions, reservations and review races, PDF/PNG/JPG upload paths, supervisor approval/commit races, download/delete races, and KM-outbox invariants.
- `documents`, `reservations`, and `concurrency` provide smaller diagnostic reproductions.

Each level has warm-up, steady load, ramp-down, then a database/file consistency check. Latency is reported as a baseline (P50/P95/P99), not as an unsupported capacity promise. Missing operation metrics, failed checks, authorization bypass, 5xx responses, duplicate formal rows, orphan rows, malformed leases, or duplicate outbox business keys fail closed.

Artifacts contain the full release TRX, application logs, k6 summaries for each level, PostgreSQL metrics, invariant snapshots, and consolidated `report.md` / `report.json`. The ephemeral database and storage tree are removed after evidence is written. No LLM API, Shared Identity, real Anderson KM, DEV 7443, Production 5010, or formal NAS is used.

## Local safety

Local runs require PostgreSQL 16 on loopback and the exact independently verified cluster system identifier:

```powershell
tools/load-test/Invoke-CsitLoadTest.ps1 -RunId localabc123 -UserLevel 5 -Workload mixed `
  -PostgresPort 55435 -PostgresPassword '<enter outside source control>' `
  -ExpectedClusterSystemId '<verified isolated cluster id>'
```

Do not point this command at DEV or Production. The script refuses clusters containing DEV/Production-like database names and refuses non-loopback hosts.

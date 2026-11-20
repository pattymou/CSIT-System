# KM v2 manual reanalysis: DEV DBA review

Migration: `20261120045247_AddKmManualReanalysisAttempts`
Target: CSIT DEV PostgreSQL only. No KM, Shared Identity, or Production migration.

## Purpose and application behavior

An accepted KM v2 vendor or equipment attachment whose callback ended `Failed` may receive a new CSIT Outbox and EventId. The failed Outbox, original KM JobId, callback receipt, and prior error stay intact. A manual audit row links old and new Outboxes and records actor, original KM JobId, time, reason, and manual attempt number. The new Outbox starts `Pending`, with its own delivery retry counter. KM's v2 import contract accepts a new EventId after the prior canonical job is `Failed`, and its source/version deletion contract removes all related analysis jobs and index entries.

The API requires `AdminOnly` and `BusinessWrite`. It locks the source row, verifies a terminal failed callback, absence of active/completed same-version work and deletion jobs, and validates formal archive, approval evidence, actual file size and SHA256 before committing. The worker rechecks these gates before delivery. Requests use a client-generated ID for idempotency. Existing v1 sources remain unavailable.

## Schema changes to review

1. `km_integration_outbox`: nullable `last_error_retryable` stores KM callback's retryability. The unique `(source_system, document_id, version)` index becomes partial for `Pending`, `Dispatching`, `Sent`, `Processing`, `Completed`. Multiple historical `Failed` Outboxes can coexist with at most one active or completed attempt.
2. `ck_km_outbox_supported_event`: equipment EventId may differ from source file ID. The worker accepts that difference only when an audit row links this Outbox to a prior terminal failed KM job. First uploads retain the existing source ID rule in their factories.
3. `km_integration_manual_retry_audits`: nullable `new_outbox_id`, `previous_km_job_id`, and `manual_attempt_number`; unique index on nonnull `new_outbox_id`; check constraint permits `ManualReanalysis` only with all three fields and a prior `Failed` status. Existing `ManualKmSyncRetry` rows remain valid.

The index replacement briefly locks `km_integration_outbox`; schedule the reviewed DEV migration during a quiet period. Check active requests and disk capacity, and take a verified DEV database backup before applying. Do not run the new application binary against the old schema.

## Isolated PostgreSQL evidence (2026-11-20)

An independent PostgreSQL 16 cluster at `127.0.0.1:55432`, system identifier `7710088167196023704`, contained only test databases. A database created from baseline `13afa22d105b5f1d7fd98aab402cd2a2667ecf48` with `EnsureCreated` accepted the generated forward migration script. Inspection confirmed the partial unique index, new columns, and migration history ID. The generated Down script succeeded on this empty test database. On a separate integration database containing manual reanalysis history, the Down guard raised an exception and left the schema intact.

The PostgreSQL integration test created vendor and equipment archived files, dispatched initial jobs through a fake KM client, applied v2 Failed callbacks, created new Outboxes, verified same-request idempotency and active-work rejection, dispatched new EventIds, applied new Failed callbacks, rejected a changed on-disk SHA256, serialized two competing second attempts, applied a Completed callback to the winning equipment attempt, and checked both vendor and equipment deletion chose the latest dispatched EventId with `DocumentVersion` scope. Deletion blocked further reanalysis. No real KM job or DEV document was dispatched.

## DEV preflight and approval

- Review the exact pushed commit and generated migration SQL. Confirm the prior migration is `20261117055627_AddEquipmentAggregateDispatch` and the DEV database is `sit_department_system_dev`.
- Confirm backup restore point, DEV runtime SHA, current migration history, no unexpected duplicate active source/version rows, and no pending KM deletion/ingestion deployment conflict.
- A DBA applies this migration to DEV through the approved database change process, then verifies the new ID in `__EFMigrationsHistory`, index predicate, constraints, and a read-only queue query. CSIT must not execute the DEV migration without that approval.
- The protected review marker for the **exact commit SHA** is created by the existing `tools/dev-deploy/Approve-DevMigrationReview.ps1` procedure only after the migration is applied. The Ready Watcher requires the marker and applied migration before deployment.
- After approval, run `Release-DevReady.ps1`; verify Ready Watcher, exact runtime SHA, deployment log, and smoke. Do not trigger reanalysis on a real document during smoke.

## Rollback

Before any manual attempts exist, the generated Down migration can restore the original unique index and equipment EventId constraint, after confirming no duplicate `(source_system, document_id, version)` rows. The Down migration has a guard that refuses rollback when manual audit rows or duplicate source/version Outboxes exist. Once attempts exist, preserve audit and callbacks: stop writes, restore the reviewed pre-migration backup to an isolated database, reconcile new attempt history with DBA and KM owners, and use a reviewed forward repair or restore plan. Never delete failed jobs or audit records merely to force the old unique index.

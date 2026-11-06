# CSIT × KM v1.4.1 Task C — DBA migration review

## Gate and exact scope

- Migration: `20261106074818_AddKmTaskCIntegrationV141`
- Required predecessor: `20261106002529_AddKmEquipmentAttachmentEventV14`
- Review SQL: `docs/km-v141-task-c-migration.sql`
- SQL SHA256: `03E37F27D046C4920CC02D4BA9A6D5CC2378A298FB056E680512BE6F213E8763`
- Authorized target after separate DBA approval: `sit_department_system_dev` only.
- This package is review-only. It does not apply the migration, create an approval marker,
  deploy the application, or change DEV/Production data.

The exact pushed source commit must be supplied to the protected approval process after review.
A marker for another commit, migration, SQL hash, or database is invalid.

## Schema and data changes

The migration makes only the Task C changes below.

1. `document_files`
   - Adds required `document_id uuid` and `version integer` columns.
   - Adds nullable `supersedes_file_id uuid` with a restrictive self-reference.
   - Backfills every existing row to `document_id=id`, `version=1`, and leaves
     `supersedes_file_id` null before setting the two required columns `NOT NULL`.
   - Adds unique `(document_id, version)` and filtered unique `supersedes_file_id` indexes.
2. `km_metadata_changes`
   - Adds the durable metadata-change request, authorization evidence, canonical payload SHA256,
     retry/lease state, KM job correlation, source revision, and full JSONB snapshots.
   - Restricts sources to `TestReport` and `DocumentLibraryFile`, revisions to at least 2, and
     statuses to the Task C state machine.
   - Adds restrictive foreign keys to the immutable analysis result and verified review revision.
3. `km_metadata_change_callbacks`
   - Adds callback idempotency evidence keyed by `callback_id`, with applied revision, payload
     SHA256, status checks, and a restrictive FK to the metadata change.
4. `km_integration_outbox.ck_km_outbox_supported_event`
   - Keeps all existing source/event rules.
   - Changes only the Document Library branch so `source_entity_id` may identify the immutable
     physical version while `document_id + version` identify the logical document version.

There is no delete, attachment rewrite, historical Audit/Outbox rewrite, NAS operation, trigger,
Shared Identity change, Vendor/Equipment enablement, or Task D schema.

## Lock, compatibility, and capacity notes

- The `document_files` backfill updates all existing rows and the two unique indexes are created
  non-concurrently inside the migration transaction. Schedule a DEV maintenance window and review
  the row count and table/index size first.
- `ALTER COLUMN ... SET NOT NULL`, the self FK, and the recreated Outbox CHECK validate existing
  data and require table locks.
- Existing Document Library rows remain v1-compatible because their stable `document_id` is their
  existing physical `id` and their `version` is 1.
- The application build must not be activated before this migration is approved and recorded.
- `KmIntegration.Enabled`, Vendor, Equipment, deletion, and other KM rollout flags must remain
  disabled unless separately approved.

## DBA preflight and validation queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261106002529_AddKmEquipmentAttachmentEventV14',
  '20261106074818_AddKmTaskCIntegrationV141'
)
ORDER BY "MigrationId";

SELECT count(*) AS document_file_count
FROM document_files;

SELECT count(*) AS invalid_backfill_count
FROM document_files
WHERE document_id IS NULL OR version IS NULL OR version < 1;

SELECT to_regclass('public.km_metadata_changes'),
       to_regclass('public.km_metadata_change_callbacks');

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid IN (
  'document_files'::regclass,
  'km_metadata_changes'::regclass,
  'km_metadata_change_callbacks'::regclass,
  'km_integration_outbox'::regclass
)
AND (conname LIKE 'ck_km_metadata_%'
     OR conname = 'ck_km_outbox_supported_event'
     OR conname = 'FK_document_files_document_files_supersedes_file_id')
ORDER BY conname;

SELECT relname, n_live_tup
FROM pg_stat_user_tables
WHERE relname IN ('km_metadata_changes', 'km_metadata_change_callbacks')
ORDER BY relname;
```

Immediately after migration, both migration IDs must be present, `invalid_backfill_count` must be
zero, both new tables must resolve and contain zero business rows, and all constraints must match
the reviewed source and SQL.

## Isolated PostgreSQL 16 evidence (2026-11-06)

- A new cluster was bound only to `127.0.0.1:55432` with trust authentication and isolated test
  databases; DEV and Production databases were not used.
- The exact predecessor was prepared, two legacy `document_files` rows were inserted, and the
  reviewed SQL was applied with `psql -X -v ON_ERROR_STOP=1`.
- Backfill result was `2 total / 2 correctly_backfilled`; `document_id` and `version` were
  `NOT NULL`; both version indexes, both metadata tables, and exactly one Task C migration-history
  row were present.
- The Document Library PostgreSQL regression exercised migration Down/Up, legacy backfill, formal
  archive, concurrent worker claim, callbacks, retry, and expired lease.
- The TestReport PostgreSQL regression exercised the existing v1 lifecycle and v2 result storage.
- The metadata-change PostgreSQL regression exercised Verified review authorization, canonical
  payload integrity across `timestamptz` persistence, M2M 202 correlation, Processing/Completed,
  callback idempotency, and exact source revision.
- EF reported no pending model changes, and the migration Designer model body matched the
  `AppDbContextModelSnapshot` body.

This evidence is technical validation only. It is not DBA approval and does not authorize applying
the migration to DEV.

## Rollback notes

- `Down` drops both metadata-change tables and all their audit evidence, so it is destructive after
  Task C metadata changes begin.
- `Down` removes the Document Library version columns and cannot preserve a v2 version chain.
- Restoring the predecessor Outbox CHECK will fail if any Document Library v2 Outbox rows exist.
- After activation, prefer a reviewed forward fix or restore the verified pre-migration backup;
  do not run `Down` casually.

## Required DEV sequence after separate approval

1. Review the exact pushed commit, migration source, SQL, and SHA256 above.
2. Confirm DEV is on predecessor `20261106002529_AddKmEquipmentAttachmentEventV14`.
3. Record table sizes, take the normal DEV backup, and verify restore readiness.
4. Apply only `docs/km-v141-task-c-migration.sql` through the protected DBA credential flow.
5. Run the validation queries and confirm KM Global/Vendor/Equipment/Deletion flags remain false.
6. Create the exact-SHA marker only through the existing protected approval process.
7. Only then run the DEV Ready release/deployment flow and post-deploy smoke verification.

Production 5010, Shared Identity, Anderson KM, NAS history, and Task D are outside this review and
must not be modified.

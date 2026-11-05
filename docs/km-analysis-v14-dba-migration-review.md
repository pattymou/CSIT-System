# KM analysis v1.4 DBA migration review

## Gate and scope

- Migration: `20261105060913_AddKmDocumentAnalysisResultsV14`
- Required predecessor: `20261102075735_AddDocumentLibraryFileDeletionLifecycle`
- Review script: `docs/km-analysis-v14-migration.sql`
- Script SHA256: `E6FA126C2FCD8E46C36B7EAE4D4259AFAF0F6DDCFFB09A6E69C3D6FEC579E83B`
- Target after separate approval: `sit_department_system_dev` only.
- This package is review-only. It does not apply the migration, create an approval marker, or change DEV/Production data.

## Schema changes

- Creates `km_analysis_callback_receipts` for globally idempotent callback evidence.
- Creates `km_document_analysis_results` for immutable, source-correlated full JSONB analysis results.
- Creates `km_analysis_review_revisions` for append-only human corrections and review audit.
- Adds restrictive foreign keys to `km_integration_outbox`, callback receipts, superseded results, and review results.
- Adds unique constraints for analysis result IDs, callback IDs, source revisions, and review revisions.
- Adds source, document/version, outbox, and supersession lookup indexes.
- Adds check constraints for source type, schema version, status, review status, version/revision, and SHA256 lengths.

No existing table or column is altered. There is no data update, delete, backfill, Outbox rebuild, or NAS operation.

## Lock and compatibility considerations

- All three tables are new and empty, so table creation and index construction do not scan existing business rows.
- Foreign keys validate only new rows because the new tables start empty.
- Existing Test Report v1, Document Library v1 metadata, equipment metadata, Outbox rows, and approval records remain unchanged.
- The application build must not be activated before this migration is approved and recorded because the new callback/query paths use these tables.
- `KmIntegration.Enabled` and KM deletion flags remain unchanged and must stay disabled unless separately approved.

## DBA validation queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261102075735_AddDocumentLibraryFileDeletionLifecycle',
  '20261105060913_AddKmDocumentAnalysisResultsV14'
)
ORDER BY "MigrationId";

SELECT to_regclass('public.km_analysis_callback_receipts'),
       to_regclass('public.km_document_analysis_results'),
       to_regclass('public.km_analysis_review_revisions');

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid IN (
  'km_analysis_callback_receipts'::regclass,
  'km_document_analysis_results'::regclass,
  'km_analysis_review_revisions'::regclass
)
ORDER BY conname;

SELECT relname, n_live_tup
FROM pg_stat_user_tables
WHERE relname IN (
  'km_analysis_callback_receipts',
  'km_document_analysis_results',
  'km_analysis_review_revisions'
)
ORDER BY relname;
```

Expected immediately after migration: both migration IDs are present, all three tables resolve, constraints match the reviewed migration, and all three new tables contain zero business rows.

## Rollback notes

- EF `Down` drops only the three new tables, in dependency-safe order.
- `Down` becomes destructive after callbacks, analysis results, or reviews are received. Do not run it after real use without an explicit export and restore plan.
- Rollback does not touch Outbox, source documents, approval records, or NAS files.
- Prefer a reviewed forward fix after activation.

## Local isolated PostgreSQL verification (2026-11-05)

- PostgreSQL 16.13 was bound only to `127.0.0.1:55436`; no DEV connection string,
  runtime role, or `sit_department_system_dev` database was used.
- The baseline schema and migration history were generated from exact predecessor
  commit `741c0ef51ffdf7daaefd62b0cba4b7caed848419` through
  `20261102075735_AddDocumentLibraryFileDeletionLifecycle`.
- EF Up was applied in `csit_km_analysis_e2e_20261105c`. The schema diff contained
  exactly the three tables in this review, and the columns of all pre-existing tables
  were unchanged. All 20 new constraints and 12 indexes were present.
- A real PostgreSQL callback transaction stored the receipt, immutable result, and
  Outbox completion together. Identical replay was idempotent; a conflicting replay
  returned 409 without adding rows. A review revision preserved the original result
  JSON. Direct invalid writes produced the expected FK, CHECK, and UNIQUE violations.
- The idempotent SQL regenerated from EF was byte-identical to
  `docs/km-analysis-v14-migration.sql`; both SHA256 values were
  `E6FA126C2FCD8E46C36B7EAE4D4259AFAF0F6DDCFFB09A6E69C3D6FEC579E83B`.
- The reviewed SQL was separately applied with `psql -v ON_ERROR_STOP=1` to the exact
  predecessor baseline in `csit_km_analysis_e2e_20261105d`. The transaction committed,
  the migration history row was present, all three tables resolved, and each contained
  zero rows immediately after migration.
- This verification is technical evidence only. It does not constitute DBA approval,
  does not create the protected exact-SHA marker, and does not authorize applying the
  migration to DEV.

## Required DEV sequence

1. DBA reviews this file and the exact SQL hash and confirms the predecessor migration.
2. Take and verify the normal DEV database backup/snapshot.
3. Apply the reviewed SQL to `sit_department_system_dev` in a maintenance window.
4. Verify migration history, constraints, table counts, and the unchanged KM feature flags.
5. Create the protected exact-SHA marker only through the existing `Approve-DevMigrationReview.ps1` process.
6. Allow the DEV Ready Watcher to deploy and then run callback, ACL, and smoke verification.

Production, Shared Identity, NAS, Staging, Quarantine, and backup configuration are outside this migration and must not be modified.

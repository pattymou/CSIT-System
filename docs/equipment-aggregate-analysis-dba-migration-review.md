# Equipment aggregate analysis DBA migration review

## Gate and scope

- Migration: `20261116083516_AddEquipmentAggregateAnalysis`
- Required predecessor: `20261108070415_AddKmTaskDDocumentDeletionV141`
- Review script: `docs/equipment-aggregate-analysis-migration.sql`
- Script SHA256: `316D7569F2CDE6710AA513B092C0550DEBCA315F7445327701C1119382F3B699`
- Target after separate approval: `sit_department_system_dev` only.
- This package is review-only. It does not apply the migration, create an approval marker, or modify DEV/Production data.

## Schema changes

- Creates `equipment_aggregate_analysis_requests` for one idempotent request per equipment, manifest and analysis version.
- Creates `equipment_aggregate_analysis_snapshots` for immutable source manifest/result JSON plus human review state.
- Creates `equipment_aggregate_analysis_callbacks` for callback idempotency evidence.
- Adds restrictive foreign keys from snapshots and callback receipts to requests.
- Adds unique indexes for request identity and one snapshot per request, plus status/equipment lookup indexes.
- Adds check constraints for request status, review status and 64-character manifest hashes.

No existing table or column is altered. There is no data update, delete, backfill, Outbox rebuild, KM resend, or NAS operation.

## Lock and compatibility considerations

- All three tables are new and empty, so creation does not scan existing business rows.
- Foreign keys validate only new rows.
- `km_document_analysis_results` and the single-document v2 contract are unchanged.
- `KmEquipmentAggregateAnalysis:Enabled` defaults to `false` when absent. The application does not create aggregate requests or synthesize results while disabled.
- When the aggregate feature flag is disabled, equipment and reservation reads return before querying the new aggregate tables. The normal migration gate still prevents this migration-bearing release from activation until the migration is approved and recorded.

## DBA validation queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261108070415_AddKmTaskDDocumentDeletionV141',
  '20261116083516_AddEquipmentAggregateAnalysis'
)
ORDER BY "MigrationId";

SELECT to_regclass('public.equipment_aggregate_analysis_requests'),
       to_regclass('public.equipment_aggregate_analysis_snapshots'),
       to_regclass('public.equipment_aggregate_analysis_callbacks');

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid IN (
  'equipment_aggregate_analysis_requests'::regclass,
  'equipment_aggregate_analysis_snapshots'::regclass,
  'equipment_aggregate_analysis_callbacks'::regclass
)
ORDER BY conname;

SELECT relname, n_live_tup
FROM pg_stat_user_tables
WHERE relname LIKE 'equipment_aggregate_analysis_%'
ORDER BY relname;
```

Expected immediately after migration: both migration IDs exist, all three tables resolve, constraints match the reviewed migration, and every new table contains zero business rows.

## Rollback notes

- EF `Down` drops only the three new tables in dependency-safe order.
- `Down` becomes destructive after requests, callbacks, snapshots or reviews exist. Export those records first or use a reviewed forward fix after activation.
- Rollback does not touch equipment, reservations, single-file KM results, Outbox, documents, or NAS.

## Required DEV sequence

1. DBA reviews this file and the exact SQL hash and confirms the predecessor migration.
2. Take and verify the normal DEV database backup/snapshot.
3. Apply the reviewed SQL to `sit_department_system_dev` in a maintenance window.
4. Verify migration history, constraints, zero initial rows, and the disabled aggregate feature flag.
5. Create the protected exact-SHA marker only through `Approve-DevMigrationReview.ps1`.
6. Allow the DEV Ready Watcher to deploy, then run ACL, stale-manifest, reservation read and callback smoke checks.

Production, Shared Identity, KM Repository, NAS, Staging, Quarantine and backup configuration are outside this migration and must not be modified.

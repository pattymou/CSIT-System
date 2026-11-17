# Equipment aggregate dispatch DBA migration review

## Gate and scope

- Migration: `20261117055627_AddEquipmentAggregateDispatch`
- Required predecessor: `20261116083516_AddEquipmentAggregateAnalysis`
- Review script: `docs/equipment-aggregate-dispatch-migration.sql`
- Script SHA256: `1F81A0BEF3A46D0F1216120BAA36EAB146B849A6F652E09545FD7F965C3697E5`
- Target after separate approval: `sit_department_system_dev` only.
- This package is review-only. It does not apply the migration, create an approval marker, enable either feature flag, or modify DEV/Production data.

## Schema changes

The migration extends the existing `equipment_aggregate_analysis_requests` table; it does not create another Outbox.

- Adds dispatch status, attempt, retry, error, lease, accepted timestamp and KM Job ID columns.
- Existing rows receive `dispatch_status='Pending'` and `dispatch_attempt_count=0`.
- Adds claim/retry lookup indexes and a unique filtered KM Job ID index.
- Adds constraints requiring valid dispatch states, a complete lease pair only while dispatching, and a KM Job ID for accepted dispatches.

There is no document, callback, snapshot, single-file Outbox, NAS or KM data rewrite.

## Lock and compatibility considerations

- PostgreSQL must briefly lock `equipment_aggregate_analysis_requests` while adding columns, constraints and indexes. The table is expected to remain small because the aggregate feature is still disabled.
- `dispatch_status` is added with the safe `Pending` default so existing rows satisfy the new check constraint.
- `KmEquipmentAggregateAnalysis:Enabled` remains `false`; the release does not claim or dispatch aggregate requests while disabled.
- The migration-bearing release must not activate until this SQL is separately approved, applied to DEV, verified, and recorded through the protected exact-SHA marker flow.

## DBA validation queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261116083516_AddEquipmentAggregateAnalysis',
  '20261117055627_AddEquipmentAggregateDispatch'
)
ORDER BY "MigrationId";

SELECT column_name, data_type, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name = 'equipment_aggregate_analysis_requests'
  AND column_name LIKE 'dispatch_%'
ORDER BY ordinal_position;

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'equipment_aggregate_analysis_requests'::regclass
  AND conname LIKE 'ck_equipment_aggregate_dispatch_%'
ORDER BY conname;

SELECT status, dispatch_status, count(*)
FROM equipment_aggregate_analysis_requests
GROUP BY status, dispatch_status
ORDER BY status, dispatch_status;
```

Expected after migration with both feature flags OFF: both migration IDs exist, all new columns and three constraints are present, no row is `Dispatching`, and no outbound aggregate request is created.

## Rollback notes

- EF `Down` removes only the added aggregate dispatch indexes, constraints and columns.
- `Down` discards dispatch history and KM Job IDs after activation. Export the affected table first or prefer a reviewed forward fix.
- Rollback does not touch aggregate snapshots/callbacks, equipment, single-file KM results, the existing Outbox, documents or NAS.

## Required DEV sequence

1. DBA reviews this file and the exact SQL hash and confirms the predecessor migration.
2. Take and verify the normal DEV database backup/snapshot.
3. Apply the reviewed SQL to `sit_department_system_dev` in a maintenance window.
4. Run the validation queries and confirm `KmEquipmentAggregateAnalysis:Enabled=false`.
5. Create the protected exact-commit marker only through `Approve-DevMigrationReview.ps1`.
6. Allow the DEV Ready Watcher to deploy and run the Flag-OFF smoke tests.

Production, Shared Identity, KM Repository, NAS, Staging, Quarantine and backup configuration are outside this migration and must not be modified.

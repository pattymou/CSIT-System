# Equipment / Vendor migration DBA review

## Gate and scope

- Migration: `20261027080940_AddEquipmentVendorManagement`
- Required predecessor: `20261027061524_AddDocumentLibraryKmIntegrationV1`
- Script: `docs/equipment-vendor-migration.sql` (idempotent EF PostgreSQL script)
- This package is review-only. It has not been applied to DEV, Stage, Production, or any formal NAS.
- DocumentLibrary's existing DBA gate remains an independent prerequisite; this migration does not imply that it has been approved.

## Schema changes

- Creates `vendors` and `vendor_files`.
- Adds nullable `apparatus.vendor_id` with a restrictive foreign key to `vendors.id`.
- Adds nullable `apparatus.storage_folder_name`; existing apparatus records are intentionally not backfilled or guessed.
- Adds original-name, SHA256, archive state/retry, and reserved metadata columns to `apparatus_files`.
- Adds archive-state and SHA256 check constraints and retry lookup indexes.
- Adds case/whitespace-normalized application key `vendors.normalized_name`, unique archive folder key, and supporting indexes.
- Adds the enabled `/vendors` item to the existing `department` menu section when that code is not already present.

## Compatibility and data policy

- `apparatus.vendor_id` remains nullable for legacy data.
- `apparatus."Manufacturer"` remains unchanged and is not automatically mapped to a Vendor.
- New equipment is validated by the application against an enabled Vendor; historical links remain readable after Vendor deactivation.
- No existing equipment files are moved, renamed, deleted, or marked formally archived by this migration.
- Existing `apparatus_files.archive_status` remains `NULL`, which denotes legacy handling. New approved equipment files use explicit `Pending`, `Archived`, or `Failed` state.
- Vendor types are optional; when present they are limited to `Manufacturer`, `Agent`, `Supplier`, and `MaintenanceProvider`.

## Lock and runtime considerations

- `ALTER TABLE apparatus_files` and `ALTER TABLE apparatus` acquire PostgreSQL table locks. The added columns have no table-rewrite defaults except integer attempt count (`DEFAULT 0`); schedule during a low-write window.
- Unique indexes on newly created `vendors` are low risk because the table starts empty.
- The foreign key is added after `vendors` creation; all existing `vendor_id` values are `NULL`, so no legacy validation scan should fail.
- Application rollout must not precede DBA approval and successful migration because the new build queries the new tables/columns.

## DBA validation queries

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
WHERE "MigrationId" IN (
  '20261027061524_AddDocumentLibraryKmIntegrationV1',
  '20261027080940_AddEquipmentVendorManagement'
)
ORDER BY "MigrationId";

SELECT to_regclass('public.vendors'),
       to_regclass('public.vendor_files');

SELECT column_name, is_nullable, data_type
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name = 'apparatus'
  AND column_name IN ('vendor_id', 'storage_folder_name')
ORDER BY column_name;

SELECT conname, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conname IN (
  'FK_apparatus_vendors_vendor_id',
  'ck_apparatus_files_archive_status',
  'ck_apparatus_files_archive_result',
  'ck_vendor_files_archive_status',
  'ck_vendor_files_archive_result',
  'ck_vendors_types'
)
ORDER BY conname;

SELECT code, title, route_path, is_enabled
FROM menu_items
WHERE code = 'vendors';
```

Expected results: both migrations recorded, both new tables resolved, both apparatus columns present and nullable, all named constraints present, and at most one enabled `vendors` menu item.

## Application/NAS smoke checks after a separately approved deployment

1. Create a Vendor with no type, then verify case-insensitive/trimmed duplicate rejection.
2. Upload one PDF and one image using an isolated NAS root; verify SHA256 and `Archived` status.
3. Rename the Vendor; verify the persisted storage folder and downloads remain valid.
4. Create equipment with an enabled Vendor; verify `vendor_id` and the Vendor display name.
5. Submit equipment attachments and verify they remain staged/pending until supervisor approval.
6. Approve one attachment; verify formal path `設備儀器管理/設備資訊/<設備名稱>_<資產編號>/`, SHA256, and archive state.
7. Verify reservations, test environment membership, resource capabilities, RAW latest-package export, and other Asset module routes.

## Local isolated verification (2026-10-27)

- PostgreSQL 16 isolated cluster bound only to `127.0.0.1:55433`.
- Baseline schema/history was generated from exact predecessor commit `49efa4ffbb9e6a4952140cf2c9d3c9ddc657a227`; no DEV database was read or modified.
- `csit_equipment_vendor_e2e`: EF Up, real Vendor/file round-trip, unique/check constraints, EF Down, and EF Up again: PASS.
- `csit_equipment_vendor_sqlreview`: direct `psql -v ON_ERROR_STOP=1 -f docs/equipment-vendor-migration.sql`: PASS.
- Both databases verified `vendors`, `vendor_files`, migration history, and the dynamic `vendors` menu entry.
- The isolated server was stopped and its databases/data directory were removed after verification.
- Release build: PASS. Full regression suite: 421/421 PASS. One timing-sensitive KM cancellation test failed on the first full run, passed immediately in isolation, and the subsequent full run passed 421/421.

## Rollback notes

- The EF `Down` migration removes the menu item, foreign key, new tables, indexes, constraints, and columns. It is destructive to Vendor data and new archive metadata.
- Do not run `Down` after real Vendor/file data exists without an explicit export and file-retention plan.
- Database rollback does not delete or move NAS files. Any physical-file decision requires a separate reviewed runbook.

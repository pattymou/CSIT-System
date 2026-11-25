# Equipment aggregate controlled reanalysis: DEV DBA review

- Migration: `20261125010333_AddEquipmentAggregateReanalysisAttempts`
- Predecessor: `20261120045247_AddKmManualReanalysisAttempts`
- Target: `sit_department_system_dev` only; no KM, Shared Identity, or Production change.
- Generated forward SQL: `docs/equipment-aggregate-reanalysis-migration.sql`
- SQL SHA256: `7EB420CB3D4CEA5FFEA894A54E40B085E88A0D4CA7CBB778760D7A3532CE7EA2`

## Reason and behavior

The existing unique index permits one request per equipment, manifest hash, and analysis version. A real rerun of the unchanged, two-document manifest requires another request without changing the manifest or global analysis version. This migration adds `attempt_number` (existing rows become 1) and optional `requested_by`, and extends that unique index with `attempt_number`. No existing request, snapshot, callback, KM job, or source document is changed or removed.

The new `POST /api/equipment-aggregate-analysis/reanalyze` operation requires `AdminOnly` and `BusinessWrite`. It takes a client-generated request ID and expected current manifest hash. The service locks the equipment row, verifies both current source documents have completed single-file KM results, rejects a changed manifest or a concurrent pending request, and creates the next attempt. Repeating the same request ID is idempotent. The existing worker dispatches a new KM aggregate job and the existing callback creates a new snapshot. Reads show only the newest attempt for the current manifest; a pending or failed attempt does not present an earlier snapshot as the current result. The request records the actor. This endpoint does not resubmit OCR, Parser, or Embedding jobs.

## DEV read-only preflight (2026-11-25)

- Runtime SHA: `e85b97971497f205805edf1e9d6a966a0553df30`.
- Equipment 6666 (`A202611010810254176647`) has two active archived PDFs with matching completed single-file KM results: `Askey_SIT_ MT8821C  修改檔 V1.pdf` and `MT8821C_Product Brochure.pdf`.
- Current two-file manifest: `21aaf26f72fa8f2160472cad5c4c9edd7ee159909a5e7872bfb854422762cee9`.
- Latest completed request: `1771a4d7-a297-45bd-834f-f4980b4f54b4`; KM job `KM-EQAGG-000007`; snapshot `52baf1ba-2b55-48f0-8ffe-50b6c57cbad6`; 21 specifications, 0 conflicts, 65 evidence entries. The earlier 15-specification/7-conflict result remains historical.
- DEV migration history ends at the required predecessor. This review is not approval to apply the new migration.
- Protected preflight backup: `C:\ProgramData\CSIT-DEV\backups\equipment-aggregate-reanalysis-preflight-20261125T010749Z\sit_department_system_dev.custom.dump`; 371,759 bytes; SHA256 `CDA5190C3FBA69EC5D9A8C9DCD4FAE9EC63ACD9DE3C90E089DC99F4BD6B13A71`. `pg_restore --list` found 499 entries and full archive decode passed. Take or reconfirm a fresh restore point at application time if DEV data changes.

## Review, apply, and verify

1. Review the exact pushed commit and SQL hash. Confirm the current migration history and that no other deployment or aggregate job is active.
2. Take and verify a fresh DEV database backup immediately before application. The index replacement takes a table lock; perform it in a quiet window.
3. After explicit Owner/DEV DBA approval, apply the reviewed SQL to `sit_department_system_dev` using the existing DBA procedure. Do not activate the new binary against the old schema.
4. Confirm `__EFMigrationsHistory` contains the new ID, `attempt_number` is 1 for all historical requests, the check constraint exists, and the unique index keys include `attempt_number`. Confirm the current two-file manifest and historical request/snapshot counts remain intact.
5. Create the protected exact-commit marker with `tools/dev-deploy/Approve-DevMigrationReview.ps1`. Run `Release-DevReady.ps1`, then verify the Ready Watcher, runtime SHA, deployment log, and smoke before the authorized equipment 6666 rerun.

```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261125010333_AddEquipmentAggregateReanalysisAttempts';
SELECT attempt_number, count(*) FROM equipment_aggregate_analysis_requests
GROUP BY attempt_number ORDER BY attempt_number;
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid = 'equipment_aggregate_analysis_requests'::regclass
AND conname = 'ck_equipment_aggregate_request_attempt';
SELECT indexdef FROM pg_indexes WHERE tablename = 'equipment_aggregate_analysis_requests'
AND indexname = 'IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~';
```

## Rollback

The Down migration refuses to run after any attempt number above 1 exists, since the old unique index cannot represent two requests for the same manifest/version. Before a rerun, Down can restore the original index. After a rerun, preserve history and use a reviewed forward repair or restore and reconcile from the DEV backup. Never delete aggregate requests, callbacks, or snapshots to force rollback.

# CSIT × AI KM v1.4 Task B — DEV DBA migration review

## Exact review scope

- Migration: `20261106002529_AddKmEquipmentAttachmentEventV14`
- SQL: `docs/km-v14-task-b-migration.sql`
- SQL SHA256: `6E4D7708CA218AC4FCB6AC38C710B973D4823E6DE7CE52B026E7835B8055E7CA`
- Required predecessor: `20261105060913_AddKmDocumentAnalysisResultsV14`
- Authorized target after separate approval: `sit_department_system_dev` only

The source commit SHA must be supplied in the separate approval after the reviewed source is
committed and pushed. A marker for another commit, SQL hash, migration, or database is invalid.

## Schema impact

This migration changes one existing CHECK constraint only:

- Drops `km_integration_outbox.ck_km_outbox_supported_event`.
- Recreates the same constraint with one additional allowed equipment event,
  `EquipmentAttachmentArchived`.
- The new event must still use `source_entity_type='ApparatusFile'`,
  `source_entity_id=document_id=event_id`, `version=1`, and null project/case/task IDs.

There are no new tables, columns, indexes, foreign keys, triggers, data updates, or backfills.
All existing allowed events and invariants remain unchanged. The ADD CHECK validates existing
Outbox rows and needs a brief table lock; apply it in the normal DEV maintenance procedure.

## Safety and rollback

- The SQL uses a native transaction and records the exact EF migration history row.
- Run with `psql -X -v ON_ERROR_STOP=1` through the protected DBA credential flow.
- Do not disable the CHECK, FK, or triggers and do not use the runtime role for DDL.
- The Up constraint is a strict superset of the predecessor, so conforming existing rows remain valid.
- Down restores the old constraint and will fail after any `EquipmentAttachmentArchived` row exists.
  Do not treat Down as a safe rollback after image Outbox creation; disable dispatch and use a
  reviewed forward fix or restore the pre-migration backup.

## Isolated PostgreSQL evidence

PostgreSQL 16 was bound only to `127.0.0.1:55432`; neither the DEV nor Production database was
used for destructive testing.

- Current-model PostgreSQL Worker claim test dispatched three independent v2 rows:
  VerificationApplicationAttachment, VendorFile, and an EquipmentAttachment PNG.
- The generated SQL was applied with `ON_ERROR_STOP=1` to a predecessor-constraint clone.
- Outbox row count remained `2 -> 2` across the migration.
- The exact migration history row was inserted once.
- `pg_get_constraintdef` confirmed `EquipmentAttachmentArchived` is accepted by the recreated CHECK.
- EF reports no pending model changes after this migration.

## Read-only DEV dry-run (no backfill)

The read-only query ran as `csit_runtime_dev` against `sit_department_system_dev`.

- Archived equipment images without an Outbox: `1`.
- Safe to backfill with existing Approved/ApprovalBypassed evidence: `0`.
- Blocked because approval evidence is missing: `1`.
- Blocked source ID: `2941659b-58c2-4932-b251-0ee9a5c1df04`
  (`A202611010810254176647`, `系統架構圖.png`).
- No Outbox row was created or changed.

The existing Excel event remains unchanged and Pending:

- Event ID: `b76d532e-9847-42cf-911b-4084841bace5`
- Outbox ID: `f42f6e44-c9a0-4e45-b934-64a40f59e292`
- Event type: `VerificationApplicationAttachmentArchived`
- File: `01_AI_KM_Phase規劃_v2.7.xlsx`

## Required DEV sequence

1. Review the exact pushed commit, migration, SQL, and SHA256 above.
2. Confirm DEV is still on predecessor migration `20261105060913_AddKmDocumentAnalysisResultsV14`.
3. Take and verify the normal DEV backup.
4. Apply only `docs/km-v14-task-b-migration.sql` to `sit_department_system_dev`.
5. Verify the CHECK definition, migration history, unchanged Outbox row counts, and disabled KM flags.
6. Create the exact-SHA marker using the existing protected review process.
7. Only then allow the DEV Ready deployment. Keep global KM, Vendor, Equipment, and deletion
   dispatch flags disabled pending real KM E2E acceptance.

Production, Shared Identity, NAS, Staging, Quarantine, and existing backups are outside this
migration and must not be modified.

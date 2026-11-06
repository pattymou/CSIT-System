START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106002529_AddKmEquipmentAttachmentEventV14') THEN
    ALTER TABLE km_integration_outbox DROP CONSTRAINT ck_km_outbox_supported_event;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106002529_AddKmEquipmentAttachmentEventV14') THEN
    ALTER TABLE km_integration_outbox ADD CONSTRAINT ck_km_outbox_supported_event CHECK (source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106002529_AddKmEquipmentAttachmentEventV14') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261106002529_AddKmEquipmentAttachmentEventV14', '10.0.4');
    END IF;
END $EF$;
COMMIT;


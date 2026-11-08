START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    ALTER TABLE equipment_file_deletion_jobs DROP CONSTRAINT ck_equipment_file_deletion_identity;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    ALTER TABLE equipment_file_deletion_jobs ADD canonical_ingestion_event_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    ALTER TABLE equipment_file_deletion_jobs ADD reason_code character varying(40);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    UPDATE equipment_file_deletion_jobs AS deletion
    SET source_entity_type = 'EquipmentAttachment',
        reason_code = 'SourceDeleted',
        canonical_ingestion_event_id = ingestion.event_id
    FROM km_integration_outbox AS ingestion
    WHERE ingestion.source_entity_id = deletion.apparatus_file_id
      AND ingestion.source_entity_type = 'ApparatusFile'
      AND ingestion.event_type IN ('EquipmentDocumentArchived','EquipmentAttachmentArchived');

    UPDATE equipment_file_deletion_jobs
    SET source_entity_type = 'EquipmentAttachment',
        reason_code = 'SourceDeleted'
    WHERE reason_code IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    ALTER TABLE equipment_file_deletion_jobs ALTER COLUMN reason_code SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE TABLE km_v2_document_deletion_callbacks (
        callback_id uuid NOT NULL,
        deletion_request_id uuid NOT NULL,
        source_type character varying(60) NOT NULL,
        status character varying(30) NOT NULL,
        payload_sha256 character varying(64) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_km_v2_document_deletion_callbacks" PRIMARY KEY (callback_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE TABLE vendor_file_deletion_jobs (
        id uuid NOT NULL,
        vendor_file_id uuid NOT NULL,
        document_id uuid NOT NULL,
        version integer NOT NULL,
        vendor_id uuid NOT NULL,
        source_type character varying(60) NOT NULL,
        file_name character varying(300) NOT NULL,
        sha256 character varying(64),
        reason_code character varying(40) NOT NULL,
        canonical_ingestion_event_id uuid,
        requested_by character varying(100) NOT NULL,
        requested_at timestamp with time zone NOT NULL,
        status character varying(30) NOT NULL,
        km_deletion_job_id character varying(150),
        attempt_count integer NOT NULL,
        last_error_code character varying(100),
        last_error_message character varying(500),
        last_error_retryable boolean,
        lease_token uuid,
        lease_expires_at timestamp with time zone,
        km_verified_at timestamp with time zone,
        db_record_deleted_at timestamp with time zone,
        physical_file_deleted_at timestamp with time zone,
        completed_at timestamp with time zone,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_vendor_file_deletion_jobs" PRIMARY KEY (id),
        CONSTRAINT ck_vendor_file_deletion_identity CHECK (source_type = 'VendorFile' AND vendor_file_id = document_id AND version = 1 AND reason_code IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup')),
        CONSTRAINT ck_vendor_file_deletion_status CHECK (status IN ('Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    INSERT INTO km_v2_document_deletion_callbacks
        (callback_id, deletion_request_id, source_type, status, payload_sha256, received_at)
    SELECT callback_id, deletion_job_id, 'EquipmentAttachment', status,
           payload_sha256, received_at
    FROM equipment_file_deletion_callbacks
    ON CONFLICT (callback_id) DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    ALTER TABLE equipment_file_deletion_jobs ADD CONSTRAINT ck_equipment_file_deletion_identity CHECK (source_entity_type = 'EquipmentAttachment' AND apparatus_file_id = document_id AND version = 1 AND reason_code IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE INDEX "IX_km_v2_document_deletion_callbacks_source_type_deletion_requ~" ON km_v2_document_deletion_callbacks (source_type, deletion_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE UNIQUE INDEX "IX_vendor_file_deletion_jobs_km_deletion_job_id" ON vendor_file_deletion_jobs (km_deletion_job_id) WHERE km_deletion_job_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE INDEX "IX_vendor_file_deletion_jobs_status_updated_at" ON vendor_file_deletion_jobs (status, updated_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    CREATE UNIQUE INDEX "IX_vendor_file_deletion_jobs_vendor_file_id" ON vendor_file_deletion_jobs (vendor_file_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108070415_AddKmTaskDDocumentDeletionV141') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261108070415_AddKmTaskDDocumentDeletionV141', '10.0.4');
    END IF;
END $EF$;
COMMIT;

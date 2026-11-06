START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE km_integration_outbox DROP CONSTRAINT ck_km_outbox_supported_event;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ADD document_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ADD supersedes_file_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ADD version integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    UPDATE document_files
    SET document_id = id,
        version = 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ALTER COLUMN document_id SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ALTER COLUMN version SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE TABLE km_metadata_changes (
        change_id uuid NOT NULL,
        analysis_result_id uuid NOT NULL,
        review_revision_id uuid NOT NULL,
        source_system character varying(30) NOT NULL,
        source_type character varying(80) NOT NULL,
        source_entity_id uuid NOT NULL,
        document_id uuid NOT NULL,
        version integer NOT NULL,
        source_revision integer NOT NULL,
        classification_json jsonb NOT NULL,
        attributes_json jsonb NOT NULL,
        changed_fields_json jsonb NOT NULL,
        payload_sha256 char(64) NOT NULL,
        status character varying(30) NOT NULL,
        km_change_job_id character varying(150),
        retry_count integer NOT NULL,
        next_retry_at timestamp with time zone,
        last_error_code character varying(100),
        last_error_message character varying(500),
        last_error_retryable boolean,
        lease_token uuid,
        lease_expires_at timestamp with time zone,
        occurred_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        sent_at timestamp with time zone,
        completed_at timestamp with time zone,
        authorized_by character varying(100) NOT NULL,
        authorized_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_km_metadata_changes" PRIMARY KEY (change_id),
        CONSTRAINT ck_km_metadata_change_payload_sha CHECK (length(payload_sha256) = 64),
        CONSTRAINT ck_km_metadata_change_sent_job CHECK (status NOT IN ('Sent','Processing','Completed') OR km_change_job_id IS NOT NULL),
        CONSTRAINT ck_km_metadata_change_source CHECK (source_system = 'CSIT' AND source_type IN ('TestReport','DocumentLibraryFile')),
        CONSTRAINT ck_km_metadata_change_status CHECK (status IN ('Pending','Dispatching','Sent','Processing','Completed','Failed')),
        CONSTRAINT ck_km_metadata_change_version CHECK (version >= 1 AND source_revision >= 2),
        CONSTRAINT "FK_km_metadata_changes_km_analysis_review_revisions_review_rev~" FOREIGN KEY (review_revision_id) REFERENCES km_analysis_review_revisions (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_km_metadata_changes_km_document_analysis_results_analysis_r~" FOREIGN KEY (analysis_result_id) REFERENCES km_document_analysis_results (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE TABLE km_metadata_change_callbacks (
        callback_id uuid NOT NULL,
        change_id uuid NOT NULL,
        status character varying(30) NOT NULL,
        applied_revision integer NOT NULL,
        payload_sha256 char(64) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_km_metadata_change_callbacks" PRIMARY KEY (callback_id),
        CONSTRAINT ck_km_metadata_callback_revision CHECK (applied_revision >= 0),
        CONSTRAINT ck_km_metadata_callback_sha CHECK (length(payload_sha256) = 64),
        CONSTRAINT ck_km_metadata_callback_status CHECK (status IN ('Processing','Completed','Failed')),
        CONSTRAINT "FK_km_metadata_change_callbacks_km_metadata_changes_change_id" FOREIGN KEY (change_id) REFERENCES km_metadata_changes (change_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE km_integration_outbox ADD CONSTRAINT ck_km_outbox_supported_event CHECK (source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE UNIQUE INDEX "IX_document_files_document_id_version" ON document_files (document_id, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE UNIQUE INDEX "IX_document_files_supersedes_file_id" ON document_files (supersedes_file_id) WHERE supersedes_file_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE INDEX "IX_km_metadata_change_callbacks_change_id" ON km_metadata_change_callbacks (change_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE INDEX "IX_km_metadata_changes_analysis_result_id" ON km_metadata_changes (analysis_result_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE UNIQUE INDEX "IX_km_metadata_changes_km_change_job_id" ON km_metadata_changes (km_change_job_id) WHERE km_change_job_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE INDEX "IX_km_metadata_changes_review_revision_id" ON km_metadata_changes (review_revision_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE UNIQUE INDEX "IX_km_metadata_changes_source_type_source_entity_id_document_i~" ON km_metadata_changes (source_type, source_entity_id, document_id, version, source_revision);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE INDEX "IX_km_metadata_changes_status_lease_expires_at" ON km_metadata_changes (status, lease_expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    CREATE INDEX "IX_km_metadata_changes_status_next_retry_at" ON km_metadata_changes (status, next_retry_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    ALTER TABLE document_files ADD CONSTRAINT "FK_document_files_document_files_supersedes_file_id" FOREIGN KEY (supersedes_file_id) REFERENCES document_files (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261106074818_AddKmTaskCIntegrationV141') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261106074818_AddKmTaskCIntegrationV141', '10.0.4');
    END IF;
END $EF$;
COMMIT;

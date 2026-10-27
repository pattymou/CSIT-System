START TRANSACTION;
ALTER TABLE km_integration_outbox DROP CONSTRAINT ck_km_outbox_supported_event;

ALTER TABLE document_processing_jobs DROP CONSTRAINT ck_document_processing_jobs_status;

ALTER TABLE document_processing_jobs ADD archive_attempt_count integer NOT NULL DEFAULT 0;

ALTER TABLE document_processing_jobs ADD archive_last_attempt_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD archive_last_error character varying(2000);

ALTER TABLE document_processing_jobs ADD archive_lease_expires_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD archive_lease_token uuid;

ALTER TABLE document_processing_jobs ADD archive_next_retry_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD archive_sha256 character varying(64);

ALTER TABLE document_processing_jobs ADD archive_status character varying(30) NOT NULL DEFAULT 'NotStarted';

ALTER TABLE document_processing_jobs ADD archived_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD km_completed_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD km_error_code character varying(100);

ALTER TABLE document_processing_jobs ADD km_error_message character varying(2000);

ALTER TABLE document_processing_jobs ADD km_error_retryable boolean;

ALTER TABLE document_processing_jobs ADD km_failed_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD km_started_at timestamp with time zone;

ALTER TABLE document_processing_jobs ADD metadata_json jsonb;

CREATE TABLE document_processing_callbacks (
    callback_id uuid NOT NULL,
    document_processing_job_id uuid NOT NULL,
    status character varying(30) NOT NULL,
    payload_sha256 character varying(64) NOT NULL,
    received_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_document_processing_callbacks" PRIMARY KEY (callback_id),
    CONSTRAINT ck_document_processing_callbacks_status CHECK (status IN ('Processing', 'Completed', 'Failed')),
    CONSTRAINT fk_document_callbacks_job FOREIGN KEY (document_processing_job_id) REFERENCES document_processing_jobs (id) ON DELETE CASCADE
);

ALTER TABLE km_integration_outbox ADD CONSTRAINT ck_km_outbox_supported_event CHECK (source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL)));

CREATE INDEX ix_document_jobs_archive_claim ON document_processing_jobs (archive_status, archive_lease_expires_at);

CREATE INDEX ix_document_jobs_archive_retry ON document_processing_jobs (status, archive_status, archive_next_retry_at);

ALTER TABLE document_processing_jobs ADD CONSTRAINT ck_document_processing_jobs_archive_result CHECK (archive_status <> 'Completed' OR (archived_at IS NOT NULL AND archive_sha256 IS NOT NULL));

ALTER TABLE document_processing_jobs ADD CONSTRAINT ck_document_processing_jobs_archive_status CHECK (archive_status IN ('NotStarted', 'Pending', 'Archiving', 'Completed', 'Failed'));

ALTER TABLE document_processing_jobs ADD CONSTRAINT ck_document_processing_jobs_metadata CHECK (status <> 'Completed' OR metadata_json IS NOT NULL OR (archive_status = 'NotStarted' AND km_completed_at IS NULL));

ALTER TABLE document_processing_jobs ADD CONSTRAINT ck_document_processing_jobs_status CHECK (status IN ('Pending', 'Accepted', 'Processing', 'Completed', 'Failed'));

CREATE INDEX ix_document_callbacks_job_received ON document_processing_callbacks (document_processing_job_id, received_at);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027061524_AddDocumentLibraryKmIntegrationV1', '10.0.4');

COMMIT;

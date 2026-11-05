START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE TABLE km_analysis_callback_receipts (
        callback_id uuid NOT NULL,
        outbox_id uuid NOT NULL,
        endpoint character varying(80) NOT NULL,
        status character varying(30) NOT NULL,
        payload_sha256 char(64) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_km_analysis_callback_receipts" PRIMARY KEY (callback_id),
        CONSTRAINT ck_km_analysis_callback_sha256 CHECK (length(payload_sha256) = 64),
        CONSTRAINT ck_km_analysis_callback_status CHECK (status IN ('Processing','Completed','Failed')),
        CONSTRAINT "FK_km_analysis_callback_receipts_km_integration_outbox_outbox_~" FOREIGN KEY (outbox_id) REFERENCES km_integration_outbox (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE TABLE km_document_analysis_results (
        id uuid NOT NULL,
        analysis_result_id uuid NOT NULL,
        callback_id uuid NOT NULL,
        outbox_id uuid NOT NULL,
        source_type character varying(80) NOT NULL,
        source_entity_id uuid NOT NULL,
        document_id uuid NOT NULL,
        version integer NOT NULL,
        event_id uuid NOT NULL,
        csit_job_id uuid NOT NULL,
        km_job_id character varying(150) NOT NULL,
        source_sha256 char(64) NOT NULL,
        metadata_schema_version character varying(12) NOT NULL,
        status character varying(30) NOT NULL,
        result_json jsonb NOT NULL,
        analyzed_at timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        indexed_at timestamp with time zone NOT NULL,
        review_status character varying(30) NOT NULL,
        reviewed_by character varying(100),
        reviewed_at timestamp with time zone,
        supersedes_result_id uuid,
        superseded_at timestamp with time zone,
        source_revision integer NOT NULL,
        callback_payload_sha256 char(64) NOT NULL,
        CONSTRAINT "PK_km_document_analysis_results" PRIMARY KEY (id),
        CONSTRAINT ck_km_analysis_payload_sha256 CHECK (length(callback_payload_sha256) = 64),
        CONSTRAINT ck_km_analysis_review_status CHECK (review_status IN ('PendingVerification','Verified','Rejected')),
        CONSTRAINT ck_km_analysis_schema_version CHECK (metadata_schema_version IN ('1.0','2.0')),
        CONSTRAINT ck_km_analysis_source_revision CHECK (source_revision >= 1),
        CONSTRAINT ck_km_analysis_source_sha256 CHECK (length(source_sha256) = 64),
        CONSTRAINT ck_km_analysis_source_type CHECK (source_type IN ('TestReport','DocumentLibraryFile','VerificationApplicationAttachment','VendorFile','EquipmentAttachment')),
        CONSTRAINT ck_km_analysis_status CHECK (status = 'Completed'),
        CONSTRAINT ck_km_analysis_version CHECK (version >= 1),
        CONSTRAINT "FK_km_document_analysis_results_km_analysis_callback_receipts_~" FOREIGN KEY (callback_id) REFERENCES km_analysis_callback_receipts (callback_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_km_document_analysis_results_km_document_analysis_results_s~" FOREIGN KEY (supersedes_result_id) REFERENCES km_document_analysis_results (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_km_document_analysis_results_km_integration_outbox_outbox_id" FOREIGN KEY (outbox_id) REFERENCES km_integration_outbox (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE TABLE km_analysis_review_revisions (
        id uuid NOT NULL,
        analysis_result_id uuid NOT NULL,
        revision integer NOT NULL,
        decision character varying(30) NOT NULL,
        corrections_json jsonb,
        reason character varying(2000),
        reviewed_by character varying(100) NOT NULL,
        reviewed_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_km_analysis_review_revisions" PRIMARY KEY (id),
        CONSTRAINT ck_km_analysis_review_decision CHECK (decision IN ('Verified','Rejected')),
        CONSTRAINT ck_km_analysis_review_revision CHECK (revision >= 1),
        CONSTRAINT "FK_km_analysis_review_revisions_km_document_analysis_results_a~" FOREIGN KEY (analysis_result_id) REFERENCES km_document_analysis_results (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE INDEX "IX_km_analysis_callback_receipts_outbox_id" ON km_analysis_callback_receipts (outbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE UNIQUE INDEX "IX_km_analysis_review_revisions_analysis_result_id_revision" ON km_analysis_review_revisions (analysis_result_id, revision);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE UNIQUE INDEX "IX_km_document_analysis_results_analysis_result_id" ON km_document_analysis_results (analysis_result_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE UNIQUE INDEX "IX_km_document_analysis_results_callback_id" ON km_document_analysis_results (callback_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE INDEX "IX_km_document_analysis_results_document_id_version" ON km_document_analysis_results (document_id, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE INDEX "IX_km_document_analysis_results_outbox_id" ON km_document_analysis_results (outbox_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE INDEX "IX_km_document_analysis_results_source_type_source_entity_id" ON km_document_analysis_results (source_type, source_entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE UNIQUE INDEX "IX_km_document_analysis_results_source_type_source_entity_id_d~" ON km_document_analysis_results (source_type, source_entity_id, document_id, version, source_revision);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    CREATE INDEX "IX_km_document_analysis_results_supersedes_result_id" ON km_document_analysis_results (supersedes_result_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105060913_AddKmDocumentAnalysisResultsV14') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261105060913_AddKmDocumentAnalysisResultsV14', '10.0.4');
    END IF;
END $EF$;
COMMIT;


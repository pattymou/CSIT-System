START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE TABLE equipment_aggregate_analysis_requests (
        id uuid NOT NULL,
        apparatus_id character varying(30) NOT NULL,
        module_code character varying(50) NOT NULL,
        manifest_hash character varying(64) NOT NULL,
        analysis_version character varying(30) NOT NULL,
        source_documents_json jsonb NOT NULL,
        status character varying(30) NOT NULL,
        requested_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone,
        failure_code character varying(100),
        failure_message character varying(1000),
        CONSTRAINT "PK_equipment_aggregate_analysis_requests" PRIMARY KEY (id),
        CONSTRAINT ck_equipment_aggregate_request_manifest CHECK (length(manifest_hash) = 64),
        CONSTRAINT ck_equipment_aggregate_request_status CHECK (status IN ('Pending','Completed','Failed','Superseded'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE TABLE equipment_aggregate_analysis_callbacks (
        callback_id uuid NOT NULL,
        request_id uuid NOT NULL,
        payload_sha256 character varying(64) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_equipment_aggregate_analysis_callbacks" PRIMARY KEY (callback_id),
        CONSTRAINT "FK_equipment_aggregate_analysis_callbacks_equipment_aggregate_~" FOREIGN KEY (request_id) REFERENCES equipment_aggregate_analysis_requests (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE TABLE equipment_aggregate_analysis_snapshots (
        id uuid NOT NULL,
        request_id uuid NOT NULL,
        apparatus_id character varying(30) NOT NULL,
        module_code character varying(50) NOT NULL,
        manifest_hash character varying(64) NOT NULL,
        analysis_version character varying(30) NOT NULL,
        source_documents_json jsonb NOT NULL,
        result_json jsonb NOT NULL,
        analyzed_at timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        review_status character varying(30) NOT NULL,
        reviewed_by character varying(100),
        reviewed_at timestamp with time zone,
        review_note character varying(2000),
        CONSTRAINT "PK_equipment_aggregate_analysis_snapshots" PRIMARY KEY (id),
        CONSTRAINT ck_equipment_aggregate_snapshot_manifest CHECK (length(manifest_hash) = 64),
        CONSTRAINT ck_equipment_aggregate_snapshot_review CHECK (review_status IN ('PendingVerification','Verified','Rejected')),
        CONSTRAINT "FK_equipment_aggregate_analysis_snapshots_equipment_aggregate_~" FOREIGN KEY (request_id) REFERENCES equipment_aggregate_analysis_requests (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE INDEX "IX_equipment_aggregate_analysis_callbacks_request_id" ON equipment_aggregate_analysis_callbacks (request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE UNIQUE INDEX "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~" ON equipment_aggregate_analysis_requests (apparatus_id, module_code, manifest_hash, analysis_version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE INDEX "IX_equipment_aggregate_analysis_requests_status_updated_at" ON equipment_aggregate_analysis_requests (status, updated_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE INDEX "IX_equipment_aggregate_analysis_snapshots_apparatus_id_module_~" ON equipment_aggregate_analysis_snapshots (apparatus_id, module_code, received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    CREATE UNIQUE INDEX "IX_equipment_aggregate_analysis_snapshots_request_id" ON equipment_aggregate_analysis_snapshots (request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116083516_AddEquipmentAggregateAnalysis') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116083516_AddEquipmentAggregateAnalysis', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_attempt_count integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_last_attempt_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_last_error_code character varying(100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_last_error_message character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_lease_expires_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_lease_token uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_next_retry_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatch_status character varying(30) NOT NULL DEFAULT 'Pending';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD dispatched_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD km_job_id character varying(150);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    CREATE INDEX "IX_equipment_aggregate_analysis_requests_dispatch_lease_expire~" ON equipment_aggregate_analysis_requests (dispatch_lease_expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    CREATE UNIQUE INDEX "IX_equipment_aggregate_analysis_requests_km_job_id" ON equipment_aggregate_analysis_requests (km_job_id) WHERE km_job_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    CREATE INDEX "IX_equipment_aggregate_analysis_requests_status_dispatch_statu~" ON equipment_aggregate_analysis_requests (status, dispatch_status, dispatch_next_retry_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD CONSTRAINT ck_equipment_aggregate_dispatch_job CHECK (dispatch_status <> 'Accepted' OR km_job_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD CONSTRAINT ck_equipment_aggregate_dispatch_lease CHECK ((dispatch_status = 'Dispatching') = (dispatch_lease_token IS NOT NULL AND dispatch_lease_expires_at IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    ALTER TABLE equipment_aggregate_analysis_requests ADD CONSTRAINT ck_equipment_aggregate_dispatch_status CHECK (dispatch_status IN ('Pending','Dispatching','Accepted','RetryWaiting','Deferred','Failed','Superseded'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261117055627_AddEquipmentAggregateDispatch') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261117055627_AddEquipmentAggregateDispatch', '10.0.4');
    END IF;
END $EF$;
COMMIT;

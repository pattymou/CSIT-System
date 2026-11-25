START TRANSACTION;
DROP INDEX "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~";

ALTER TABLE equipment_aggregate_analysis_requests ADD attempt_number integer NOT NULL DEFAULT 1;

ALTER TABLE equipment_aggregate_analysis_requests ADD requested_by character varying(100);

ALTER TABLE equipment_aggregate_analysis_requests ADD CONSTRAINT ck_equipment_aggregate_request_attempt CHECK (attempt_number >= 1);

CREATE UNIQUE INDEX "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~" ON equipment_aggregate_analysis_requests (apparatus_id, module_code, manifest_hash, analysis_version, attempt_number);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261125010333_AddEquipmentAggregateReanalysisAttempts', '10.0.4');

COMMIT;

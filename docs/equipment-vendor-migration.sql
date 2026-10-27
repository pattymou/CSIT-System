START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD archive_attempt_count integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD archive_error character varying(2000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD archive_next_retry_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD archive_status character varying(30);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD archive_updated_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD metadata_json jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD metadata_source character varying(100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD metadata_verification_status character varying(30);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD metadata_version character varying(100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD original_file_name character varying(300);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD sha256 character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus ADD storage_folder_name character varying(240);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus ADD vendor_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE TABLE vendors (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        normalized_name character varying(200) NOT NULL,
        aliases character varying(500),
        types text[] NOT NULL,
        website character varying(500),
        contact_person character varying(200),
        phone character varying(100),
        email character varying(320),
        address character varying(1000),
        notes character varying(4000),
        is_active boolean NOT NULL DEFAULT TRUE,
        storage_folder_name character varying(240) NOT NULL,
        created_by character varying(200) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_by character varying(200) NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_vendors" PRIMARY KEY (id),
        CONSTRAINT ck_vendors_types CHECK (types <@ ARRAY['Manufacturer','Agent','Supplier','MaintenanceProvider']::text[])
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE TABLE vendor_files (
        id uuid NOT NULL,
        vendor_id uuid NOT NULL,
        original_file_name character varying(300) NOT NULL,
        stored_file_name character varying(300) NOT NULL,
        folder_path character varying(1000) NOT NULL,
        content_type character varying(200),
        file_size bigint NOT NULL,
        sha256 character varying(64),
        archive_status character varying(30) NOT NULL,
        archive_error character varying(2000),
        archive_attempt_count integer NOT NULL DEFAULT 0,
        archive_next_retry_at timestamp with time zone,
        archive_updated_at timestamp with time zone NOT NULL,
        uploaded_by character varying(200) NOT NULL,
        uploaded_at timestamp with time zone NOT NULL,
        metadata_json jsonb,
        metadata_source character varying(100),
        metadata_version character varying(100),
        metadata_verification_status character varying(30) NOT NULL,
        CONSTRAINT "PK_vendor_files" PRIMARY KEY (id),
        CONSTRAINT ck_vendor_files_archive_result CHECK (archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64)),
        CONSTRAINT ck_vendor_files_archive_status CHECK (archive_status IN ('Pending','Archived','Failed')),
        CONSTRAINT ck_vendor_files_metadata_verification CHECK (metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected')),
        CONSTRAINT "FK_vendor_files_vendors_vendor_id" FOREIGN KEY (vendor_id) REFERENCES vendors (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE INDEX "IX_apparatus_files_archive_status_archive_next_retry_at" ON apparatus_files (archive_status, archive_next_retry_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD CONSTRAINT ck_apparatus_files_archive_result CHECK (archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD CONSTRAINT ck_apparatus_files_archive_status CHECK (archive_status IS NULL OR archive_status IN ('Pending','Archived','Failed'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus_files ADD CONSTRAINT ck_apparatus_files_metadata_verification CHECK (metadata_verification_status IS NULL OR metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE INDEX "IX_apparatus_vendor_id" ON apparatus (vendor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE INDEX "IX_vendor_files_archive_status_archive_next_retry_at" ON vendor_files (archive_status, archive_next_retry_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE INDEX "IX_vendor_files_vendor_id_uploaded_at" ON vendor_files (vendor_id, uploaded_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE INDEX "IX_vendors_is_active_name" ON vendors (is_active, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE UNIQUE INDEX "IX_vendors_normalized_name" ON vendors (normalized_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    CREATE UNIQUE INDEX "IX_vendors_storage_folder_name" ON vendors (storage_folder_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    ALTER TABLE apparatus ADD CONSTRAINT "FK_apparatus_vendors_vendor_id" FOREIGN KEY (vendor_id) REFERENCES vendors (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    INSERT INTO menu_items
        (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
         requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
    SELECT 'ea5b3405-74c3-497d-8336-391daaa3704f'::uuid, s.id, 'vendors',
           '廠商資訊管理', '/vendors', '▦',
           COALESCE((SELECT MAX(i.sort_order) + 10 FROM menu_items i WHERE i.section_id = s.id), 10),
           TRUE, FALSE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
    FROM menu_sections s
    WHERE LOWER(s.code) = 'department'
      AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'vendors');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027080940_AddEquipmentVendorManagement') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261027080940_AddEquipmentVendorManagement', '10.0.4');
    END IF;
END $EF$;
COMMIT;

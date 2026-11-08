CREATE TABLE apparatus (
    "Id" character varying(30) NOT NULL,
    "ModuleCode" character varying(100) NOT NULL,
    "ProductsId" character varying(100),
    "Name" character varying(200) NOT NULL,
    "NameEn" character varying(200),
    "Kind" character varying(100) NOT NULL,
    "PartNo" text,
    "Manufacturer" text,
    "ManufacturerNumber" text,
    "Brand" text,
    "Model" text,
    "Number" text,
    "ProcurementStaff" text,
    "Imei" text,
    "Os" text,
    "OsVersion" text,
    "InspectionDate" date,
    "MaintenanceDate" date,
    "Place" text,
    "CostPrice" text,
    "YearsUse" text,
    "DaysUse" text,
    "PriceUse" text,
    "CustodianDepartment" text,
    "Custodian" character varying(100) NOT NULL,
    "Agent" text,
    "ReservationStatus" character varying(50) NOT NULL,
    "Feature" text,
    "Spec" text,
    "Note" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_apparatus" PRIMARY KEY ("Id")
);


CREATE TABLE menu_sections (
    id uuid NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    icon text,
    sort_order integer NOT NULL,
    is_enabled boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_menu_sections" PRIMARY KEY (id)
);


CREATE TABLE modules (
    id uuid NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    display_name text NOT NULL,
    route_prefix text NOT NULL,
    icon text,
    description text,
    is_enabled boolean NOT NULL,
    sort_order integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_modules" PRIMARY KEY (id)
);


CREATE TABLE system_options (
    id uuid NOT NULL,
    category text NOT NULL,
    name text NOT NULL,
    value text NOT NULL,
    sort integer NOT NULL,
    is_enabled boolean NOT NULL,
    note text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_system_options" PRIMARY KEY (id)
);


CREATE TABLE users (
    id uuid NOT NULL,
    account text NOT NULL,
    display_name text NOT NULL,
    department text NOT NULL,
    email text NOT NULL,
    is_admin boolean NOT NULL,
    CONSTRAINT "PK_users" PRIMARY KEY (id)
);


CREATE TABLE apparatus_files (
    "Id" uuid NOT NULL,
    "ApparatusId" character varying(30) NOT NULL,
    "FileName" character varying(300) NOT NULL,
    "FilePath" character varying(1000) NOT NULL,
    "ContentType" text,
    "FileSize" bigint NOT NULL,
    "UploadEmp" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    nas_folder_path text,
    nas_file_path text,
    raw_json_path text,
    is_raw_data_exported boolean NOT NULL,
    raw_data_exported_at timestamp with time zone,
    raw_data_export_error text,
    CONSTRAINT "PK_apparatus_files" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_apparatus_files_apparatus_ApparatusId" FOREIGN KEY ("ApparatusId") REFERENCES apparatus ("Id") ON DELETE CASCADE
);


CREATE TABLE menu_items (
    id uuid NOT NULL,
    section_id uuid NOT NULL,
    code text NOT NULL,
    title text NOT NULL,
    route_path text NOT NULL,
    icon text,
    sort_order integer NOT NULL,
    is_enabled boolean NOT NULL,
    admin_only boolean NOT NULL,
    module_code text,
    template_type text,
    use_standard_template boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_menu_items" PRIMARY KEY (id),
    CONSTRAINT "FK_menu_items_menu_sections_section_id" FOREIGN KEY (section_id) REFERENCES menu_sections (id) ON DELETE CASCADE
);


CREATE TABLE module_records (
    id uuid NOT NULL,
    module_id uuid NOT NULL,
    record_no text NOT NULL,
    name text NOT NULL,
    customer text,
    owner text,
    pm_sales text,
    status text NOT NULL,
    result text,
    progress integer NOT NULL,
    start_date date,
    expected_end_date date,
    sample_ready_date date,
    note text,
    applicant_note text,
    team text,
    npi text,
    hardware_version text,
    software_version text,
    hardware_engineer text,
    software_engineer text,
    pjm text,
    location text,
    request_department text,
    request_applicant text,
    sub_pu text,
    assign_owner text,
    mechanical_engineer text,
    department text,
    firmware_version text,
    wireless_drive text,
    customer_product_name text,
    chipset text,
    sample_mac_address text,
    utility_version text,
    dsp_model text,
    dqa_owner text,
    jira_link text,
    notify_users text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_module_records" PRIMARY KEY (id),
    CONSTRAINT "FK_module_records_modules_module_id" FOREIGN KEY (module_id) REFERENCES modules (id) ON DELETE CASCADE
);


CREATE TABLE module_record_cases (
    id uuid NOT NULL,
    record_id uuid NOT NULL,
    case_no text NOT NULL,
    name text NOT NULL,
    status text NOT NULL,
    note text,
    sort_order integer NOT NULL,
    wifi_no text,
    bt_no text,
    gcf_no text,
    ptcrb_no text,
    is_draft boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_module_record_cases" PRIMARY KEY (id),
    CONSTRAINT "FK_module_record_cases_module_records_record_id" FOREIGN KEY (record_id) REFERENCES module_records (id) ON DELETE CASCADE
);


CREATE TABLE module_record_tasks (
    id uuid NOT NULL,
    case_id uuid NOT NULL,
    task_no text NOT NULL,
    name text NOT NULL,
    assign_engineer text,
    status text NOT NULL,
    result text,
    progress integer NOT NULL,
    start_date date,
    expected_end_date date,
    sub_pu text,
    model_name text,
    lab text,
    quoted text,
    reimburse text,
    note text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_module_record_tasks" PRIMARY KEY (id),
    CONSTRAINT "FK_module_record_tasks_module_record_cases_case_id" FOREIGN KEY (case_id) REFERENCES module_record_cases (id) ON DELETE CASCADE
);


CREATE TABLE module_case_files (
    id uuid NOT NULL,
    record_id uuid NOT NULL,
    case_id uuid,
    case_no text NOT NULL,
    task_id uuid,
    task_no text,
    file_name text NOT NULL,
    file_path text NOT NULL,
    content_type text,
    file_size bigint NOT NULL,
    upload_emp text,
    created_at timestamp with time zone NOT NULL,
    nas_folder_path text,
    nas_file_path text,
    raw_json_path text,
    is_raw_data_exported boolean NOT NULL,
    raw_data_exported_at timestamp with time zone,
    raw_data_export_error text,
    CONSTRAINT "PK_module_case_files" PRIMARY KEY (id),
    CONSTRAINT "FK_module_case_files_module_record_cases_case_id" FOREIGN KEY (case_id) REFERENCES module_record_cases (id),
    CONSTRAINT "FK_module_case_files_module_record_tasks_task_id" FOREIGN KEY (task_id) REFERENCES module_record_tasks (id),
    CONSTRAINT "FK_module_case_files_module_records_record_id" FOREIGN KEY (record_id) REFERENCES module_records (id) ON DELETE CASCADE
);


CREATE INDEX "IX_apparatus_files_ApparatusId" ON apparatus_files ("ApparatusId");


CREATE INDEX "IX_menu_items_section_id" ON menu_items (section_id);


CREATE INDEX "IX_module_case_files_case_id" ON module_case_files (case_id);


CREATE INDEX "IX_module_case_files_record_id" ON module_case_files (record_id);


CREATE INDEX "IX_module_case_files_task_id" ON module_case_files (task_id);


CREATE INDEX "IX_module_record_cases_record_id" ON module_record_cases (record_id);


CREATE INDEX "IX_module_record_tasks_case_id" ON module_record_tasks (case_id);


CREATE INDEX "IX_module_records_module_id" ON module_records (module_id);


-- Required reference row used by the later vendor-menu migration. No business
-- or environment data is copied into the isolated test database.
INSERT INTO menu_sections
    (id, code, name, icon, sort_order, is_enabled, created_at, updated_at)
VALUES
    ('30000000-0000-4000-8000-000000000000', 'department', 'Department', NULL,
     1, TRUE, NOW(), NOW());


-- The repository's first seven migrations were already represented by the
-- original a2d6224 model. Record that immutable baseline before applying every
-- later migration from the current checkout.
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
('20260424090000_CaseUploadNewIdMode', '10.0.4'),
('20260424120000_AddUploadEmpToCaseFiles', '10.0.4'),
('20260424130000_AlignModuleTaskFieldsWithLegacy', '10.0.4'),
('20260424150000_AddTaskLevelTestReportFiles', '10.0.4'),
('20260427034040_AddApparatusManagement', '10.0.4'),
('20260427040146_AddMissingApparatusColumns', '10.0.4'),
('20260428010854_AddRawDataColumnsToModuleCaseFiles', '10.0.4');
-- End of immutable initial baseline.

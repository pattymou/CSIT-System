using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

/// <summary>
/// 支援 Case 新增頁 new-id 上傳模式：
/// 1. Case 尚未儲存前，附件可用 record_id + case_no 先上傳。
/// 2. Case 儲存後再把 module_case_files.case_id 綁回正式 Case。
/// 3. 補齊舊系統 Case 頁需要的 WiFi / BT / GCF / PTCRB 欄位。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260424090000_CaseUploadNewIdMode")]
public partial class CaseUploadNewIdMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS module_case_files (
    id uuid PRIMARY KEY,
    record_id uuid NOT NULL,
    case_id uuid NULL,
    case_no varchar(100) NOT NULL,
    file_name varchar(500) NOT NULL,
    file_path varchar(1000) NOT NULL,
    content_type varchar(200) NULL,
    file_size bigint NOT NULL DEFAULT 0,
    created_at timestamp without time zone NOT NULL DEFAULT now()
);
""");

        migrationBuilder.Sql("""
ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS record_id uuid;

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS case_no varchar(100);

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS file_name varchar(500);

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS file_path varchar(1000);

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS content_type varchar(200);

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS file_size bigint NOT NULL DEFAULT 0;

ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS created_at timestamp without time zone NOT NULL DEFAULT now();

ALTER TABLE module_case_files
    ALTER COLUMN case_id DROP NOT NULL;
""");

        migrationBuilder.Sql("""
ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS case_no varchar(100) NOT NULL DEFAULT '';

ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS wifi_no varchar(200);

ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS bt_no varchar(200);

ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS gcf_no varchar(200);

ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS ptcrb_no varchar(200);

ALTER TABLE module_record_cases
    ADD COLUMN IF NOT EXISTS is_draft boolean NOT NULL DEFAULT false;
""");

        migrationBuilder.Sql("""
CREATE INDEX IF NOT EXISTS ix_module_case_files_record_id_case_no
    ON module_case_files (record_id, case_no);

CREATE INDEX IF NOT EXISTS ix_module_case_files_case_id
    ON module_case_files (case_id);
""");

        migrationBuilder.Sql("""
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_module_case_files_module_records_record_id'
    ) THEN
        ALTER TABLE module_case_files
        ADD CONSTRAINT fk_module_case_files_module_records_record_id
        FOREIGN KEY (record_id) REFERENCES module_records(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_module_case_files_module_record_cases_case_id'
    ) THEN
        ALTER TABLE module_case_files
        ADD CONSTRAINT fk_module_case_files_module_record_cases_case_id
        FOREIGN KEY (case_id) REFERENCES module_record_cases(id) ON DELETE CASCADE;
    END IF;
END $$;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_module_case_files_module_record_cases_case_id'
    ) THEN
        ALTER TABLE module_case_files
        DROP CONSTRAINT fk_module_case_files_module_record_cases_case_id;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_module_case_files_module_records_record_id'
    ) THEN
        ALTER TABLE module_case_files
        DROP CONSTRAINT fk_module_case_files_module_records_record_id;
    END IF;
END $$;
""");

        migrationBuilder.Sql("""
DROP INDEX IF EXISTS ix_module_case_files_case_id;
DROP INDEX IF EXISTS ix_module_case_files_record_id_case_no;
""");

        migrationBuilder.Sql("""
ALTER TABLE module_record_cases
    DROP COLUMN IF EXISTS wifi_no,
    DROP COLUMN IF EXISTS bt_no,
    DROP COLUMN IF EXISTS gcf_no,
    DROP COLUMN IF EXISTS ptcrb_no,
    DROP COLUMN IF EXISTS is_draft;
""");

        migrationBuilder.Sql("""
DROP TABLE IF EXISTS module_case_files;
""");
    }
}

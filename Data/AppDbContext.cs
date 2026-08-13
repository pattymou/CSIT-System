using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ModuleEntity> Modules => Set<ModuleEntity>();
    public DbSet<ModuleRecord> ModuleRecords => Set<ModuleRecord>();
    public DbSet<ModuleRecordCase> ModuleRecordCases => Set<ModuleRecordCase>();
    public DbSet<ModuleRecordTask> ModuleRecordTasks => Set<ModuleRecordTask>();
    public DbSet<MenuSection> MenuSections => Set<MenuSection>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ModuleCaseFile> ModuleCaseFiles => Set<ModuleCaseFile>();
    public DbSet<Apparatus> Apparatuses => Set<Apparatus>();
    public DbSet<ApparatusFile> ApparatusFiles => Set<ApparatusFile>();
    public DbSet<SystemOption> SystemOptions => Set<SystemOption>();
    public DbSet<VerificationApplication> VerificationApplications => Set<VerificationApplication>();
    public DbSet<VerificationApplicationFile> VerificationApplicationFiles => Set<VerificationApplicationFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModuleEntity>(entity =>
        {
            entity.ToTable("modules");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.RoutePrefix).HasColumnName("route_prefix");
            entity.Property(x => x.Icon).HasColumnName("icon");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ModuleRecord>(entity =>
        {
            entity.ToTable("module_records");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ModuleId).HasColumnName("module_id");
            entity.Property(x => x.RecordNo).HasColumnName("record_no");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Customer).HasColumnName("customer");
            entity.Property(x => x.Owner).HasColumnName("owner");
            entity.Property(x => x.PmSales).HasColumnName("pm_sales");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Result).HasColumnName("result");
            entity.Property(x => x.Progress).HasColumnName("progress");
            entity.Property(x => x.StartDate).HasColumnName("start_date");
            entity.Property(x => x.ExpectedEndDate).HasColumnName("expected_end_date");
            entity.Property(x => x.SampleReadyDate).HasColumnName("sample_ready_date");
            entity.Property(x => x.Note).HasColumnName("note");
            entity.Property(x => x.ApplicantNote).HasColumnName("applicant_note");

            entity.Property(x => x.Team).HasColumnName("team");
            entity.Property(x => x.Npi).HasColumnName("npi");
            entity.Property(x => x.HardwareVersion).HasColumnName("hardware_version");
            entity.Property(x => x.SoftwareVersion).HasColumnName("software_version");
            entity.Property(x => x.HardwareEngineer).HasColumnName("hardware_engineer");
            entity.Property(x => x.SoftwareEngineer).HasColumnName("software_engineer");
            entity.Property(x => x.Pjm).HasColumnName("pjm");
            entity.Property(x => x.Location).HasColumnName("location");
            entity.Property(x => x.RequestDepartment).HasColumnName("request_department");
            entity.Property(x => x.RequestApplicant).HasColumnName("request_applicant");

            entity.Property(x => x.SubPu).HasColumnName("sub_pu");
            entity.Property(x => x.AssignOwner).HasColumnName("assign_owner");
            entity.Property(x => x.MechanicalEngineer).HasColumnName("mechanical_engineer");
            entity.Property(x => x.Department).HasColumnName("department");
            entity.Property(x => x.FirmwareVersion).HasColumnName("firmware_version");
            entity.Property(x => x.WirelessDrive).HasColumnName("wireless_drive");
            entity.Property(x => x.CustomerProductName).HasColumnName("customer_product_name");
            entity.Property(x => x.Chipset).HasColumnName("chipset");
            entity.Property(x => x.SampleMacAddress).HasColumnName("sample_mac_address");
            entity.Property(x => x.UtilityVersion).HasColumnName("utility_version");
            entity.Property(x => x.DspModel).HasColumnName("dsp_model");
            entity.Property(x => x.JiraLink).HasColumnName("jira_link");
            entity.Property(x => x.DqaOwner).HasColumnName("dqa_owner");
            entity.Property(x => x.NotifyUsers).HasColumnName("notify_users");

            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(x => x.Module)
                .WithMany(x => x.Records)
                .HasForeignKey(x => x.ModuleId);
        });

        modelBuilder.HasSequence<long>("verification_application_no_seq");

        modelBuilder.Entity<VerificationApplication>(entity =>
        {
            entity.ToTable("verification_applications", table =>
                table.HasCheckConstraint(
                    "ck_verification_applications_status",
                    "status IN ('Draft', 'Submitted', 'Returned', 'Accepted', 'Rejected')"));
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApplicationNo).HasColumnName("application_no").HasMaxLength(32).IsRequired();
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ApplicantAccount).HasColumnName("applicant_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ApplicantName).HasColumnName("applicant_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ApplicantEmail).HasColumnName("applicant_email").HasMaxLength(320).IsRequired();
            entity.Property(x => x.Department).HasColumnName("department").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ApplicantExtension).HasColumnName("applicant_extension").HasMaxLength(50);
            entity.Property(x => x.ProjectName).HasColumnName("project_name").HasMaxLength(300).IsRequired();
            entity.Property(x => x.SubPu).HasColumnName("sub_pu").HasMaxLength(200);
            entity.Property(x => x.Customer).HasColumnName("customer").HasMaxLength(200);
            entity.Property(x => x.ProductModel).HasColumnName("product_model").HasMaxLength(300);
            entity.Property(x => x.RequestedFinishDate).HasColumnName("requested_finish_date");
            entity.Property(x => x.ValidationRequirement).HasColumnName("validation_requirement");
            entity.Property(x => x.HardwareVersion).HasColumnName("hardware_version").HasMaxLength(200);
            entity.Property(x => x.FirmwareVersion).HasColumnName("firmware_version").HasMaxLength(200);
            entity.Property(x => x.SoftwareVersion).HasColumnName("software_version").HasMaxLength(200);
            entity.Property(x => x.SampleReadyDate).HasColumnName("sample_ready_date");
            entity.Property(x => x.JiraLink).HasColumnName("jira_link").HasMaxLength(1000);
            entity.Property(x => x.Location).HasColumnName("location").HasMaxLength(100);
            entity.Property(x => x.Npi).HasColumnName("npi").HasMaxLength(200);
            entity.Property(x => x.WirelessDrive).HasColumnName("wireless_drive").HasMaxLength(200);
            entity.Property(x => x.Chipset).HasColumnName("chipset").HasMaxLength(200);
            entity.Property(x => x.SampleMacAddress).HasColumnName("sample_mac_address").HasMaxLength(200);
            entity.Property(x => x.UtilityVersion).HasColumnName("utility_version").HasMaxLength(200);
            entity.Property(x => x.DspModel).HasColumnName("dsp_model").HasMaxLength(200);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.ModuleRecordId).HasColumnName("module_record_id");
            entity.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(x => x.ReturnedAt).HasColumnName("returned_at");
            entity.Property(x => x.RejectedAt).HasColumnName("rejected_at");
            entity.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            entity.Property(x => x.ProcessedBy).HasColumnName("processed_by").HasMaxLength(100);
            entity.Property(x => x.ProcessingNote).HasColumnName("processing_note");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(x => x.ApplicationNo).IsUnique();
            entity.HasIndex(x => x.ModuleRecordId).IsUnique().HasFilter("module_record_id IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.SubmittedAt });

            entity.HasOne(x => x.ModuleRecord)
                .WithOne()
                .HasForeignKey<VerificationApplication>(x => x.ModuleRecordId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VerificationApplicationFile>(entity =>
        {
            entity.ToTable("verification_application_files");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.VerificationApplicationId).HasColumnName("verification_application_id");
            entity.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300).IsRequired();
            entity.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => x.VerificationApplicationId);
            entity.HasOne(x => x.VerificationApplication)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.VerificationApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModuleRecordCase>(entity =>
        {
            entity.ToTable("module_record_cases");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RecordId).HasColumnName("record_id");
            entity.Property(x => x.CaseNo).HasColumnName("case_no");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Note).HasColumnName("note");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.WifiNo).HasColumnName("wifi_no");
            entity.Property(x => x.BtNo).HasColumnName("bt_no");
            entity.Property(x => x.GcfNo).HasColumnName("gcf_no");
            entity.Property(x => x.PtcrbNo).HasColumnName("ptcrb_no");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.IsDraft).HasColumnName("is_draft");

            entity.HasOne(x => x.Record)
                .WithMany(x => x.Cases)
                .HasForeignKey(x => x.RecordId);
        });

        modelBuilder.Entity<ModuleRecordTask>(entity =>
        {
            entity.ToTable("module_record_tasks");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CaseId).HasColumnName("case_id");
            entity.Property(x => x.TaskNo).HasColumnName("task_no");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.AssignEngineer).HasColumnName("assign_engineer");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Result).HasColumnName("result");
            entity.Property(x => x.Progress).HasColumnName("progress");
            entity.Property(x => x.StartDate).HasColumnName("start_date");
            entity.Property(x => x.ExpectedEndDate).HasColumnName("expected_end_date");
            entity.Property(x => x.SubPu).HasColumnName("sub_pu");
            entity.Property(x => x.ModelName).HasColumnName("model_name");
            entity.Property(x => x.Lab).HasColumnName("lab");
            entity.Property(x => x.Quoted).HasColumnName("quoted");
            entity.Property(x => x.Reimburse).HasColumnName("reimburse");
            entity.Property(x => x.Note).HasColumnName("note");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(x => x.Case)
                .WithMany(x => x.Tasks)
                .HasForeignKey(x => x.CaseId);
        });

        modelBuilder.Entity<MenuSection>(entity =>
        {
            entity.ToTable("menu_sections");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Icon).HasColumnName("icon");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<MenuItem>(entity =>
        {
            entity.ToTable("menu_items");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SectionId).HasColumnName("section_id");
            entity.Property(x => x.Code).HasColumnName("code");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.RoutePath).HasColumnName("route_path");
            entity.Property(x => x.Icon).HasColumnName("icon");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.AdminOnly).HasColumnName("admin_only");
            entity.Property(x => x.ModuleCode).HasColumnName("module_code");
            entity.Property(x => x.TemplateType).HasColumnName("template_type");
            entity.Property(x => x.UseStandardTemplate).HasColumnName("use_standard_template");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(x => x.Section)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.SectionId);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Account).HasColumnName("account");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.Department).HasColumnName("department");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.IsAdmin).HasColumnName("is_admin");
        });

        modelBuilder.Entity<ModuleCaseFile>(entity =>
        {
            entity.ToTable("module_case_files");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RecordId).HasColumnName("record_id");
            entity.Property(x => x.CaseId).HasColumnName("case_id");
            entity.Property(x => x.CaseNo).HasColumnName("case_no");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.TaskNo).HasColumnName("task_no");
            entity.Property(x => x.FileName).HasColumnName("file_name");
            entity.Property(x => x.FilePath).HasColumnName("file_path");
            entity.Property(x => x.ContentType).HasColumnName("content_type");
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.UploadEmp).HasColumnName("upload_emp");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");

            entity.HasOne(x => x.Record)
                .WithMany()
                .HasForeignKey(x => x.RecordId);

            entity.HasOne(x => x.Case)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.CaseId)
                .IsRequired(false);

            entity.HasOne(x => x.Task)
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .IsRequired(false);
        });

        modelBuilder.Entity<Apparatus>(entity =>
        {
            entity.ToTable("apparatus");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasMaxLength(30);
            entity.Property(x => x.ModuleCode).HasColumnName("ModuleCode").HasMaxLength(100).IsRequired();
            entity.Property(x => x.ProductsId).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NameEn).HasMaxLength(200);
            entity.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Custodian).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ReservationStatus).HasMaxLength(50);
            entity.Property(x => x.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<ApparatusFile>(entity =>
        {
            entity.ToTable("apparatus_files");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("Id");
            entity.Property(x => x.ApparatusId).HasColumnName("ApparatusId").HasMaxLength(30).IsRequired();
            entity.Property(x => x.FileName).HasColumnName("FileName").HasMaxLength(300).IsRequired();
            entity.Property(x => x.FilePath).HasColumnName("FilePath").HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("ContentType");
            entity.Property(x => x.FileSize).HasColumnName("FileSize");
            entity.Property(x => x.UploadEmp).HasColumnName("UploadEmp");
            entity.Property(x => x.CreatedAt).HasColumnName("CreatedAt");

            entity.Property(x => x.NasFolderPath).HasColumnName("nas_folder_path");
            entity.Property(x => x.NasFilePath).HasColumnName("nas_file_path");
            entity.Property(x => x.RawJsonPath).HasColumnName("raw_json_path");
            entity.Property(x => x.IsRawDataExported).HasColumnName("is_raw_data_exported");
            entity.Property(x => x.RawDataExportedAt).HasColumnName("raw_data_exported_at");
            entity.Property(x => x.RawDataExportError).HasColumnName("raw_data_export_error");

            entity.HasOne(x => x.Apparatus)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.ApparatusId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

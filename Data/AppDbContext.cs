using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Data;

public class AppDbContext : DbContext
{
    internal Guid? CaseDeletionCleanupJobId { get; set; }
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
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorFile> VendorFiles => Set<VendorFile>();
    public DbSet<SystemOption> SystemOptions => Set<SystemOption>();
    public DbSet<TeamRouting> TeamRoutings => Set<TeamRouting>();
    public DbSet<VerificationApplication> VerificationApplications => Set<VerificationApplication>();
    public DbSet<VerificationApplicationFile> VerificationApplicationFiles => Set<VerificationApplicationFile>();
    public DbSet<VerificationCategory> VerificationCategories => Set<VerificationCategory>();
    public DbSet<TestEnvironment> TestEnvironments => Set<TestEnvironment>();
    public DbSet<EquipmentGroup> EquipmentGroups => Set<EquipmentGroup>();
    public DbSet<EquipmentGroupDevice> EquipmentGroupDevices => Set<EquipmentGroupDevice>();
    public DbSet<EquipmentGroupRequirement> EquipmentGroupRequirements => Set<EquipmentGroupRequirement>();
    public DbSet<TestCapability> TestCapabilities => Set<TestCapability>();
    public DbSet<TestPlanTemplate> TestPlanTemplates => Set<TestPlanTemplate>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<TestExecutionProfile> TestExecutionProfiles => Set<TestExecutionProfile>();
    public DbSet<PlannedTestItem> PlannedTestItems => Set<PlannedTestItem>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationItem> ReservationItems => Set<ReservationItem>();
    public DbSet<ApparatusResourceCapability> ApparatusResourceCapabilities => Set<ApparatusResourceCapability>();
    public DbSet<ReservationExtensionRequest> ReservationExtensionRequests => Set<ReservationExtensionRequest>();
    public DbSet<ReservationAuditEvent> ReservationAuditEvents => Set<ReservationAuditEvent>();
    public DbSet<PendingSubmission> PendingSubmissions => Set<PendingSubmission>();
    public DbSet<ApprovalAuditEvent> ApprovalAuditEvents => Set<ApprovalAuditEvent>();
    public DbSet<KmIntegrationOutbox> KmIntegrationOutbox => Set<KmIntegrationOutbox>();
    public DbSet<KmIntegrationManualRetryAudit> KmIntegrationManualRetryAudits => Set<KmIntegrationManualRetryAudit>();
    public DbSet<VerificationCaseDeletionJob> VerificationCaseDeletionJobs => Set<VerificationCaseDeletionJob>();
    public DbSet<VerificationCaseDeletionTarget> VerificationCaseDeletionTargets => Set<VerificationCaseDeletionTarget>();
    public DbSet<VerificationCaseDeletionCallbackReceipt> VerificationCaseDeletionCallbacks => Set<VerificationCaseDeletionCallbackReceipt>();
    public DbSet<ValidationFormTemplate> ValidationFormTemplates => Set<ValidationFormTemplate>();
    public DbSet<ValidationFormTemplateVersion> ValidationFormTemplateVersions => Set<ValidationFormTemplateVersion>();
    public DbSet<ValidationProductType> ValidationProductTypes => Set<ValidationProductType>();
    public DbSet<ValidationSectionDefinition> ValidationSectionDefinitions => Set<ValidationSectionDefinition>();
    public DbSet<ValidationProductTypeSectionMapping> ValidationProductTypeSectionMappings => Set<ValidationProductTypeSectionMapping>();
    public DbSet<ValidationFieldDefinition> ValidationFieldDefinitions => Set<ValidationFieldDefinition>();
    public DbSet<ValidationFieldOption> ValidationFieldOptions => Set<ValidationFieldOption>();
    public DbSet<ValidationFieldCondition> ValidationFieldConditions => Set<ValidationFieldCondition>();
    public DbSet<ValidationTestItemCategory> ValidationTestItemCategories => Set<ValidationTestItemCategory>();
    public DbSet<ValidationProductTypeTestCategoryMapping> ValidationProductTypeTestCategoryMappings => Set<ValidationProductTypeTestCategoryMapping>();
    public DbSet<ValidationTestItemDefinition> ValidationTestItemDefinitions => Set<ValidationTestItemDefinition>();
    public DbSet<ValidationTestItemParameterDefinition> ValidationTestItemParameterDefinitions => Set<ValidationTestItemParameterDefinition>();
    public DbSet<ValidationTestItemParameterOption> ValidationTestItemParameterOptions => Set<ValidationTestItemParameterOption>();
    public DbSet<DocumentFolder> DocumentFolders => Set<DocumentFolder>();
    public DbSet<DocumentLibraryFile> DocumentLibraryFiles => Set<DocumentLibraryFile>();
    public DbSet<DocumentProcessingJob> DocumentProcessingJobs => Set<DocumentProcessingJob>();
    public DbSet<DocumentProcessingCallbackReceipt> DocumentProcessingCallbacks => Set<DocumentProcessingCallbackReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditMutation();
        return SaveChangesGuardedAsync(acceptAllChangesOnSuccess, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditMutation();
        return SaveChangesGuardedAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task<int> SaveChangesGuardedAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        var recordIds = await VerificationCaseDeletionWriteGuard.AffectedRecordsAsync(this, cancellationToken);
        if (recordIds.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownsTransaction = Database.CurrentTransaction is null && Database.IsRelational();
        await using var transaction = ownsTransaction
            ? await Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        foreach (var recordId in recordIds.Order())
        {
            await VerificationCaseDeletionWriteGuard.LockRecordAsync(this, recordId, cancellationToken);
            if (await VerificationCaseDeletionJobs.AsNoTracking().AnyAsync(x => x.RecordId == recordId &&
                (x.Status == "WaitingForKm" || x.Status == "KmVerified" ||
                 x.Status == "LocalReady" || x.Status == "LocalCleanup" ||
                 x.Status == "Completed") &&
                !(x.Status == "LocalCleanup" && x.Id == CaseDeletionCleanupJobId), cancellationToken))
                throw new InvalidOperationException("This verification case is frozen for deletion.");
        }
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private void RejectAuditMutation()
    {
        if (ChangeTracker.Entries<ApprovalAuditEvent>().Any(x =>
                x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Approval audit events are append-only.");
        if (ChangeTracker.Entries<KmIntegrationManualRetryAudit>().Any(x =>
                x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("KM manual retry audits are append-only.");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureTestCatalog();
        modelBuilder.ConfigureVerificationApplicationRouting();
        modelBuilder.ConfigureSystemMasterData();
        modelBuilder.ConfigureReservations();
        modelBuilder.ConfigureApparatusResourceCapabilities();
        modelBuilder.ConfigureSupervisorApproval();
        modelBuilder.ConfigureKmIntegration();
        modelBuilder.ConfigureVerificationCaseDeletion();
        modelBuilder.ConfigureValidationRequests();

        modelBuilder.Entity<DocumentFolder>(entity =>
        {
            entity.ToTable("document_folders", table =>
                table.HasCheckConstraint("ck_document_folders_level", "level BETWEEN 1 AND 3"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ParentId).HasColumnName("parent_id");
            entity.Property(x => x.Level).HasColumnName("level");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.ParentId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.ParentId, x.SortOrder });
            entity.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentLibraryFile>(entity =>
        {
            entity.ToTable("document_files");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.FolderId).HasColumnName("folder_id");
            entity.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(300).IsRequired();
            entity.Property(x => x.StandardFileName).HasColumnName("standard_file_name").HasMaxLength(350).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(x => x.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(200).IsRequired();
            entity.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.RelativePath).HasColumnName("relative_path").HasMaxLength(1000).IsRequired();
            entity.Property(x => x.StorageArea).HasColumnName("storage_area").HasMaxLength(30)
                .HasDefaultValue(DocumentStorageAreas.Library).IsRequired();
            entity.HasIndex(x => x.StandardFileName).IsUnique();
            entity.HasIndex(x => new { x.FolderId, x.UploadedAt });
            entity.HasOne(x => x.Folder).WithMany(x => x.Files).HasForeignKey(x => x.FolderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentProcessingJob>(entity =>
        {
            entity.ToTable("document_processing_jobs", table =>
            {
                table.HasCheckConstraint("ck_document_processing_jobs_status",
                    "status IN ('Pending', 'Accepted', 'Processing', 'Completed', 'Failed')");
                table.HasCheckConstraint("ck_document_processing_jobs_archive_status",
                    "archive_status IN ('NotStarted', 'Pending', 'Archiving', 'Completed', 'Failed')");
                table.HasCheckConstraint("ck_document_processing_jobs_metadata",
                    "status <> 'Completed' OR metadata_json IS NOT NULL OR (archive_status = 'NotStarted' AND km_completed_at IS NULL)");
                table.HasCheckConstraint("ck_document_processing_jobs_archive_result",
                    "archive_status <> 'Completed' OR (archived_at IS NOT NULL AND archive_sha256 IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DocumentFileId).HasColumnName("document_file_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
            entity.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(x => x.KmStartedAt).HasColumnName("km_started_at");
            entity.Property(x => x.KmCompletedAt).HasColumnName("km_completed_at");
            entity.Property(x => x.KmFailedAt).HasColumnName("km_failed_at");
            entity.Property(x => x.KmErrorCode).HasColumnName("km_error_code").HasMaxLength(100);
            entity.Property(x => x.KmErrorMessage).HasColumnName("km_error_message").HasMaxLength(2000);
            entity.Property(x => x.KmErrorRetryable).HasColumnName("km_error_retryable");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(x => x.ArchiveStatus).HasColumnName("archive_status").HasMaxLength(30)
                .HasDefaultValue(DocumentArchiveStatuses.NotStarted).IsRequired();
            entity.Property(x => x.ArchiveAttemptCount).HasColumnName("archive_attempt_count");
            entity.Property(x => x.ArchiveNextRetryAt).HasColumnName("archive_next_retry_at");
            entity.Property(x => x.ArchiveLeaseToken).HasColumnName("archive_lease_token");
            entity.Property(x => x.ArchiveLeaseExpiresAt).HasColumnName("archive_lease_expires_at");
            entity.Property(x => x.ArchiveLastAttemptAt).HasColumnName("archive_last_attempt_at");
            entity.Property(x => x.ArchiveLastError).HasColumnName("archive_last_error").HasMaxLength(2000);
            entity.Property(x => x.ArchiveSha256).HasColumnName("archive_sha256").HasMaxLength(64);
            entity.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            entity.HasIndex(x => x.DocumentFileId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.ArchiveStatus, x.ArchiveNextRetryAt })
                .HasDatabaseName("ix_document_jobs_archive_retry");
            entity.HasIndex(x => new { x.ArchiveStatus, x.ArchiveLeaseExpiresAt })
                .HasDatabaseName("ix_document_jobs_archive_claim");
            entity.HasOne(x => x.DocumentFile).WithOne(x => x.ProcessingJob)
                .HasForeignKey<DocumentProcessingJob>(x => x.DocumentFileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentProcessingCallbackReceipt>(entity =>
        {
            entity.ToTable("document_processing_callbacks", table =>
                table.HasCheckConstraint("ck_document_processing_callbacks_status",
                    "status IN ('Processing', 'Completed', 'Failed')"));
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.DocumentProcessingJobId).HasColumnName("document_processing_job_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => new { x.DocumentProcessingJobId, x.ReceivedAt })
                .HasDatabaseName("ix_document_callbacks_job_received");
            entity.HasOne(x => x.DocumentProcessingJob).WithMany(x => x.CallbackReceipts)
                .HasForeignKey(x => x.DocumentProcessingJobId).OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_document_callbacks_job");
        });

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
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(100);
            entity.Property(x => x.RoutingDepartmentOptionId).HasColumnName("routing_department_option_id");
            entity.Property(x => x.RoutingDepartmentCode).HasColumnName("routing_department_code");
            entity.Property(x => x.RoutingDepartmentName).HasColumnName("routing_department_name");
            entity.HasOne<SystemOption>().WithMany().HasForeignKey(x => x.RoutingDepartmentOptionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.TeamOptionId).HasColumnName("team_option_id");
            entity.Property(x => x.TeamCode).HasColumnName("team_code").HasMaxLength(200);
            entity.Property(x => x.TeamName).HasColumnName("team_name").HasMaxLength(200);
            entity.Property(x => x.VerificationCategoryId).HasColumnName("verification_category_id");
            entity.Property(x => x.CategoryCode).HasColumnName("category_code").HasMaxLength(100);
            entity.Property(x => x.CategoryName).HasColumnName("category_name").HasMaxLength(200);
            entity.Property(x => x.AssignedLeaderAccount).HasColumnName("assigned_leader_account").HasMaxLength(100);
            entity.Property(x => x.AssignedLeaderDisplayName).HasColumnName("assigned_leader_display_name").HasMaxLength(200);
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
            entity.Property(x => x.ExpectedEndDate).HasColumnName("expected_end_date");
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
            entity.Property(x => x.ValidationTemplateVersionId).HasColumnName("validation_template_version_id");
            entity.Property(x => x.ValidationTemplateVersionNumber).HasColumnName("validation_template_version_number");
            entity.Property(x => x.ValidationProductTypeCode).HasColumnName("validation_product_type_code").HasMaxLength(100);
            entity.Property(x => x.ValidationFormPayloadJson).HasColumnName("validation_form_payload_json").HasColumnType("jsonb");
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
            entity.HasIndex(x => new { x.ApplicantAccount, x.Status });
            entity.HasIndex(x => new { x.AssignedLeaderAccount, x.Status });
            entity.HasIndex(x => new { x.TeamOptionId, x.Status });

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
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.DocumentLibraryFileId).HasColumnName("document_library_file_id");
            entity.HasIndex(x => x.VerificationApplicationId);
            entity.HasIndex(x => x.DocumentLibraryFileId).IsUnique().HasFilter("document_library_file_id IS NOT NULL");
            entity.HasOne(x => x.VerificationApplication)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.VerificationApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DocumentLibraryFile)
                .WithOne()
                .HasForeignKey<VerificationApplicationFile>(x => x.DocumentLibraryFileId)
                .OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(x => x.SourceTestCategoryCode).HasColumnName("source_test_category_code").HasMaxLength(100);
            entity.Property(x => x.BtNo).HasColumnName("bt_no");
            entity.Property(x => x.GcfNo).HasColumnName("gcf_no");
            entity.Property(x => x.PtcrbNo).HasColumnName("ptcrb_no");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.IsDraft).HasColumnName("is_draft");

            entity.HasOne(x => x.Record)
                .WithMany(x => x.Cases)
                .HasForeignKey(x => x.RecordId);
            entity.HasIndex(x => new { x.RecordId, x.SourceTestCategoryCode })
                .IsUnique()
                .HasFilter("source_test_category_code IS NOT NULL");
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
            entity.Property(x => x.ActualCompletionDate).HasColumnName("actual_completion_date");
            entity.Property(x => x.SourceTestItemCode).HasColumnName("source_test_item_code").HasMaxLength(100);
            entity.Property(x => x.SourceTestParametersJson).HasColumnName("source_test_parameters_json").HasColumnType("jsonb");
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
            entity.HasIndex(x => new { x.CaseId, x.SourceTestItemCode })
                .IsUnique()
                .HasFilter("source_test_item_code IS NOT NULL");
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
            entity.Property(x => x.RequiresSupervisorApproval).HasColumnName("requires_supervisor_approval").HasDefaultValue(false);
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
            entity.Property(x => x.IdentityUserId).HasColumnName("identity_user_id");
            entity.Property(x => x.Account).HasColumnName("account");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.Department).HasColumnName("department");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.IsAdmin).HasColumnName("is_admin");
            entity.HasIndex(x => x.IdentityUserId).IsUnique();
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
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.VersionNo).HasColumnName("version_no");
            entity.Property(x => x.FileKind).HasColumnName("file_kind").HasMaxLength(30);
            entity.Property(x => x.ReviewStatus).HasColumnName("review_status").HasMaxLength(30);
            entity.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(200);
            entity.Property(x => x.ReviewComment).HasColumnName("review_comment").HasMaxLength(2000);
            entity.Property(x => x.IsFinal).HasColumnName("is_final");
            entity.Property(x => x.FinalizedAt).HasColumnName("finalized_at");
            entity.Property(x => x.FinalizedBy).HasColumnName("finalized_by").HasMaxLength(200);
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(x => new { x.DocumentId, x.VersionNo }).IsUnique();
            entity.HasIndex(x => x.DocumentId)
                .IsUnique()
                .HasFilter("\"is_final\" = TRUE");
            entity.HasIndex(x => new { x.ReviewStatus, x.IsFinal });

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
            entity.Property(x => x.CustodianAccount).HasMaxLength(100);
            entity.Property(x => x.ReservationStatus).HasMaxLength(50);
            entity.Property(x => x.VendorId).HasColumnName("vendor_id");
            entity.Property(x => x.StorageFolderName).HasColumnName("storage_folder_name").HasMaxLength(240);
            entity.Property(x => x.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

            entity.HasIndex(x => x.CustodianAccount);
            entity.HasOne(x => x.DepartmentOption).WithMany().HasForeignKey(x => x.DepartmentOptionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.OwnerTeamOptionId);
            entity.HasOne(x => x.OwnerTeamOption)
                .WithMany()
                .HasForeignKey(x => x.OwnerTeamOptionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.VendorId);
            entity.HasOne(x => x.Vendor).WithMany(x => x.Apparatuses).HasForeignKey(x => x.VendorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApparatusFile>(entity =>
        {
            entity.ToTable("apparatus_files", table =>
            {
                table.HasCheckConstraint("ck_apparatus_files_archive_status",
                    "archive_status IS NULL OR archive_status IN ('Pending','Archived','Failed')");
                table.HasCheckConstraint("ck_apparatus_files_archive_result",
                    "archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64)");
                table.HasCheckConstraint("ck_apparatus_files_metadata_verification",
                    "metadata_verification_status IS NULL OR metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected')");
            });
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("Id");
            entity.Property(x => x.ApparatusId).HasColumnName("ApparatusId").HasMaxLength(30).IsRequired();
            entity.Property(x => x.FileName).HasColumnName("FileName").HasMaxLength(300).IsRequired();
            entity.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(300);
            entity.Property(x => x.FilePath).HasColumnName("FilePath").HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("ContentType");
            entity.Property(x => x.FileSize).HasColumnName("FileSize");
            entity.Property(x => x.UploadEmp).HasColumnName("UploadEmp");
            entity.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.ArchiveStatus).HasColumnName("archive_status").HasMaxLength(30);
            entity.Property(x => x.ArchiveError).HasColumnName("archive_error").HasMaxLength(2000);
            entity.Property(x => x.ArchiveAttemptCount).HasColumnName("archive_attempt_count").HasDefaultValue(0);
            entity.Property(x => x.ArchiveNextRetryAt).HasColumnName("archive_next_retry_at");
            entity.Property(x => x.ArchiveUpdatedAt).HasColumnName("archive_updated_at");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(x => x.MetadataSource).HasColumnName("metadata_source").HasMaxLength(100);
            entity.Property(x => x.MetadataVersion).HasColumnName("metadata_version").HasMaxLength(100);
            entity.Property(x => x.MetadataVerificationStatus).HasColumnName("metadata_verification_status").HasMaxLength(30);

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
            entity.HasIndex(x => new { x.ArchiveStatus, x.ArchiveNextRetryAt });
        });

        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.ToTable("vendors", table => table.HasCheckConstraint("ck_vendors_types",
                "types <@ ARRAY['Manufacturer','Agent','Supplier','MaintenanceProvider']::text[]"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Aliases).HasColumnName("aliases").HasMaxLength(500);
            entity.Property(x => x.Types).HasColumnName("types").HasColumnType("text[]").IsRequired();
            entity.Property(x => x.Website).HasColumnName("website").HasMaxLength(500);
            entity.Property(x => x.ContactPerson).HasColumnName("contact_person").HasMaxLength(200);
            entity.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(100);
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
            entity.Property(x => x.Address).HasColumnName("address").HasMaxLength(1000);
            entity.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(x => x.StorageFolderName).HasColumnName("storage_folder_name").HasMaxLength(240).IsRequired();
            entity.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(200).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200).IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            entity.HasIndex(x => x.NormalizedName).IsUnique();
            entity.HasIndex(x => x.StorageFolderName).IsUnique();
            entity.HasIndex(x => new { x.IsActive, x.Name });
        });

        modelBuilder.Entity<VendorFile>(entity =>
        {
            entity.ToTable("vendor_files", table =>
            {
                table.HasCheckConstraint("ck_vendor_files_archive_status", "archive_status IN ('Pending','Archived','Failed')");
                table.HasCheckConstraint("ck_vendor_files_archive_result",
                    "archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64)");
                table.HasCheckConstraint("ck_vendor_files_metadata_verification",
                    "metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.VendorId).HasColumnName("vendor_id");
            entity.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(300).IsRequired();
            entity.Property(x => x.StoredFileName).HasColumnName("stored_file_name").HasMaxLength(300).IsRequired();
            entity.Property(x => x.FolderPath).HasColumnName("folder_path").HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.ArchiveStatus).HasColumnName("archive_status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ArchiveError).HasColumnName("archive_error").HasMaxLength(2000);
            entity.Property(x => x.ArchiveAttemptCount).HasColumnName("archive_attempt_count").HasDefaultValue(0);
            entity.Property(x => x.ArchiveNextRetryAt).HasColumnName("archive_next_retry_at");
            entity.Property(x => x.ArchiveUpdatedAt).HasColumnName("archive_updated_at");
            entity.Property(x => x.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(200).IsRequired();
            entity.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(x => x.MetadataSource).HasColumnName("metadata_source").HasMaxLength(100);
            entity.Property(x => x.MetadataVersion).HasColumnName("metadata_version").HasMaxLength(100);
            entity.Property(x => x.MetadataVerificationStatus).HasColumnName("metadata_verification_status").HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.VendorId, x.UploadedAt });
            entity.HasIndex(x => new { x.ArchiveStatus, x.ArchiveNextRetryAt });
            entity.HasOne(x => x.Vendor).WithMany(x => x.Files).HasForeignKey(x => x.VendorId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

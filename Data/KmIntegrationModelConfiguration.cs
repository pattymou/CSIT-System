using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class KmIntegrationModelConfiguration
{
    public static void ConfigureKmIntegration(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KmIntegrationOutbox>(entity =>
        {
            entity.ToTable("km_integration_outbox", table =>
            {
                table.HasCheckConstraint("ck_km_outbox_supported_event",
                    "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'EquipmentDocumentArchived' AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
                table.HasCheckConstraint("ck_km_outbox_version_size", "version > 0 AND size_bytes > 0");
                table.HasCheckConstraint("ck_km_outbox_sent_job",
                    "status NOT IN ('Sent', 'Processing', 'Completed') OR km_job_id IS NOT NULL");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(50).IsRequired();
            entity.Property(x => x.SourceSystem).HasColumnName("source_system").HasMaxLength(30).IsRequired();
            entity.Property(x => x.SourceEntityType).HasColumnName("source_entity_type").HasMaxLength(50).IsRequired();
            entity.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.CaseId).HasColumnName("case_id");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
            entity.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(200).IsRequired();
            entity.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            entity.Property(x => x.CommittedAt).HasColumnName("committed_at");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.KmJobId).HasColumnName("km_job_id").HasMaxLength(150);
            entity.Property(x => x.RetryCount).HasColumnName("retry_count");
            entity.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(100);
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(500);
            entity.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.SentAt).HasColumnName("sent_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(x => x.EventId).IsUnique();
            entity.HasIndex(x => new { x.SourceSystem, x.DocumentId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextRetryAt });
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            entity.HasIndex(x => x.KmJobId).IsUnique().HasFilter("km_job_id IS NOT NULL");
            entity.HasIndex(x => x.SourceEntityId);
        });

        modelBuilder.Entity<KmIntegrationManualRetryAudit>(entity =>
        {
            entity.ToTable("km_integration_manual_retry_audits", table =>
            {
                table.HasCheckConstraint("ck_km_manual_retry_reason",
                    "length(btrim(reason)) BETWEEN 5 AND 500");
                table.HasCheckConstraint("ck_km_manual_retry_action",
                    "action = 'ManualKmSyncRetry' AND previous_status = 'Failed'");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.OutboxId).HasColumnName("outbox_id");
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(100).IsRequired();
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
            entity.Property(x => x.PreviousStatus).HasColumnName("previous_status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.PreviousErrorCode).HasColumnName("previous_error_code").HasMaxLength(100);
            entity.Property(x => x.PreviousErrorMessage).HasColumnName("previous_error_message").HasMaxLength(500);
            entity.Property(x => x.PreviousRetryCount).HasColumnName("previous_retry_count");
            entity.HasIndex(x => new { x.OutboxId, x.RequestedAt });
        });
    }
}

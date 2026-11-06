using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class KmMetadataChangeModelConfiguration
{
    public static void ConfigureKmMetadataChanges(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KmMetadataChange>(entity =>
        {
            entity.ToTable("km_metadata_changes", table =>
            {
                table.HasCheckConstraint("ck_km_metadata_change_source", "source_system = 'CSIT' AND source_type IN ('TestReport','DocumentLibraryFile')");
                table.HasCheckConstraint("ck_km_metadata_change_version", "version >= 1 AND source_revision >= 2");
                table.HasCheckConstraint("ck_km_metadata_change_status", "status IN ('Pending','Dispatching','Sent','Processing','Completed','Failed')");
                table.HasCheckConstraint("ck_km_metadata_change_payload_sha", "length(payload_sha256) = 64");
                table.HasCheckConstraint("ck_km_metadata_change_sent_job", "status NOT IN ('Sent','Processing','Completed') OR km_change_job_id IS NOT NULL");
            });
            entity.HasKey(x => x.ChangeId);
            entity.Property(x => x.ChangeId).HasColumnName("change_id");
            entity.Property(x => x.AnalysisResultId).HasColumnName("analysis_result_id");
            entity.Property(x => x.ReviewRevisionId).HasColumnName("review_revision_id");
            entity.Property(x => x.SourceSystem).HasColumnName("source_system").HasMaxLength(30);
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(80);
            entity.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.SourceRevision).HasColumnName("source_revision");
            entity.Property(x => x.ClassificationJson).HasColumnName("classification_json").HasColumnType("jsonb");
            entity.Property(x => x.AttributesJson).HasColumnName("attributes_json").HasColumnType("jsonb");
            entity.Property(x => x.ChangedFieldsJson).HasColumnName("changed_fields_json").HasColumnType("jsonb");
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasColumnType("char(64)");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.KmChangeJobId).HasColumnName("km_change_job_id").HasMaxLength(150);
            entity.Property(x => x.RetryCount).HasColumnName("retry_count");
            entity.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(100);
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(500);
            entity.Property(x => x.LastErrorRetryable).HasColumnName("last_error_retryable");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.SentAt).HasColumnName("sent_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.AuthorizedBy).HasColumnName("authorized_by").HasMaxLength(100);
            entity.Property(x => x.AuthorizedAt).HasColumnName("authorized_at");
            entity.HasIndex(x => new { x.SourceType, x.SourceEntityId, x.DocumentId, x.Version, x.SourceRevision }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextRetryAt });
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            entity.HasIndex(x => x.KmChangeJobId).IsUnique().HasFilter("km_change_job_id IS NOT NULL");
            entity.HasOne(x => x.AnalysisResult).WithMany().HasForeignKey(x => x.AnalysisResultId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReviewRevision).WithMany().HasForeignKey(x => x.ReviewRevisionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KmMetadataChangeCallbackReceipt>(entity =>
        {
            entity.ToTable("km_metadata_change_callbacks", table =>
            {
                table.HasCheckConstraint("ck_km_metadata_callback_status", "status IN ('Processing','Completed','Failed')");
                table.HasCheckConstraint("ck_km_metadata_callback_revision", "applied_revision >= 0");
                table.HasCheckConstraint("ck_km_metadata_callback_sha", "length(payload_sha256) = 64");
            });
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.ChangeId).HasColumnName("change_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.AppliedRevision).HasColumnName("applied_revision");
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasColumnType("char(64)");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.ChangeId);
            entity.HasOne(x => x.Change).WithMany().HasForeignKey(x => x.ChangeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

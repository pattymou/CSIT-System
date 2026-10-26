using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class VerificationCaseDeletionModelConfiguration
{
    public static void ConfigureVerificationCaseDeletion(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VerificationCaseDeletionJob>(entity =>
        {
            entity.ToTable("verification_case_deletion_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApplicationId).HasColumnName("application_id");
            entity.Property(x => x.RecordId).HasColumnName("record_id");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(100);
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.RecordId).IsUnique();
            entity.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<VerificationCaseDeletionTarget>(entity =>
        {
            entity.ToTable("verification_case_deletion_targets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(60);
            entity.Property(x => x.DocumentFileId).HasColumnName("document_file_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
            entity.Property(x => x.ApplicationId).HasColumnName("application_id");
            entity.Property(x => x.RecordId).HasColumnName("record_id");
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.KmDeletionJobId).HasColumnName("km_deletion_job_id").HasMaxLength(150);
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(100);
            entity.Property(x => x.LastErrorRetryable).HasColumnName("last_error_retryable");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.KmVerifiedAt).HasColumnName("km_verified_at");
            entity.Ignore(x => x.Resource);
            entity.HasIndex(x => new { x.JobId, x.SourceType, x.SourceEntityId, x.Version })
                .IsUnique().HasFilter("source_entity_id IS NOT NULL");
            entity.HasIndex(x => new { x.JobId, x.DocumentFileId }).IsUnique()
                .HasFilter("document_file_id IS NOT NULL");
            entity.HasIndex(x => x.KmDeletionJobId).IsUnique()
                .HasFilter("km_deletion_job_id IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
        });
        modelBuilder.Entity<VerificationCaseDeletionCallbackReceipt>(entity =>
        {
            entity.ToTable("verification_case_deletion_callbacks");
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.TargetId).HasColumnName("target_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.TargetId);
            entity.HasOne<VerificationCaseDeletionTarget>().WithMany().HasForeignKey(x => x.TargetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

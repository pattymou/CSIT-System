using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class DocumentLibraryFileDeletionModelConfiguration
{
    public static void ConfigureDocumentLibraryFileDeletion(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentLibraryFileDeletionJob>(entity =>
        {
            entity.ToTable("document_file_deletion_jobs", table =>
            {
                table.HasCheckConstraint("ck_document_file_deletion_status",
                    "status IN ('Preparing','Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                table.HasCheckConstraint("ck_document_file_deletion_snapshot",
                    "file_size > 0 AND length(sha256) = 64 AND length(btrim(relative_path)) > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DocumentFileId).HasColumnName("document_file_id");
            entity.Property(x => x.FolderId).HasColumnName("folder_id");
            entity.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300);
            entity.Property(x => x.RelativePath).HasColumnName("relative_path").HasMaxLength(1000);
            entity.Property(x => x.StorageArea).HasColumnName("storage_area").HasMaxLength(30);
            entity.Property(x => x.FileSize).HasColumnName("file_size");
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(100);
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.KmDeletionJobId).HasColumnName("km_deletion_job_id").HasMaxLength(150);
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(100);
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(500);
            entity.Property(x => x.LastErrorRetryable).HasColumnName("last_error_retryable");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.KmVerifiedAt).HasColumnName("km_verified_at");
            entity.Property(x => x.DbRecordsDeletedAt).HasColumnName("db_records_deleted_at");
            entity.Property(x => x.NasFileRemovedAt).HasColumnName("nas_file_removed_at");
            entity.Property(x => x.StagingFileRemovedAt).HasColumnName("staging_file_removed_at");
            entity.Property(x => x.ApprovalStagingRemovedAt)
                .HasColumnName("approval_staging_removed_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Ignore(x => x.Resource);
            entity.HasIndex(x => x.DocumentFileId).IsUnique();
            entity.HasIndex(x => x.KmDeletionJobId).IsUnique()
                .HasFilter("km_deletion_job_id IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<DocumentLibraryFileDeletionCallbackReceipt>(entity =>
        {
            entity.ToTable("document_file_deletion_callbacks");
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.DeletionJobId).HasColumnName("deletion_job_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.DeletionJobId);
            entity.HasOne<DocumentLibraryFileDeletionJob>().WithMany()
                .HasForeignKey(x => x.DeletionJobId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_document_file_deletion_callbacks_job");
        });
    }
}

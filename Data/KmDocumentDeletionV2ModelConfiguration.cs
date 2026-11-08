using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class KmDocumentDeletionV2ModelConfiguration
{
    public static void ConfigureKmDocumentDeletionV2(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VendorFileDeletionJob>(entity =>
        {
            entity.ToTable("vendor_file_deletion_jobs", table =>
            {
                table.HasCheckConstraint("ck_vendor_file_deletion_status",
                    "status IN ('Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                table.HasCheckConstraint("ck_vendor_file_deletion_identity",
                    "source_type = 'VendorFile' AND vendor_file_id = document_id AND version = 1 AND reason_code IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.VendorFileId).HasColumnName("vendor_file_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.VendorId).HasColumnName("vendor_id");
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(60);
            entity.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300);
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(40);
            entity.Property(x => x.CanonicalIngestionEventId).HasColumnName("canonical_ingestion_event_id");
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
            entity.Property(x => x.DbRecordDeletedAt).HasColumnName("db_record_deleted_at");
            entity.Property(x => x.PhysicalFileDeletedAt).HasColumnName("physical_file_deleted_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Ignore(x => x.Resource);
            entity.HasIndex(x => x.VendorFileId).IsUnique();
            entity.HasIndex(x => x.KmDeletionJobId).IsUnique()
                .HasFilter("km_deletion_job_id IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<KmDocumentDeletionV2CallbackReceipt>(entity =>
        {
            entity.ToTable("km_v2_document_deletion_callbacks");
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.DeletionRequestId).HasColumnName("deletion_request_id");
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(60);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => new { x.SourceType, x.DeletionRequestId });
        });
    }
}

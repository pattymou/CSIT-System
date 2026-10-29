using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class EquipmentFileDeletionModelConfiguration
{
    public static void ConfigureEquipmentFileDeletion(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EquipmentFileDeletionJob>(entity =>
        {
            entity.ToTable("equipment_file_deletion_jobs", table =>
            {
                table.HasCheckConstraint("ck_equipment_file_deletion_status",
                    "status IN ('Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                table.HasCheckConstraint("ck_equipment_file_deletion_identity",
                    "source_entity_type = 'ApparatusFile' AND apparatus_file_id = document_id AND version = 1");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApparatusFileId).HasColumnName("apparatus_file_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.EquipmentId).HasColumnName("equipment_id").HasMaxLength(30);
            entity.Property(x => x.SourceEntityType).HasColumnName("source_entity_type").HasMaxLength(60);
            entity.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300);
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
            entity.Property(x => x.DbRecordDeletedAt).HasColumnName("db_record_deleted_at");
            entity.Property(x => x.PhysicalFileDeletedAt).HasColumnName("physical_file_deleted_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Ignore(x => x.Resource);
            entity.HasIndex(x => x.ApparatusFileId).IsUnique();
            entity.HasIndex(x => x.KmDeletionJobId).IsUnique()
                .HasFilter("km_deletion_job_id IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<EquipmentFileDeletionCallbackReceipt>(entity =>
        {
            entity.ToTable("equipment_file_deletion_callbacks");
            entity.HasKey(x => x.CallbackId);
            entity.Property(x => x.CallbackId).HasColumnName("callback_id");
            entity.Property(x => x.DeletionJobId).HasColumnName("deletion_job_id");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
            entity.HasIndex(x => x.DeletionJobId);
            entity.HasOne<EquipmentFileDeletionJob>().WithMany().HasForeignKey(x => x.DeletionJobId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

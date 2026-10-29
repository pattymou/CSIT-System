using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class EquipmentDeletionModelConfiguration
{
    public static void ConfigureEquipmentDeletion(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EquipmentDeletionJob>(entity =>
        {
            entity.ToTable("equipment_deletion_jobs", table => table.HasCheckConstraint(
                "ck_equipment_deletion_status",
                "status IN ('WaitingForFiles','Finalizing','CleanupFailed','Blocked','Completed')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.EquipmentId).HasColumnName("equipment_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.EquipmentName).HasColumnName("equipment_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ProductsId).HasColumnName("products_id").HasMaxLength(100);
            entity.Property(x => x.StorageFolderName).HasColumnName("storage_folder_name").HasMaxLength(240);
            entity.Property(x => x.AttachmentCount).HasColumnName("attachment_count");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(100).IsRequired();
            entity.Property(x => x.RequestedAt).HasColumnName("requested_at");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.DbRecordDeletedAt).HasColumnName("db_record_deleted_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(100);
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(500);
            entity.Property(x => x.CleanupAttemptCount).HasColumnName("cleanup_attempt_count");
            entity.HasIndex(x => x.EquipmentId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
        });
    }
}

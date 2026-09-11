using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class VerificationApplicationModelConfiguration
{
    public static void ConfigureVerificationApplicationRouting(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VerificationCategory>(entity =>
        {
            entity.ToTable("verification_categories", table =>
                table.HasCheckConstraint("ck_verification_categories_display_order", "display_order >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ModuleCode).HasColumnName("module_code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.LeaderAccount).HasColumnName("leader_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.LeaderDisplayName).HasColumnName("leader_display_name").HasMaxLength(200);
            entity.Property(x => x.IsActive).HasColumnName("is_active");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.IsActive, x.DisplayOrder });
        });
    }
}

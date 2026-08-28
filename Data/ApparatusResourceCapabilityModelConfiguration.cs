using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

internal static class ApparatusResourceCapabilityModelConfiguration
{
    public static void ConfigureApparatusResourceCapabilities(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApparatusResourceCapability>(entity =>
        {
            entity.ToTable("apparatus_resource_capabilities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ApparatusId).HasColumnName("apparatus_id").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
            entity.Property(x => x.CapabilityTag).HasColumnName("capability_tag").HasMaxLength(200);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(x => x.ResourceType);
            entity.HasIndex(x => new { x.ResourceType, x.CapabilityTag });
            entity.HasIndex(x => x.ApparatusId);
            entity.HasIndex(x => new { x.ApparatusId, x.ResourceType })
                .IsUnique()
                .HasFilter("capability_tag IS NULL");
            entity.HasIndex(x => new { x.ApparatusId, x.ResourceType, x.CapabilityTag })
                .IsUnique()
                .HasFilter("capability_tag IS NOT NULL");

            entity.HasOne(x => x.Apparatus)
                .WithMany(x => x.ResourceCapabilities)
                .HasForeignKey(x => x.ApparatusId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

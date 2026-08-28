using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class SystemMasterDataModelConfiguration
{
    public static void ConfigureSystemMasterData(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TeamRouting>(entity =>
        {
            entity.ToTable("team_routings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TeamOptionId).HasColumnName("team_option_id");
            entity.Property(x => x.LeaderAccount).HasColumnName("leader_account").HasMaxLength(100).IsRequired();
            entity.Property(x => x.LeaderDisplayName).HasColumnName("leader_display_name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(x => x.TeamOptionId).IsUnique();
            entity.HasIndex(x => x.IsEnabled);
        });
    }
}

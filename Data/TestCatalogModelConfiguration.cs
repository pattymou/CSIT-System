using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

internal static class TestCatalogModelConfiguration
{
    public static void ConfigureTestCatalog(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestEnvironment>(entity =>
        {
            entity.ToTable("test_environments", table =>
            {
                table.HasCheckConstraint("ck_test_environments_status", "status IN ('Active', 'Maintenance', 'Disabled')");
                table.HasCheckConstraint("ck_test_environments_booking_mode", "booking_mode IN ('Exclusive', 'Shared')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Site).HasColumnName("site").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.BookingMode).HasColumnName("booking_mode").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.Category, x.Status });
        });

        modelBuilder.Entity<EquipmentGroup>(entity =>
        {
            entity.ToTable("equipment_groups", table =>
                table.HasCheckConstraint("ck_equipment_groups_status", "status IN ('Active', 'Disabled')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<EquipmentGroupRequirement>(entity =>
        {
            entity.ToTable("equipment_group_requirements", table =>
                table.HasCheckConstraint("ck_equipment_group_requirements_quantity", "quantity > 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.EquipmentGroupId).HasColumnName("equipment_group_id");
            entity.Property(x => x.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
            entity.Property(x => x.CapabilityTag).HasColumnName("capability_tag").HasMaxLength(200);
            entity.Property(x => x.Quantity).HasColumnName("quantity");
            entity.Property(x => x.Required).HasColumnName("required");
            entity.Property(x => x.AllowAlternative).HasColumnName("allow_alternative");
            entity.Property(x => x.PreferredEquipmentId).HasColumnName("preferred_equipment_id").HasMaxLength(30);
            entity.HasIndex(x => x.EquipmentGroupId);
            entity.HasIndex(x => x.PreferredEquipmentId);
            entity.HasOne(x => x.EquipmentGroup)
                .WithMany(x => x.Requirements)
                .HasForeignKey(x => x.EquipmentGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.PreferredEquipment)
                .WithMany()
                .HasForeignKey(x => x.PreferredEquipmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TestCapability>(entity =>
        {
            entity.ToTable("test_capabilities", table =>
                table.HasCheckConstraint("ck_test_capabilities_status", "status IN ('Draft', 'Active', 'Retired')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.Category, x.Status });
        });

        modelBuilder.Entity<TestPlanTemplate>(entity =>
        {
            entity.ToTable("test_plan_templates", table =>
                table.HasCheckConstraint("ck_test_plan_templates_status", "status IN ('Draft', 'Published', 'Retired')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).HasColumnName("version").HasMaxLength(50).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.SourceFilePath).HasColumnName("source_file_path").HasMaxLength(1000);
            entity.Property(x => x.StructuredDefinition).HasColumnName("structured_definition");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
            entity.Property(x => x.PublishedAt).HasColumnName("published_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.Code, x.Version }).IsUnique();
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("report_templates", table =>
            {
                table.HasCheckConstraint("ck_report_templates_status", "status IN ('Draft', 'Published', 'Retired')");
                table.HasCheckConstraint("ck_report_templates_type", "template_type IN ('Excel', 'PDF', 'Other')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).HasColumnName("version").HasMaxLength(50).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.TemplateType).HasColumnName("template_type").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.TemplateFilePath).HasColumnName("template_file_path").HasMaxLength(1000);
            entity.Property(x => x.ResultSchema).HasColumnName("result_schema");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.Code, x.Version }).IsUnique();
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<TestExecutionProfile>(entity =>
        {
            entity.ToTable("test_execution_profiles", table =>
            {
                table.HasCheckConstraint("ck_test_execution_profiles_duration", "estimated_duration_minutes > 0");
                table.HasCheckConstraint("ck_test_execution_profiles_automation", "automation_level IN ('Manual', 'SemiAuto', 'Auto')");
                table.HasCheckConstraint("ck_test_execution_profiles_status", "status IN ('Active', 'Disabled')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.TestCapabilityId).HasColumnName("test_capability_id");
            entity.Property(x => x.TestEnvironmentId).HasColumnName("test_environment_id");
            entity.Property(x => x.EquipmentGroupId).HasColumnName("equipment_group_id");
            entity.Property(x => x.TestPlanTemplateId).HasColumnName("test_plan_template_id");
            entity.Property(x => x.ReportTemplateId).HasColumnName("report_template_id");
            entity.Property(x => x.EstimatedDurationMinutes).HasColumnName("estimated_duration_minutes");
            entity.Property(x => x.AutomationLevel).HasColumnName("automation_level").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.IsDefault).HasColumnName("is_default");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.TestCapabilityId, x.Status });
            entity.HasIndex(x => x.TestCapabilityId)
                .IsUnique()
                .HasFilter("status = 'Active' AND is_default");
            entity.HasOne(x => x.TestCapability).WithMany(x => x.ExecutionProfiles).HasForeignKey(x => x.TestCapabilityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestEnvironment).WithMany(x => x.ExecutionProfiles).HasForeignKey(x => x.TestEnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.EquipmentGroup).WithMany(x => x.ExecutionProfiles).HasForeignKey(x => x.EquipmentGroupId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestPlanTemplate).WithMany(x => x.ExecutionProfiles).HasForeignKey(x => x.TestPlanTemplateId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReportTemplate).WithMany(x => x.ExecutionProfiles).HasForeignKey(x => x.ReportTemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlannedTestItem>(entity =>
        {
            entity.ToTable("planned_test_items", table =>
            {
                table.HasCheckConstraint("ck_planned_test_items_duration", "estimated_duration_minutes > 0");
                table.HasCheckConstraint("ck_planned_test_items_source", "planning_source IN ('Agent', 'Manual')");
                table.HasCheckConstraint("ck_planned_test_items_status", "status IN ('Draft', 'WaitingResource', 'Ready', 'Running', 'Completed', 'Returned', 'Cancelled')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ModuleRecordId).HasColumnName("module_record_id");
            entity.Property(x => x.TestCapabilityId).HasColumnName("test_capability_id");
            entity.Property(x => x.TestExecutionProfileId).HasColumnName("test_execution_profile_id");
            entity.Property(x => x.TestEnvironmentId).HasColumnName("test_environment_id");
            entity.Property(x => x.EquipmentGroupId).HasColumnName("equipment_group_id");
            entity.Property(x => x.TestPlanTemplateId).HasColumnName("test_plan_template_id");
            entity.Property(x => x.TestPlanTemplateVersion).HasColumnName("test_plan_template_version").HasMaxLength(50).IsRequired();
            entity.Property(x => x.ReportTemplateId).HasColumnName("report_template_id");
            entity.Property(x => x.ReportTemplateVersion).HasColumnName("report_template_version").HasMaxLength(50).IsRequired();
            entity.Property(x => x.EstimatedDurationMinutes).HasColumnName("estimated_duration_minutes");
            entity.Property(x => x.PlanningSource).HasColumnName("planning_source").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.ModuleRecordId, x.Status });
            entity.HasIndex(x => x.TestExecutionProfileId);
            entity.HasOne(x => x.ModuleRecord).WithMany().HasForeignKey(x => x.ModuleRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestCapability).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.TestCapabilityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestExecutionProfile).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.TestExecutionProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestEnvironment).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.TestEnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.EquipmentGroup).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.EquipmentGroupId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TestPlanTemplate).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.TestPlanTemplateId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReportTemplate).WithMany(x => x.PlannedTestItems).HasForeignKey(x => x.ReportTemplateId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

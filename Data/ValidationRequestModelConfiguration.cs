using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

public static class ValidationRequestModelConfiguration
{
    public static void ConfigureValidationRequests(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ValidationFormTemplate>(entity =>
        {
            entity.ToTable("validation_form_templates");
            entity.HasKey(x => x.Id);
            MapIdAndAudit(entity);
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ValidationFormTemplateVersion>(entity =>
        {
            entity.ToTable("validation_form_template_versions", table => table.HasCheckConstraint(
                "ck_validation_template_version_status", "status IN ('Draft','Published','Inactive')"));
            entity.HasKey(x => x.Id);
            MapIdAndAudit(entity);
            entity.Property(x => x.TemplateId).HasColumnName("template_id");
            entity.Property(x => x.VersionNumber).HasColumnName("version_number");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            entity.Property(x => x.PublishedAt).HasColumnName("published_at");
            entity.HasIndex(x => new { x.TemplateId, x.VersionNumber }).IsUnique();
            entity.HasOne(x => x.Template).WithMany(x => x.Versions).HasForeignKey(x => x.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationProductType>(entity =>
        {
            entity.ToTable("validation_product_types");
            entity.HasKey(x => x.Id);
            MapIdAndAudit(entity);
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(200).IsRequired();
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.TeamOptionId).HasColumnName("team_option_id");
            entity.HasIndex(x => x.TeamOptionId);
            entity.HasOne<SystemOption>().WithMany().HasForeignKey(x => x.TeamOptionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ValidationSectionDefinition>(entity =>
        {
            entity.ToTable("validation_section_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TemplateVersionId).HasColumnName("template_version_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.AlwaysVisible).HasColumnName("always_visible");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.HasIndex(x => new { x.TemplateVersionId, x.Code }).IsUnique();
            entity.HasOne(x => x.TemplateVersion).WithMany(x => x.Sections).HasForeignKey(x => x.TemplateVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationProductTypeSectionMapping>(entity =>
        {
            entity.ToTable("validation_product_type_section_mappings");
            entity.HasKey(x => new { x.ProductTypeId, x.SectionDefinitionId });
            entity.Property(x => x.ProductTypeId).HasColumnName("product_type_id");
            entity.Property(x => x.SectionDefinitionId).HasColumnName("section_definition_id");
            entity.HasOne(x => x.ProductType).WithMany().HasForeignKey(x => x.ProductTypeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.SectionDefinition).WithMany().HasForeignKey(x => x.SectionDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationFieldDefinition>(entity =>
        {
            entity.ToTable("validation_field_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SectionDefinitionId).HasColumnName("section_definition_id");
            entity.Property(x => x.ParentFieldDefinitionId).HasColumnName("parent_field_definition_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(300).IsRequired();
            entity.Property(x => x.HelpText).HasColumnName("help_text");
            entity.Property(x => x.DesignNote).HasColumnName("design_note");
            entity.Property(x => x.FieldType).HasColumnName("field_type").HasMaxLength(30).IsRequired();
            entity.Property(x => x.IsRequired).HasColumnName("is_required");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.IsSensitive).HasColumnName("is_sensitive");
            entity.Property(x => x.DataSource).HasColumnName("data_source").HasMaxLength(200);
            entity.Property(x => x.ValidationJson).HasColumnName("validation_json").HasColumnType("jsonb");
            entity.Property(x => x.WasPublished).HasColumnName("was_published");
            entity.HasIndex(x => new { x.SectionDefinitionId, x.Code }).IsUnique();
            entity.HasOne(x => x.SectionDefinition).WithMany(x => x.Fields).HasForeignKey(x => x.SectionDefinitionId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_validation_fields_section");
            entity.HasOne(x => x.ParentFieldDefinition).WithMany(x => x.Children).HasForeignKey(x => x.ParentFieldDefinitionId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_validation_fields_parent");
        });

        modelBuilder.Entity<ValidationFieldOption>(entity =>
        {
            entity.ToTable("validation_field_options");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.FieldDefinitionId).HasColumnName("field_definition_id");
            entity.Property(x => x.ValueCode).HasColumnName("value_code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(300).IsRequired();
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.WasPublished).HasColumnName("was_published");
            entity.HasIndex(x => new { x.FieldDefinitionId, x.ValueCode }).IsUnique();
            entity.HasOne(x => x.FieldDefinition).WithMany(x => x.Options).HasForeignKey(x => x.FieldDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationFieldCondition>(entity =>
        {
            entity.ToTable("validation_field_conditions", table =>
            {
                table.HasCheckConstraint("ck_validation_condition_operator", "operator IN ('Equals','NotEquals','Contains','NotContains','IsTrue','IsFalse')");
                table.HasCheckConstraint("ck_validation_condition_action", "action IN ('Visible','Required')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TargetFieldDefinitionId).HasColumnName("target_field_definition_id");
            entity.Property(x => x.SourceFieldCode).HasColumnName("source_field_code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.Operator).HasColumnName("operator").HasMaxLength(30).IsRequired();
            entity.Property(x => x.ExpectedValue).HasColumnName("expected_value").HasMaxLength(300);
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.TargetFieldDefinitionId, x.SourceFieldCode, x.Operator, x.ExpectedValue, x.Action }).IsUnique();
            entity.HasOne(x => x.TargetFieldDefinition).WithMany(x => x.Conditions).HasForeignKey(x => x.TargetFieldDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationTestItemCategory>(entity =>
        {
            entity.ToTable("validation_test_item_categories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TemplateVersionId).HasColumnName("template_version_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(30).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(200).IsRequired();
            entity.Property(x => x.AlwaysVisible).HasColumnName("always_visible");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.HasIndex(x => new { x.TemplateVersionId, x.Code }).IsUnique();
            entity.HasOne(x => x.TemplateVersion).WithMany(x => x.TestCategories).HasForeignKey(x => x.TemplateVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationProductTypeTestCategoryMapping>(entity =>
        {
            entity.ToTable("validation_product_type_test_category_mappings");
            entity.HasKey(x => new { x.ProductTypeId, x.TestItemCategoryId });
            entity.Property(x => x.ProductTypeId).HasColumnName("product_type_id");
            entity.Property(x => x.TestItemCategoryId).HasColumnName("test_item_category_id");
            entity.HasOne(x => x.ProductType).WithMany().HasForeignKey(x => x.ProductTypeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TestItemCategory).WithMany().HasForeignKey(x => x.TestItemCategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationTestItemDefinition>(entity =>
        {
            entity.ToTable("validation_test_item_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TestItemCategoryId).HasColumnName("test_item_category_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.DesignNote).HasColumnName("design_note");
            entity.Property(x => x.IsMandatory).HasColumnName("is_mandatory");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.IsArchived).HasColumnName("is_archived");
            entity.Property(x => x.WasPublished).HasColumnName("was_published");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.HasIndex(x => new { x.TestItemCategoryId, x.Code }).IsUnique();
            entity.HasOne(x => x.TestItemCategory).WithMany(x => x.TestItems).HasForeignKey(x => x.TestItemCategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ValidationTestItemParameterDefinition>(entity =>
        {
            entity.ToTable("validation_test_item_parameter_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TestItemDefinitionId).HasColumnName("test_item_definition_id");
            entity.Property(x => x.ParentParameterDefinitionId).HasColumnName("parent_parameter_definition_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(300).IsRequired();
            entity.Property(x => x.HelpText).HasColumnName("help_text");
            entity.Property(x => x.DesignNote).HasColumnName("design_note");
            entity.Property(x => x.FieldType).HasColumnName("field_type").HasMaxLength(30).IsRequired();
            entity.Property(x => x.IsRequired).HasColumnName("is_required");
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.ValidationJson).HasColumnName("validation_json").HasColumnType("jsonb");
            entity.Property(x => x.WasPublished).HasColumnName("was_published");
            entity.HasIndex(x => new { x.TestItemDefinitionId, x.Code }).IsUnique();
            entity.HasOne(x => x.TestItemDefinition).WithMany(x => x.Parameters).HasForeignKey(x => x.TestItemDefinitionId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_validation_parameters_item");
            entity.HasOne(x => x.ParentParameterDefinition).WithMany(x => x.Children).HasForeignKey(x => x.ParentParameterDefinitionId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_validation_parameters_parent");
        });

        modelBuilder.Entity<ValidationTestItemParameterOption>(entity =>
        {
            entity.ToTable("validation_test_item_parameter_options");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ParameterDefinitionId).HasColumnName("parameter_definition_id");
            entity.Property(x => x.ValueCode).HasColumnName("value_code").HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayLabel).HasColumnName("display_label").HasMaxLength(300).IsRequired();
            entity.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            entity.Property(x => x.DisplayOrder).HasColumnName("display_order");
            entity.Property(x => x.WasPublished).HasColumnName("was_published");
            entity.HasIndex(x => new { x.ParameterDefinitionId, x.ValueCode }).IsUnique();
            entity.HasOne(x => x.ParameterDefinition).WithMany(x => x.Options).HasForeignKey(x => x.ParameterDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ValidationRequestSeedData.Configure(modelBuilder);
    }

    private static void MapIdAndAudit<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class
    {
        entity.Property<Guid>("Id").HasColumnName("id");
        entity.Property<DateTime>("CreatedAt").HasColumnName("created_at");
        entity.Property<DateTime>("UpdatedAt").HasColumnName("updated_at");
    }
}

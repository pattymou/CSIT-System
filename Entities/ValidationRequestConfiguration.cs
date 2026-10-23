namespace SIT.DepartmentSystem.Web.Entities;

public static class ValidationTemplateStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Inactive = "Inactive";
}

public static class ValidationFieldTypes
{
    public static readonly string[] All =
    [
        "Text", "TextArea", "SensitiveText", "Number", "Dropdown", "SingleSelect",
        "Checkbox", "MultiSelect", "Attachment", "Group", "RepeatableText",
        "RepeatableGroup", "Lookup"
    ];
}

public static class ValidationConditionOperators
{
    public static readonly string[] All = ["Equals", "NotEquals", "Contains", "NotContains", "IsTrue", "IsFalse"];
}

public static class ValidationConditionActions
{
    public static readonly string[] All = ["Visible", "Required"];
}

public sealed class ValidationFormTemplate
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ValidationFormTemplateVersion> Versions { get; set; } = [];
}

public sealed class ValidationFormTemplateVersion
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public int VersionNumber { get; set; }
    public string Status { get; set; } = ValidationTemplateStatuses.Draft;
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ValidationFormTemplate Template { get; set; } = null!;
    public List<ValidationSectionDefinition> Sections { get; set; } = [];
    public List<ValidationTestItemCategory> TestCategories { get; set; } = [];
}

public sealed class ValidationProductType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public Guid? TeamOptionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ValidationSectionDefinition
{
    public Guid Id { get; set; }
    public Guid TemplateVersionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AlwaysVisible { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public ValidationFormTemplateVersion TemplateVersion { get; set; } = null!;
    public List<ValidationFieldDefinition> Fields { get; set; } = [];
}

public sealed class ValidationProductTypeSectionMapping
{
    public Guid ProductTypeId { get; set; }
    public Guid SectionDefinitionId { get; set; }
    public ValidationProductType ProductType { get; set; } = null!;
    public ValidationSectionDefinition SectionDefinition { get; set; } = null!;
}

public sealed class ValidationFieldDefinition
{
    public Guid Id { get; set; }
    public Guid SectionDefinitionId { get; set; }
    public Guid? ParentFieldDefinitionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public string? DesignNote { get; set; }
    public string FieldType { get; set; } = "Text";
    public bool IsRequired { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public bool IsSensitive { get; set; }
    public string? DataSource { get; set; }
    public string? ValidationJson { get; set; }
    public bool WasPublished { get; set; }
    public ValidationSectionDefinition SectionDefinition { get; set; } = null!;
    public ValidationFieldDefinition? ParentFieldDefinition { get; set; }
    public List<ValidationFieldDefinition> Children { get; set; } = [];
    public List<ValidationFieldOption> Options { get; set; } = [];
    public List<ValidationFieldCondition> Conditions { get; set; } = [];
}

public sealed class ValidationFieldOption
{
    public Guid Id { get; set; }
    public Guid FieldDefinitionId { get; set; }
    public string ValueCode { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public bool WasPublished { get; set; }
    public ValidationFieldDefinition FieldDefinition { get; set; } = null!;
}

public sealed class ValidationFieldCondition
{
    public Guid Id { get; set; }
    public Guid TargetFieldDefinitionId { get; set; }
    public string SourceFieldCode { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string? ExpectedValue { get; set; }
    public string Action { get; set; } = string.Empty;
    public ValidationFieldDefinition TargetFieldDefinition { get; set; } = null!;
}

public sealed class ValidationTestItemCategory
{
    public Guid Id { get; set; }
    public Guid TemplateVersionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public bool AlwaysVisible { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public ValidationFormTemplateVersion TemplateVersion { get; set; } = null!;
    public List<ValidationTestItemDefinition> TestItems { get; set; } = [];
}

public sealed class ValidationProductTypeTestCategoryMapping
{
    public Guid ProductTypeId { get; set; }
    public Guid TestItemCategoryId { get; set; }
    public ValidationProductType ProductType { get; set; } = null!;
    public ValidationTestItemCategory TestItemCategory { get; set; } = null!;
}

public sealed class ValidationTestItemDefinition
{
    public Guid Id { get; set; }
    public Guid TestItemCategoryId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DesignNote { get; set; }
    public bool IsMandatory { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsArchived { get; set; }
    public bool WasPublished { get; set; }
    public int DisplayOrder { get; set; }
    public ValidationTestItemCategory TestItemCategory { get; set; } = null!;
    public List<ValidationTestItemParameterDefinition> Parameters { get; set; } = [];
}

public sealed class ValidationTestItemParameterDefinition
{
    public Guid Id { get; set; }
    public Guid TestItemDefinitionId { get; set; }
    public Guid? ParentParameterDefinitionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public string? DesignNote { get; set; }
    public string FieldType { get; set; } = "Text";
    public bool IsRequired { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public string? ValidationJson { get; set; }
    public bool WasPublished { get; set; }
    public ValidationTestItemDefinition TestItemDefinition { get; set; } = null!;
    public ValidationTestItemParameterDefinition? ParentParameterDefinition { get; set; }
    public List<ValidationTestItemParameterDefinition> Children { get; set; } = [];
    public List<ValidationTestItemParameterOption> Options { get; set; } = [];
}

public sealed class ValidationTestItemParameterOption
{
    public Guid Id { get; set; }
    public Guid ParameterDefinitionId { get; set; }
    public string ValueCode { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public bool WasPublished { get; set; }
    public ValidationTestItemParameterDefinition ParameterDefinition { get; set; } = null!;
}

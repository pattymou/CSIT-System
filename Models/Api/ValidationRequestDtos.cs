namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed record ValidationTemplateVersionDto(Guid Id, int VersionNumber, string Status, DateTime? PublishedAt);
public sealed record ValidationProductTypeDto(Guid Id, string Code, string DisplayLabel, bool IsEnabled, int DisplayOrder,
    Guid? TeamOptionId, string? TeamCode, string? TeamName,
    IReadOnlyList<Guid> SectionIds, IReadOnlyList<Guid> TestCategoryIds);
public sealed record ValidationOptionDto(Guid Id, string ValueCode, string DisplayLabel, bool IsEnabled, int DisplayOrder);
public sealed record ValidationConditionDto(Guid Id, string SourceFieldCode, string Operator, string? ExpectedValue, string Action);
public sealed record ValidationFieldDto(Guid Id, Guid SectionId, Guid? ParentFieldId, string Code, string DisplayLabel,
    string? HelpText, string? DesignNote, string FieldType, bool IsRequired, bool IsEnabled, int DisplayOrder,
    bool IsSensitive, string? DataSource, string? ValidationJson, IReadOnlyList<ValidationOptionDto> Options,
    IReadOnlyList<ValidationConditionDto> Conditions, IReadOnlyList<ValidationFieldDto> Children);
public sealed record ValidationSectionDto(Guid Id, string Code, string DisplayLabel, string? Description,
    bool AlwaysVisible, bool IsEnabled, int DisplayOrder, IReadOnlyList<ValidationFieldDto> Fields);
public sealed record ValidationParameterDto(Guid Id, Guid? ParentParameterId, string Code, string DisplayLabel,
    string? HelpText, string? DesignNote, string FieldType, bool IsRequired, bool IsEnabled, int DisplayOrder,
    string? ValidationJson, IReadOnlyList<ValidationOptionDto> Options, IReadOnlyList<ValidationParameterDto> Children);
public sealed record ValidationTestItemDto(Guid Id, Guid CategoryId, string Code, string DisplayLabel, string? Description,
    string? DesignNote, bool IsMandatory, bool IsEnabled, bool IsArchived, int DisplayOrder,
    IReadOnlyList<ValidationParameterDto> Parameters);
public sealed record ValidationTestCategoryDto(Guid Id, string Code, string DisplayLabel, bool AlwaysVisible,
    bool IsEnabled, int DisplayOrder, IReadOnlyList<ValidationTestItemDto> TestItems);
public sealed record ValidationRequestConfigurationDto(string TemplateCode, string TemplateDisplayName,
    ValidationTemplateVersionDto Version, IReadOnlyList<ValidationTemplateVersionDto> Versions,
    IReadOnlyList<ValidationProductTypeDto> ProductTypes, IReadOnlyList<ValidationSectionDto> Sections,
    IReadOnlyList<ValidationTestCategoryDto> TestCategories, IReadOnlyList<string> SupportedFieldTypes);
public sealed record ValidationRequestPreviewDto(string TemplateCode, Guid TemplateVersionId, int TemplateVersion, string ProductTypeCode,
    string ProductTypeLabel, IReadOnlyList<ValidationSectionDto> Sections,
    IReadOnlyList<ValidationTestCategoryDto> TestCategories);

public sealed class ValidationRequestSubmissionDto
{
    public Guid TemplateVersionId { get; set; }
    public int TemplateVersion { get; set; }
    public string ProductTypeCode { get; set; } = string.Empty;
    public Dictionary<string, List<string>> FieldValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SelectedTestItemCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, List<string>>> TestParameterValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ValidationRequestPreviewDto? FormSnapshot { get; set; }
}

public sealed record SaveValidationProductTypeRequest(string Code, string DisplayLabel, bool IsEnabled, int DisplayOrder, Guid? TeamOptionId = null);
public sealed record SaveValidationFieldRequest(Guid SectionId, Guid? ParentFieldId, string Code, string DisplayLabel,
    string? HelpText, string? DesignNote, string FieldType, bool IsRequired, bool IsEnabled, int DisplayOrder,
    bool IsSensitive, string? DataSource, string? ValidationJson);
public sealed record SaveValidationOptionRequest(string ValueCode, string DisplayLabel, bool IsEnabled, int DisplayOrder);
public sealed record SaveValidationConditionRequest(string SourceFieldCode, string Operator, string? ExpectedValue, string Action);
public sealed record SaveValidationTestItemRequest(Guid CategoryId, string Code, string DisplayLabel, string? Description,
    string? DesignNote, bool IsMandatory, bool IsEnabled, int DisplayOrder);
public sealed record SaveValidationParameterRequest(Guid? ParentParameterId, string Code, string DisplayLabel,
    string? HelpText, string? DesignNote, string FieldType, bool IsRequired, bool IsEnabled, int DisplayOrder,
    string? ValidationJson);
public sealed record SetValidationMappingsRequest(IReadOnlyList<Guid> Ids);

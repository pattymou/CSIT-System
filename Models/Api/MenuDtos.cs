namespace SIT.DepartmentSystem.Web.Models.Api;

public class MenuSectionDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; }
}

public class MenuItemDto
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; }
    public bool AdminOnly { get; set; }
    public string? ModuleCode { get; set; }

    // 新增：是否套用公版三層版面
    public bool UseStandardTemplate { get; set; }
    public string? TemplateType { get; set; }
}

public class MenuItemUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool AdminOnly { get; set; }
    public string? ModuleCode { get; set; }

    // 新增：是否套用公版三層版面
    public bool UseStandardTemplate { get; set; }
    public string? TemplateType { get; set; }
}
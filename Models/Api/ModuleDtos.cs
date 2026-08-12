namespace SIT.DepartmentSystem.Web.Models.Api;

public class ModuleListItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
}

public class ModuleDetailDto : ModuleListItemDto
{
    public string? Description { get; set; }
}

public class ModuleUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
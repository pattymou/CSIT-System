namespace SIT.DepartmentSystem.Web.Entities;

public class ModuleEntity
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ModuleRecord> Records { get; set; } = new List<ModuleRecord>();
}
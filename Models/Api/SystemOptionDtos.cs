namespace SIT.DepartmentSystem.Web.Models.Api;

public class SystemOptionDto
{
    public Guid Id { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public int Sort { get; set; }

    public bool IsEnabled { get; set; }

    public string? Note { get; set; }
}

public class SystemOptionUpsertRequest
{
    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Value { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? Note { get; set; }
}
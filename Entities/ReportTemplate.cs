namespace SIT.DepartmentSystem.Web.Entities;

public class ReportTemplate
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public TemplateStatus Status { get; set; } = TemplateStatus.Draft;
    public ReportTemplateType TemplateType { get; set; } = ReportTemplateType.Other;
    public string? TemplateFilePath { get; set; }
    public string? ResultSchema { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TestExecutionProfile> ExecutionProfiles { get; set; } = new List<TestExecutionProfile>();
    public ICollection<PlannedTestItem> PlannedTestItems { get; set; } = new List<PlannedTestItem>();
}

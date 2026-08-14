namespace SIT.DepartmentSystem.Web.Entities;

public class TestCapability
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TestCapabilityStatus Status { get; set; } = TestCapabilityStatus.Draft;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TestExecutionProfile> ExecutionProfiles { get; set; } = new List<TestExecutionProfile>();
    public ICollection<PlannedTestItem> PlannedTestItems { get; set; } = new List<PlannedTestItem>();
}

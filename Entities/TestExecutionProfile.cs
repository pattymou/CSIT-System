namespace SIT.DepartmentSystem.Web.Entities;

public class TestExecutionProfile
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid TestCapabilityId { get; set; }
    public Guid TestEnvironmentId { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public Guid TestPlanTemplateId { get; set; }
    public Guid ReportTemplateId { get; set; }
    public int EstimatedDurationMinutes { get; set; }
    public AutomationLevel AutomationLevel { get; set; } = AutomationLevel.Manual;
    public bool IsDefault { get; set; }
    public TestExecutionProfileStatus Status { get; set; } = TestExecutionProfileStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public TestCapability TestCapability { get; set; } = null!;
    public TestEnvironment TestEnvironment { get; set; } = null!;
    public EquipmentGroup EquipmentGroup { get; set; } = null!;
    public TestPlanTemplate TestPlanTemplate { get; set; } = null!;
    public ReportTemplate ReportTemplate { get; set; } = null!;
    public ICollection<PlannedTestItem> PlannedTestItems { get; set; } = new List<PlannedTestItem>();
}

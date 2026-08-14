namespace SIT.DepartmentSystem.Web.Entities;

public class PlannedTestItem
{
    public Guid Id { get; set; }
    public Guid ModuleRecordId { get; set; }
    public Guid TestCapabilityId { get; set; }
    public Guid TestExecutionProfileId { get; set; }
    public Guid TestEnvironmentId { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public Guid TestPlanTemplateId { get; set; }
    public string TestPlanTemplateVersion { get; set; } = string.Empty;
    public Guid ReportTemplateId { get; set; }
    public string ReportTemplateVersion { get; set; } = string.Empty;
    public int EstimatedDurationMinutes { get; set; }
    public PlanningSource PlanningSource { get; set; }
    public PlannedTestItemStatus Status { get; private set; } = PlannedTestItemStatus.Draft;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ModuleRecord ModuleRecord { get; set; } = null!;
    public TestCapability TestCapability { get; set; } = null!;
    public TestExecutionProfile TestExecutionProfile { get; set; } = null!;
    public TestEnvironment TestEnvironment { get; set; } = null!;
    public EquipmentGroup EquipmentGroup { get; set; } = null!;
    public TestPlanTemplate TestPlanTemplate { get; set; } = null!;
    public ReportTemplate ReportTemplate { get; set; } = null!;

    public void ChangeStatus(PlannedTestItemStatus next, DateTime now)
    {
        if (!IsAllowedTransition(Status, next))
        {
            throw new InvalidOperationException($"Cannot transition planned test item from {Status} to {next}.");
        }

        Status = next;
        UpdatedAt = now;
    }

    private static bool IsAllowedTransition(PlannedTestItemStatus current, PlannedTestItemStatus next) =>
        (current, next) switch
        {
            (PlannedTestItemStatus.Draft, PlannedTestItemStatus.WaitingResource or PlannedTestItemStatus.Cancelled) => true,
            (PlannedTestItemStatus.WaitingResource, PlannedTestItemStatus.Ready or PlannedTestItemStatus.Returned or PlannedTestItemStatus.Cancelled) => true,
            (PlannedTestItemStatus.Ready, PlannedTestItemStatus.Running or PlannedTestItemStatus.Returned or PlannedTestItemStatus.Cancelled) => true,
            (PlannedTestItemStatus.Running, PlannedTestItemStatus.Completed or PlannedTestItemStatus.Returned or PlannedTestItemStatus.Cancelled) => true,
            (PlannedTestItemStatus.Returned, PlannedTestItemStatus.WaitingResource or PlannedTestItemStatus.Cancelled) => true,
            _ => false
        };
}

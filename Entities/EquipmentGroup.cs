namespace SIT.DepartmentSystem.Web.Entities;

public class EquipmentGroup
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public EquipmentGroupStatus Status { get; set; } = EquipmentGroupStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<EquipmentGroupRequirement> Requirements { get; set; } = new List<EquipmentGroupRequirement>();
    public ICollection<TestExecutionProfile> ExecutionProfiles { get; set; } = new List<TestExecutionProfile>();
    public ICollection<PlannedTestItem> PlannedTestItems { get; set; } = new List<PlannedTestItem>();
}

public class EquipmentGroupRequirement
{
    public Guid Id { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public int Quantity { get; set; }
    public bool Required { get; set; } = true;
    public bool AllowAlternative { get; set; } = true;
    public string? PreferredEquipmentId { get; set; }

    public EquipmentGroup EquipmentGroup { get; set; } = null!;
    public Apparatus? PreferredEquipment { get; set; }
}

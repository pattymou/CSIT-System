namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed class ResourceSchedulerProposalRequest
{
    public Guid TestExecutionProfileId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class ResourceAssignmentProposal
{
    public bool IsFeasible { get; set; }
    public SchedulerCatalogReferenceDto TestExecutionProfile { get; set; } = new();
    public SchedulerCatalogReferenceDto TestEnvironment { get; set; } = new();
    public SchedulerCatalogReferenceDto EquipmentGroup { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<ResourceAssignmentRequirementDto> Requirements { get; set; } = new();
    public string? PolicyFailure { get; set; }
}

public sealed class SchedulerCatalogReferenceDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ResourceAssignmentRequirementDto
{
    public Guid EquipmentGroupRequirementId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public int Quantity { get; set; }
    public bool Required { get; set; }
    public bool AllowAlternative { get; set; }
    public string? PreferredEquipmentId { get; set; }
    public List<ResourceAssignmentApparatusDto> SelectedApparatus { get; set; } = new();
    public int UnresolvedQuantity { get; set; }
    public string? FailureReason { get; set; }
    public string? Note { get; set; }
}

public sealed class ResourceAssignmentApparatusDto
{
    public string ApparatusId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Place { get; set; }
    public string? Custodian { get; set; }
}

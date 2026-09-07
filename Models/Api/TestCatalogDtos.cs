using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Models.Api;

public class TestEnvironmentUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TestEnvironmentStatus Status { get; set; } = TestEnvironmentStatus.Active;
    public BookingMode BookingMode { get; set; } = BookingMode.Exclusive;
}

public class TestEnvironmentDto : TestEnvironmentUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EquipmentGroupUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid OwnerTeamOptionId { get; set; }
    public string Site { get; set; } = string.Empty;
    public string? Description { get; set; }
    public EquipmentGroupStatus Status { get; set; } = EquipmentGroupStatus.Active;
}

public class EquipmentGroupDto : EquipmentGroupUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string OwnerTeamCode { get; set; } = string.Empty;
    public string OwnerTeamName { get; set; } = string.Empty;
    public int TotalDeviceCount { get; set; }
    public int InEnvironmentDeviceCount { get; set; }
    public EquipmentGroupCompletenessStatus CompletenessStatus { get; set; }
    public List<EquipmentGroupMissingDeviceDto> MissingDevices { get; set; } = new();
    public List<EquipmentGroupRequirementDto> Requirements { get; set; } = new();
}

public sealed class EquipmentGroupMissingDeviceDto
{
    public string ApparatusId { get; set; } = string.Empty;
    public string ApparatusName { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
}

public sealed class EquipmentGroupDeviceDto
{
    public Guid Id { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public string EquipmentGroupCode { get; set; } = string.Empty;
    public string EquipmentGroupName { get; set; } = string.Empty;
    public Guid OwnerTeamOptionId { get; set; }
    public string OwnerTeamName { get; set; } = string.Empty;
    public string ApparatusId { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string ApparatusName { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? Custodian { get; set; }
    public string? CustodianAccount { get; set; }
    public string? ReservationStatus { get; set; }
    public string? Place { get; set; }
    public bool IsInEnvironment { get; set; }
    public DateTime AddedAt { get; set; }
    public string AddedBy { get; set; } = string.Empty;
    public DateTime? PresenceUpdatedAt { get; set; }
    public string? PresenceUpdatedBy { get; set; }
    public string? Note { get; set; }
    public string? Warning { get; set; }
    public bool CanUpdatePresence { get; set; }
}

public sealed class EquipmentGroupDeviceCandidateDto
{
    public string ApparatusId { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string ApparatusName { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? Custodian { get; set; }
    public string? CustodianAccount { get; set; }
    public string? Place { get; set; }
    public string? ReservationStatus { get; set; }
    public Guid? OwnerTeamOptionId { get; set; }
    public string? OwnerTeamName { get; set; }
    public Guid? ActiveEquipmentGroupId { get; set; }
    public string? ActiveEquipmentGroupName { get; set; }
    public bool CanAdd { get; set; }
    public string? UnavailableReason { get; set; }
}

public sealed class AddEquipmentGroupDevicesRequest
{
    public List<string> ApparatusIds { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class UpdateEquipmentGroupDevicePresenceRequest
{
    public bool IsInEnvironment { get; set; }
    public string? Note { get; set; }
}

public sealed class ApparatusEnvironmentAssignmentDto
{
    public Guid MembershipId { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public string EquipmentGroupCode { get; set; } = string.Empty;
    public string EquipmentGroupName { get; set; } = string.Empty;
    public Guid OwnerTeamOptionId { get; set; }
    public string OwnerTeamName { get; set; } = string.Empty;
    public bool IsInEnvironment { get; set; }
    public bool CanUpdatePresence { get; set; }
    public string? Warning { get; set; }
}

public class EquipmentGroupRequirementUpsertRequest
{
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Required { get; set; } = true;
    public bool AllowAlternative { get; set; } = true;
    public string? PreferredEquipmentId { get; set; }
}

public class EquipmentGroupRequirementDto : EquipmentGroupRequirementUpsertRequest
{
    public Guid Id { get; set; }
    public Guid EquipmentGroupId { get; set; }
    public string? PreferredEquipmentName { get; set; }
    public string? PreferredEquipmentProductsId { get; set; }
}

public sealed class EquipmentGroupManagementOptionsDto
{
    public bool IsAdmin { get; set; }
    public List<SystemOptionDto> Teams { get; set; } = new();
}

public sealed class EquipmentGroupEquipmentOptionDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string ReservationStatus { get; set; } = string.Empty;
}

public class TestCapabilityUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TestCapabilityStatus Status { get; set; } = TestCapabilityStatus.Draft;
}

public class TestCapabilityDto : TestCapabilityUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TestPlanTemplateUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public TemplateStatus Status { get; set; } = TemplateStatus.Draft;
    public string? SourceFilePath { get; set; }
    public string? StructuredDefinition { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public class TestPlanTemplateDto : TestPlanTemplateUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ReportTemplateUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public TemplateStatus Status { get; set; } = TemplateStatus.Draft;
    public ReportTemplateType TemplateType { get; set; } = ReportTemplateType.Other;
    public string? TemplateFilePath { get; set; }
    public string? ResultSchema { get; set; }
}

public class ReportTemplateDto : ReportTemplateUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TestExecutionProfileUpsertRequest
{
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
}

public class TestExecutionProfileDto : TestExecutionProfileUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PlannedTestItemCreateRequest
{
    public Guid ModuleRecordId { get; set; }
    public Guid TestExecutionProfileId { get; set; }
    public PlanningSource PlanningSource { get; set; } = PlanningSource.Manual;
}

public class PlannedTestItemDto
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
    public PlannedTestItemStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

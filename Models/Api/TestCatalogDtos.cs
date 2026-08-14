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
    public string? Description { get; set; }
    public EquipmentGroupStatus Status { get; set; } = EquipmentGroupStatus.Active;
}

public class EquipmentGroupDto : EquipmentGroupUpsertRequest
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<EquipmentGroupRequirementDto> Requirements { get; set; } = new();
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

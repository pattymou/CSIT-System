using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Models.Api;

public enum ReservationMode { Direct, Environment }

public sealed class CreateReservationRequest
{
    public ReservationMode Mode { get; set; } = ReservationMode.Direct;
    public string Purpose { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<ReservationItemRequest> Items { get; set; } = new();
    public Guid? TestExecutionProfileId { get; set; }
    public List<ReservationRequirementSelectionRequest> Selections { get; set; } = new();
    public string ApplicantExtension { get; set; } = string.Empty;
    public string? ProductModelName { get; set; }
    public string? Customer { get; set; }
    public string? ProjectSubPu { get; set; }
    public string? Note { get; set; }
    public string? ApplicantAgentName { get; set; }
    public string? ApplicantAgentExtension { get; set; }
    public string? ApplicantAgentEmail { get; set; }
}

public sealed class ReservationItemRequest
{
    public string ApparatusId { get; set; } = string.Empty;
}

public sealed class UpdateReservationRequest
{
    public ReservationMode Mode { get; set; } = ReservationMode.Direct;
    public string Purpose { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<ReservationItemRequest> Items { get; set; } = new();
    public Guid? TestExecutionProfileId { get; set; }
    public List<ReservationRequirementSelectionRequest> Selections { get; set; } = new();
    public string ApplicantExtension { get; set; } = string.Empty;
    public string? ProductModelName { get; set; }
    public string? Customer { get; set; }
    public string? ProjectSubPu { get; set; }
    public string? Note { get; set; }
    public string? ApplicantAgentName { get; set; }
    public string? ApplicantAgentExtension { get; set; }
    public string? ApplicantAgentEmail { get; set; }
}

public sealed class ReservationRequirementSelectionRequest
{
    public Guid EquipmentGroupRequirementId { get; set; }
    public List<string> ApparatusIds { get; set; } = new();
}

public sealed class ReservationListDto
{
    public Guid Id { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public string ApplicantAccount { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string ApplicantDepartment { get; set; } = string.Empty;
    public string? ApplicantExtension { get; set; }
    public string? ApplicantAgentName { get; set; }
    public string? ApplicantAgentExtension { get; set; }
    public string? ApplicantAgentEmail { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? ProductModelName { get; set; }
    public string? Customer { get; set; }
    public string? ProjectSubPu { get; set; }
    public string? Note { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public ReservationStatus Status { get; set; }
    public ReservationMode Mode { get; set; }
    public int ItemCount { get; set; }
    public List<string> ApparatusNames { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? BorrowedAt { get; set; }
}

public enum ReservationOverdueCategory
{
    OverdueUnreturned
}

public sealed class ReservationOverdueResponseDto
{
    public int TotalCount { get; set; }
    public int OverdueReturnCount { get; set; }
    public List<ReservationOverdueItemDto> Items { get; set; } = new();
}

public sealed class ReservationOverdueItemDto
{
    public Guid ReservationId { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public ReservationOverdueCategory Category { get; set; }
    public ReservationStatus ReservationStatus { get; set; }
    public string ApplicantName { get; set; } = string.Empty;
    public string ApplicantDepartment { get; set; } = string.Empty;
    public string? ApplicantExtension { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime? BorrowedAt { get; set; }
    public List<ReservationOverdueApparatusDto> VisibleApparatus { get; set; } = new();
    public int TotalReservationItemCount { get; set; }
    public int VisibleApparatusCount { get; set; }
}

public sealed class ReservationOverdueApparatusDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Place { get; set; }
    public string? Custodian { get; set; }
    public string? CustodianAccount { get; set; }
    public Guid? OwnerTeamOptionId { get; set; }
    public string? OwnerTeamName { get; set; }
}

public sealed class ReservationDetailDto
{
    public Guid Id { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public string ApplicantAccount { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string ApplicantDepartment { get; set; } = string.Empty;
    public string? ApplicantEmail { get; set; }
    public string? ApplicantExtension { get; set; }
    public string? ApplicantAgentName { get; set; }
    public string? ApplicantAgentExtension { get; set; }
    public string? ApplicantAgentEmail { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? ProductModelName { get; set; }
    public string? Customer { get; set; }
    public string? ProjectSubPu { get; set; }
    public string? Note { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public ReservationStatus Status { get; set; }
    public ReservationMode Mode { get; set; }
    public Guid? TestExecutionProfileId { get; set; }
    public Guid? TestEnvironmentId { get; set; }
    public Guid? EquipmentGroupId { get; set; }
    public string? TestEnvironmentCodeSnapshot { get; set; }
    public string? TestEnvironmentNameSnapshot { get; set; }
    public string? EquipmentGroupCodeSnapshot { get; set; }
    public string? EquipmentGroupNameSnapshot { get; set; }
    public string? TestExecutionProfileCodeSnapshot { get; set; }
    public string? TestExecutionProfileNameSnapshot { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectReason { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTime? BorrowedAt { get; set; }
    public string? BorrowedBy { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public string? ReturnedBy { get; set; }
    public List<ReservationItemDto> Items { get; set; } = new();
    public List<ReservationExtensionRequestDto> ExtensionRequests { get; set; } = new();
    public List<ReservationAuditEventDto> AuditEvents { get; set; } = new();
    public bool IsOverdue { get; set; }
}

public sealed class ReservationItemDto
{
    public Guid Id { get; set; }
    public string ApparatusId { get; set; } = string.Empty;
    public string ApparatusName { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? Place { get; set; }
    public string? Custodian { get; set; }
    public string? CustodianDepartment { get; set; }
    public string? PriceUse { get; set; }
    public Guid? EquipmentGroupRequirementId { get; set; }
    public string? RequirementResourceTypeSnapshot { get; set; }
    public string? RequirementCapabilityTagSnapshot { get; set; }
}

public sealed class ReservationEnvironmentOptionDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<ReservationProfileOptionDto> Profiles { get; set; } = new();
}

public sealed class ReservationApplicationOptionsDto
{
    public ReservationApplicantSnapshotDto Applicant { get; set; } = new();
    public List<ReservationOptionDto> Customers { get; set; } = new();
    public List<ReservationOptionDto> SubPus { get; set; } = new();
}

public sealed class ReservationApplicantSnapshotDto
{
    public string Name { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string? Email { get; set; }
}

public sealed class ReservationOptionDto
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class ReservationProfileOptionDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid TestCapabilityId { get; set; }
    public string TestCapabilityCode { get; set; } = string.Empty;
    public string TestCapabilityName { get; set; } = string.Empty;
    public Guid EquipmentGroupId { get; set; }
    public string EquipmentGroupCode { get; set; } = string.Empty;
    public string EquipmentGroupName { get; set; } = string.Empty;
    public List<ReservationRequirementOptionDto> Requirements { get; set; } = new();
}

public sealed class ReservationRequirementOptionDto
{
    public Guid Id { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string? CapabilityTag { get; set; }
    public int Quantity { get; set; }
    public bool Required { get; set; }
    public bool AllowAlternative { get; set; }
    public string? PreferredEquipmentId { get; set; }
    public string? PreferredEquipmentName { get; set; }
    public bool PreferredEquipmentBookable { get; set; }
}

public sealed class ReservationTransitionRequest
{
    public string? Reason { get; set; }
}

public sealed class ReservationOverviewQuery
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? ApparatusId { get; set; }
    public string? Department { get; set; }
    public ReservationStatus? Status { get; set; }
    public string? Borrower { get; set; }
    public bool IncludeHistory { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class ReservationOverviewPageDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ReservationOverviewDto> Items { get; set; } = new();
}

public sealed class ReservationOverviewDto
{
    public Guid ReservationId { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public ReservationStatus Status { get; set; }
    public string ApplicantAccount { get; set; } = string.Empty;
    public string ApplicantDepartment { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string? ApplicantExtension { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public bool IsOverdue { get; set; }
    public ReservationMode Mode { get; set; }
    public string? TestEnvironmentName { get; set; }
    public string? EquipmentGroupName { get; set; }
    public string? TestExecutionProfileName { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ReservationOverviewApparatusDto> Apparatus { get; set; } = new();
}

public sealed class ReservationOverviewApparatusDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
}

public sealed class ReservationExtensionCreateRequest
{
    public DateTime RequestedEndTime { get; set; }
}

public sealed class ReservationExtensionReviewRequest
{
    public string? Reason { get; set; }
}

public sealed class ReservationExtensionRequestDto
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public DateTime CurrentEndTimeSnapshot { get; set; }
    public DateTime RequestedEndTime { get; set; }
    public string RequestedByAccount { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public ReservationExtensionRequestStatus Status { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedByAccount { get; set; }
    public string? ReviewedByName { get; set; }
    public string? RejectReason { get; set; }
    public string ApplicantDepartment { get; set; } = string.Empty;
    public string? ApplicantExtension { get; set; }
    public List<string> ApparatusNames { get; set; } = new();
}

public sealed class ReservationAuditEventDto
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public ReservationStatus? FromStatus { get; set; }
    public ReservationStatus? ToStatus { get; set; }
    public string ActorAccount { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
    public string? Details { get; set; }
}

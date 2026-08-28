namespace SIT.DepartmentSystem.Web.Entities;

public class Reservation
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
    public ReservationStatus Status { get; private set; } = ReservationStatus.Draft;

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

    public DateTime? ApprovedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTime? RejectedAt { get; private set; }
    public string? RejectedBy { get; private set; }
    public string? RejectReason { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? CancelledBy { get; private set; }
    public string? CancelReason { get; private set; }
    public DateTime? BorrowedAt { get; private set; }
    public string? BorrowedBy { get; private set; }
    public DateTime? ReturnedAt { get; private set; }
    public string? ReturnedBy { get; private set; }

    public List<ReservationItem> Items { get; set; } = new();
    public List<ReservationExtensionRequest> ExtensionRequests { get; set; } = new();
    public List<ReservationAuditEvent> AuditEvents { get; set; } = new();
    public TestExecutionProfile? TestExecutionProfile { get; set; }
    public TestEnvironment? TestEnvironment { get; set; }
    public EquipmentGroup? EquipmentGroup { get; set; }

    public void Submit(DateTime now)
    {
        EnsureStatus(ReservationStatus.Draft);
        if (Items.Count == 0) throw new InvalidOperationException("A reservation must contain at least one apparatus.");
        RequireForSubmission(Purpose, "Purpose");
        RequireForSubmission(ProductModelName, "ProductModelName");
        RequireForSubmission(Customer, "Customer");
        RequireForSubmission(ProjectSubPu, "ProjectSubPu");
        RequireForSubmission(ApplicantExtension, "ApplicantExtension");
        RequireForSubmission(ApplicantAgentName, "ApplicantAgentName");
        RequireForSubmission(ApplicantAgentExtension, "ApplicantAgentExtension");
        RequireForSubmission(ApplicantAgentEmail, "ApplicantAgentEmail");
        Status = ReservationStatus.Pending;
        UpdatedAt = now;
    }

    public void UpdateDraft(
        string purpose,
        string? productModelName,
        string? customer,
        string? projectSubPu,
        string? note,
        DateTime startTime,
        DateTime endTime,
        string applicantExtension,
        string? applicantAgentName,
        string? applicantAgentExtension,
        string? applicantAgentEmail,
        IReadOnlyCollection<ReservationItem> items,
        ReservationEnvironmentContext? environmentContext,
        DateTime now)
    {
        EnsureStatus(ReservationStatus.Draft);
        if (items.Count == 0) throw new InvalidOperationException("A reservation must contain at least one apparatus.");

        Purpose = purpose;
        ProductModelName = Clean(productModelName);
        Customer = Clean(customer);
        ProjectSubPu = Clean(projectSubPu);
        Note = Clean(note);
        StartTime = startTime;
        EndTime = endTime;
        ApplicantExtension = applicantExtension;
        ApplicantAgentName = Clean(applicantAgentName);
        ApplicantAgentExtension = Clean(applicantAgentExtension);
        ApplicantAgentEmail = Clean(applicantAgentEmail);
        Items.Clear();
        Items.AddRange(items);
        ApplyEnvironmentContext(environmentContext);
        UpdatedAt = now;
    }

    public void ApplyEnvironmentContext(ReservationEnvironmentContext? context)
    {
        TestExecutionProfileId = context?.TestExecutionProfileId;
        TestEnvironmentId = context?.TestEnvironmentId;
        EquipmentGroupId = context?.EquipmentGroupId;
        TestEnvironmentCodeSnapshot = context?.TestEnvironmentCode;
        TestEnvironmentNameSnapshot = context?.TestEnvironmentName;
        EquipmentGroupCodeSnapshot = context?.EquipmentGroupCode;
        EquipmentGroupNameSnapshot = context?.EquipmentGroupName;
        TestExecutionProfileCodeSnapshot = context?.TestExecutionProfileCode;
        TestExecutionProfileNameSnapshot = context?.TestExecutionProfileName;
    }

    public void Approve(string account, DateTime now)
    {
        EnsureStatus(ReservationStatus.Pending);
        Status = ReservationStatus.Approved;
        ApprovedAt = now;
        ApprovedBy = account;
        UpdatedAt = now;
    }

    public void Reject(string account, string? reason, DateTime now)
    {
        EnsureStatus(ReservationStatus.Pending);
        Status = ReservationStatus.Rejected;
        RejectedAt = now;
        RejectedBy = account;
        RejectReason = Clean(reason);
        UpdatedAt = now;
    }

    public void Cancel(string account, string? reason, DateTime now)
    {
        if (Status is not ReservationStatus.Pending and not ReservationStatus.Approved)
            throw new InvalidOperationException(
                $"Cannot transition reservation from {Status}; expected Pending or Approved.");
        Status = ReservationStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = account;
        CancelReason = Clean(reason);
        UpdatedAt = now;
    }

    public void Checkout(string account, DateTime now)
    {
        EnsureStatus(ReservationStatus.Approved);
        Status = ReservationStatus.Borrowed;
        BorrowedAt = now;
        BorrowedBy = account;
        UpdatedAt = now;
    }

    public void Return(string account, DateTime now)
    {
        EnsureStatus(ReservationStatus.Borrowed);
        Status = ReservationStatus.Returned;
        ReturnedAt = now;
        ReturnedBy = account;
        UpdatedAt = now;
    }

    public void ExtendEndTime(DateTime requestedEndTime, DateTime now)
    {
        if (Status is not ReservationStatus.Approved and not ReservationStatus.Borrowed)
            throw new InvalidOperationException("Only an Approved or Borrowed reservation can be extended.");
        if (requestedEndTime <= EndTime)
            throw new InvalidOperationException("RequestedEndTime must be later than the current EndTime.");
        EndTime = requestedEndTime;
        UpdatedAt = now;
    }

    private void EnsureStatus(ReservationStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Cannot transition reservation from {Status}; expected {expected}.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void RequireForSubmission(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    }
}

public sealed record ReservationEnvironmentContext(
    Guid TestExecutionProfileId,
    Guid TestEnvironmentId,
    Guid EquipmentGroupId,
    string TestEnvironmentCode,
    string TestEnvironmentName,
    string EquipmentGroupCode,
    string EquipmentGroupName,
    string TestExecutionProfileCode,
    string TestExecutionProfileName);

namespace SIT.DepartmentSystem.Web.Entities;

public static class ReservationAuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string CheckedOut = "CheckedOut";
    public const string Returned = "Returned";
    public const string ExtensionRequested = "ExtensionRequested";
    public const string ExtensionApproved = "ExtensionApproved";
    public const string ExtensionRejected = "ExtensionRejected";
    public const string ExtensionCancelled = "ExtensionCancelled";
}

public sealed class ReservationAuditEvent
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public ReservationStatus? FromStatus { get; set; }
    public ReservationStatus? ToStatus { get; set; }
    public string ActorAccount { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
    public string? Details { get; set; }
    public Reservation Reservation { get; set; } = null!;
}

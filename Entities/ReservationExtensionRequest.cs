namespace SIT.DepartmentSystem.Web.Entities;

public enum ReservationExtensionRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled
}

public sealed class ReservationExtensionRequest
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public DateTime CurrentEndTimeSnapshot { get; set; }
    public DateTime RequestedEndTime { get; set; }
    public string RequestedByAccount { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public ReservationExtensionRequestStatus Status { get; private set; } = ReservationExtensionRequestStatus.Pending;
    public DateTime? ReviewedAt { get; private set; }
    public string? ReviewedByAccount { get; private set; }
    public string? ReviewedByName { get; private set; }
    public string? RejectReason { get; private set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Reservation Reservation { get; set; } = null!;

    public void Approve(string account, string name, DateTime now)
    {
        EnsurePending();
        Status = ReservationExtensionRequestStatus.Approved;
        ReviewedAt = UpdatedAt = now;
        ReviewedByAccount = account;
        ReviewedByName = name;
    }

    public void Reject(string account, string name, string reason, DateTime now)
    {
        EnsurePending();
        Status = ReservationExtensionRequestStatus.Rejected;
        ReviewedAt = UpdatedAt = now;
        ReviewedByAccount = account;
        ReviewedByName = name;
        RejectReason = string.IsNullOrWhiteSpace(reason)
            ? throw new InvalidOperationException("Reject reason is required.")
            : reason.Trim();
    }

    public void Cancel(DateTime now)
    {
        EnsurePending();
        Status = ReservationExtensionRequestStatus.Cancelled;
        UpdatedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != ReservationExtensionRequestStatus.Pending)
            throw new InvalidOperationException("Only a Pending extension request can be processed.");
    }
}

namespace SIT.DepartmentSystem.Web.Entities;

public sealed class KmIntegrationManualRetryAudit
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OutboxId { get; private set; }
    public Guid EventId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string Action { get; private set; } = "ManualKmSyncRetry";
    public string PreviousStatus { get; private set; } = string.Empty;
    public string? PreviousErrorCode { get; private set; }
    public string? PreviousErrorMessage { get; private set; }
    public int PreviousRetryCount { get; private set; }

    private KmIntegrationManualRetryAudit() { }

    public static KmIntegrationManualRetryAudit Create(KmIntegrationOutbox row, string actor,
        string reason, DateTime now) => new()
    {
        OutboxId = row.Id, EventId = row.EventId, DocumentId = row.DocumentId,
        Version = row.Version, RequestedBy = actor.Trim(), RequestedAt = now,
        Reason = reason.Trim(), PreviousStatus = row.Status.ToString(),
        PreviousErrorCode = row.LastErrorCode, PreviousErrorMessage = SafeErrorMessage(row.LastErrorMessage),
        PreviousRetryCount = row.RetryCount
    };

    public static string? SafeErrorMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null :
        new[] { "token", "secret", "password", "credential", "bearer" }
            .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? "Sensitive error details redacted." : message;
}

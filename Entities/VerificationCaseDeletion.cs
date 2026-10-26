using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Entities;

public sealed class VerificationCaseDeletionJob
{
    public Guid Id { get; private set; }
    public Guid ApplicationId { get; private set; }
    public Guid RecordId { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    // A case is never marked deleted by a KM 202 or callback.
    public string Status { get; private set; } = "Prepared";
    public DateTime UpdatedAt { get; private set; }
    public List<VerificationCaseDeletionTarget> Targets { get; private set; } = [];

    private VerificationCaseDeletionJob() { }

    public static VerificationCaseDeletionJob Prepare(Guid applicationId, Guid recordId,
        string requestedBy, string reason, DateTime now)
    {
        if (applicationId == Guid.Empty || recordId == Guid.Empty ||
            string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length is < 5 or > 500 ||
            System.Text.RegularExpressions.Regex.IsMatch(reason,
                @"(?i)(password|token|secret)\s*[:=]|[A-Za-z]:\\|\\\\"))
            throw new InvalidOperationException("Invalid case deletion request.");
        return new()
        {
            Id = Guid.NewGuid(), ApplicationId = applicationId, RecordId = recordId,
            RequestedBy = requestedBy.Trim(), Reason = reason.Trim(),
            RequestedAt = now, UpdatedAt = now
        };
    }

    public void AddTarget(KmDeletionResource resource, DateTime now)
    {
        if (resource.ApplicationId != ApplicationId || resource.RecordId != RecordId ||
            !resource.IsValid() || Targets.Any(x => x.Resource == resource))
            throw new InvalidOperationException("Deletion target is invalid or duplicated.");
        Targets.Add(VerificationCaseDeletionTarget.Create(Id, resource, now));
    }

    public void MarkWaitingForKm(DateTime now)
    {
        if (Status != "Prepared" || Targets.Count == 0)
            throw new InvalidOperationException("No valid KM deletion targets were prepared.");
        Status = "WaitingForKm";
        UpdatedAt = now;
    }

    public void MarkKmVerified(DateTime now)
    {
        if (Status != "WaitingForKm" || Targets.Count == 0 ||
            Targets.Any(x => x.Status != "Completed"))
            throw new InvalidOperationException("KM deletion is not fully verified.");
        Status = "KmVerified";
        UpdatedAt = now;
    }
}

public sealed class VerificationCaseDeletionTarget
{
    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public Guid? DocumentFileId { get; private set; }
    public Guid? DocumentId { get; private set; }
    public int? Version { get; private set; }
    public Guid? SourceEntityId { get; private set; }
    public Guid? ApplicationId { get; private set; }
    public Guid? RecordId { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public string Status { get; private set; } = "Pending";
    public string? KmDeletionJobId { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public bool? LastErrorRetryable { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? KmVerifiedAt { get; private set; }

    public KmDeletionResource Resource => new(SourceType, DocumentFileId, DocumentId,
        Version, SourceEntityId, ApplicationId, RecordId);

    private VerificationCaseDeletionTarget() { }

    internal static VerificationCaseDeletionTarget Create(Guid jobId, KmDeletionResource resource,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(), JobId = jobId, SourceType = resource.SourceType,
        DocumentFileId = resource.DocumentFileId, DocumentId = resource.DocumentId,
        Version = resource.Version, SourceEntityId = resource.SourceEntityId,
        ApplicationId = resource.ApplicationId, RecordId = resource.RecordId,
        RequestedAt = now, UpdatedAt = now
    };

    public KmDeletionRequest ToRequest() => new("1.0", "CSIT", "DocumentDeletionRequested",
        Id, new DateTimeOffset(DateTime.SpecifyKind(RequestedAt, DateTimeKind.Utc)),
        "VerificationApplicationDeleted", Resource);

    public Guid ClaimDispatch(DateTime now, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero ||
            !(Status == "Pending" ||
              (Status is "DeliveryFailed" or "Failed") && LastErrorRetryable == true ||
              Status == "Dispatching" && LeaseExpiresAt <= now))
            throw new InvalidOperationException("Deletion target is not dispatchable.");
        var lease = Guid.NewGuid();
        Status = "Dispatching";
        LeaseToken = lease;
        LeaseExpiresAt = now.Add(leaseDuration);
        UpdatedAt = now;
        return lease;
    }

    public void DeliveryAccepted(Guid lease, string kmJobId, DateTime now)
    {
        if (Status != "Dispatching" || LeaseToken != lease || string.IsNullOrWhiteSpace(kmJobId) ||
            KmDeletionJobId is not null && KmDeletionJobId != kmJobId)
            throw new InvalidOperationException("KM deletion acceptance conflicts with persisted state.");
        KmDeletionJobId = kmJobId;
        Status = "Accepted";
        AttemptCount++;
        LastErrorCode = null;
        LastErrorRetryable = null;
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public void DeliveryFailed(Guid lease, string code, bool retryable, DateTime now)
    {
        if (Status != "Dispatching" || LeaseToken != lease)
            throw new InvalidOperationException("Accepted KM work cannot return to delivery failure.");
        Status = KmDeletionJobId is null ? "DeliveryFailed" : "Failed";
        AttemptCount++;
        LastErrorCode = code;
        LastErrorRetryable = retryable;
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public void ApplyCallback(KmDeletionCallback callback, DateTime now)
    {
        if (callback.DeletionRequestId != Id || callback.Resource != Resource ||
            KmDeletionJobId != callback.KmDeletionJobId || !callback.IsValid())
            throw new InvalidOperationException("KM deletion callback correlation failed.");
        if (Status == "Completed")
            throw new InvalidOperationException("Completed KM deletion cannot move backwards.");
        if (Status is not ("Accepted" or "Processing" or "Failed" or "Dispatching"))
            throw new InvalidOperationException("KM deletion callback is out of order.");
        Status = callback.Status;
        LastErrorCode = callback.Error?.Code;
        LastErrorRetryable = callback.Error?.Retryable;
        KmVerifiedAt = callback.Status == "Completed"
            ? callback.Result!.VerifiedAt.UtcDateTime : null;
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }
}

public sealed class VerificationCaseDeletionCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid TargetId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

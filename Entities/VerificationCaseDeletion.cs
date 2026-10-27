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
    public string? CleanupManifestJson { get; private set; }
    public DateTime? DbCleanedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? LastCleanupErrorCode { get; private set; }
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
        if (Status != "Prepared" || !Targets.Any(x => x.Status == "Pending") ||
            Targets.Any(x => x.Status is not ("Pending" or "NotDispatched")))
            throw new InvalidOperationException("The deletion request has already been confirmed.");
        Status = "WaitingForKm";
        UpdatedAt = now;
    }

    public void MarkLocalReady(DateTime now)
    {
        if (Status != "Prepared" || Targets.Any(x => x.Status != "NotDispatched"))
            throw new InvalidOperationException("KM delivery cannot be excluded.");
        Status = "LocalReady";
        UpdatedAt = now;
    }

    public void MarkKmVerified(DateTime now)
    {
        if (Status != "WaitingForKm" ||
            Targets.Any(x => x.Status is not ("Completed" or "NotDispatched")) ||
            !Targets.Any(x => x.Status == "Completed"))
            throw new InvalidOperationException("KM deletion is not fully verified.");
        Status = "KmVerified";
        UpdatedAt = now;
    }

    public void MarkLocalCleanup(string manifestJson, DateTime now)
    {
        if (Status is not ("LocalReady" or "KmVerified") ||
            Targets.Any(x => x.Status is not ("NotDispatched" or "Completed")) ||
            Targets.Any(x => x.Status == "Completed" && x.KmVerifiedAt is null) ||
            string.IsNullOrWhiteSpace(manifestJson))
            throw new InvalidOperationException("Deletion is not ready for local cleanup.");
        CleanupManifestJson = manifestJson;
        Status = "LocalCleanup";
        UpdatedAt = now;
    }

    public void MarkDbCleaned(DateTime now)
    {
        if (Status != "LocalCleanup" || CleanupManifestJson is null)
            throw new InvalidOperationException("Local cleanup was not prepared.");
        DbCleanedAt ??= now;
        UpdatedAt = now;
    }

    public void MarkCleanupFailed(string code, DateTime now)
    {
        if (Status != "LocalCleanup" || string.IsNullOrWhiteSpace(code) || code.Length > 100)
            throw new InvalidOperationException("Invalid cleanup failure.");
        LastCleanupErrorCode = code;
        UpdatedAt = now;
    }

    public void MarkCompleted(DateTime now)
    {
        if (Status != "LocalCleanup" || DbCleanedAt is null)
            throw new InvalidOperationException("Local cleanup is incomplete.");
        Status = "Completed";
        CompletedAt = now;
        LastCleanupErrorCode = null;
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

    public void MarkNotDispatched(DateTime now)
    {
        if (Status != "Pending" || AttemptCount != 0 || KmDeletionJobId is not null)
            throw new InvalidOperationException("KM delivery cannot be excluded.");
        Status = "NotDispatched";
        UpdatedAt = now;
    }

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

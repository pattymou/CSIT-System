namespace SIT.DepartmentSystem.Web.Entities;

public static class EquipmentAggregateAnalysisStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Superseded = "Superseded";
}

public static class EquipmentAggregateDispatchStatuses
{
    public const string Pending = "Pending";
    public const string Dispatching = "Dispatching";
    public const string Accepted = "Accepted";
    public const string RetryWaiting = "RetryWaiting";
    public const string Deferred = "Deferred";
    public const string Failed = "Failed";
    public const string Superseded = "Superseded";
}

public sealed class EquipmentAggregateAnalysisRequest
{
    public Guid Id { get; private set; }
    public string ApparatusId { get; private set; } = string.Empty;
    public string ModuleCode { get; private set; } = string.Empty;
    public string ManifestHash { get; private set; } = string.Empty;
    public string AnalysisVersion { get; private set; } = string.Empty;
    public int AttemptNumber { get; private set; } = 1;
    public string? RequestedBy { get; private set; }
    public string SourceDocumentsJson { get; private set; } = "[]";
    public string Status { get; private set; } = EquipmentAggregateAnalysisStatuses.Pending;
    public DateTime RequestedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public string DispatchStatus { get; private set; } = EquipmentAggregateDispatchStatuses.Pending;
    public string? KmJobId { get; private set; }
    public int DispatchAttemptCount { get; private set; }
    public DateTime? DispatchNextRetryAt { get; private set; }
    public string? DispatchLastErrorCode { get; private set; }
    public string? DispatchLastErrorMessage { get; private set; }
    public DateTime? DispatchLastAttemptAt { get; private set; }
    public Guid? DispatchLeaseToken { get; private set; }
    public DateTime? DispatchLeaseExpiresAt { get; private set; }
    public DateTime? DispatchedAt { get; private set; }

    private EquipmentAggregateAnalysisRequest() { }

    public static EquipmentAggregateAnalysisRequest Create(Guid requestId, string apparatusId,
        string moduleCode, string manifestHash, string analysisVersion, string documentsJson, DateTime now,
        int attemptNumber = 1, string? requestedBy = null)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(apparatusId) ||
            string.IsNullOrWhiteSpace(moduleCode) || !IsSha256(manifestHash) ||
            string.IsNullOrWhiteSpace(analysisVersion) || string.IsNullOrWhiteSpace(documentsJson) ||
            attemptNumber < 1)
            throw new InvalidOperationException("Invalid equipment aggregate analysis request.");
        return new()
        {
            Id = requestId, ApparatusId = apparatusId.Trim(), ModuleCode = moduleCode.Trim().ToLowerInvariant(),
            ManifestHash = manifestHash.ToLowerInvariant(), AnalysisVersion = analysisVersion.Trim(),
            SourceDocumentsJson = documentsJson, AttemptNumber = attemptNumber,
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim(),
            RequestedAt = now, UpdatedAt = now
        };
    }

    public void Complete(DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending)
            throw new InvalidOperationException("Only a pending aggregate request can complete.");
        Status = EquipmentAggregateAnalysisStatuses.Completed;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string code, string? message, DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending)
            throw new InvalidOperationException("Only a pending aggregate request can fail.");
        Status = EquipmentAggregateAnalysisStatuses.Failed;
        FailureCode = Clean(code, 100) ?? "unknown";
        FailureMessage = Clean(message, 1000);
        UpdatedAt = now;
    }

    public void Supersede(DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending) return;
        Status = EquipmentAggregateAnalysisStatuses.Superseded;
        DispatchStatus = EquipmentAggregateDispatchStatuses.Superseded;
        DispatchNextRetryAt = null;
        DispatchLeaseToken = null;
        DispatchLeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public Guid ClaimDispatch(DateTime now, TimeSpan leaseDuration, int maxRetryCount)
    {
        var retryReady = DispatchStatus == EquipmentAggregateDispatchStatuses.RetryWaiting &&
            DispatchNextRetryAt <= now && DispatchAttemptCount < maxRetryCount;
        var deferredReady = DispatchStatus == EquipmentAggregateDispatchStatuses.Deferred &&
            DispatchNextRetryAt <= now;
        var expiredLease = DispatchStatus == EquipmentAggregateDispatchStatuses.Dispatching &&
            DispatchLeaseExpiresAt <= now && DispatchAttemptCount < maxRetryCount;
        if (Status != EquipmentAggregateAnalysisStatuses.Pending || leaseDuration <= TimeSpan.Zero ||
            maxRetryCount < 1 || !(DispatchStatus == EquipmentAggregateDispatchStatuses.Pending ||
            retryReady || deferredReady || expiredLease))
            throw new InvalidOperationException("Aggregate analysis request is not dispatchable.");
        var lease = Guid.NewGuid();
        DispatchStatus = EquipmentAggregateDispatchStatuses.Dispatching;
        DispatchLeaseToken = lease;
        DispatchLeaseExpiresAt = now.Add(leaseDuration);
        DispatchAttemptCount++;
        DispatchLastAttemptAt = now;
        DispatchNextRetryAt = null;
        UpdatedAt = now;
        return lease;
    }

    public void AcceptDispatch(Guid lease, string kmJobId, DateTime now)
    {
        EnsureDispatchLease(lease);
        if (string.IsNullOrWhiteSpace(kmJobId))
            throw new InvalidOperationException("KM aggregate job ID is missing.");
        DispatchStatus = EquipmentAggregateDispatchStatuses.Accepted;
        KmJobId = kmJobId.Trim();
        DispatchedAt = now;
        DispatchLastErrorCode = null;
        DispatchLastErrorMessage = null;
        ClearDispatchLease(now);
    }

    public void FailDispatch(Guid lease, string code, string message, bool retryable,
        bool featureDisabled, DateTime? nextRetryAt, int maxRetryCount, DateTime now)
    {
        EnsureDispatchLease(lease);
        DispatchLastErrorCode = Clean(code, 100) ?? "unknown";
        DispatchLastErrorMessage = Clean(message, 1000);
        if (featureDisabled)
        {
            DispatchStatus = EquipmentAggregateDispatchStatuses.Deferred;
            DispatchNextRetryAt = nextRetryAt ?? throw new InvalidOperationException(
                "A deferred aggregate request requires a retry time.");
        }
        else if (retryable && DispatchAttemptCount < maxRetryCount)
        {
            DispatchStatus = EquipmentAggregateDispatchStatuses.RetryWaiting;
            DispatchNextRetryAt = nextRetryAt ?? throw new InvalidOperationException(
                "A retryable aggregate request requires a retry time.");
        }
        else
        {
            DispatchStatus = EquipmentAggregateDispatchStatuses.Failed;
            DispatchNextRetryAt = null;
        }
        ClearDispatchLease(now);
    }

    public void ExhaustDispatch(DateTime now)
    {
        if (Status != EquipmentAggregateAnalysisStatuses.Pending ||
            DispatchStatus is not (EquipmentAggregateDispatchStatuses.Dispatching or
                EquipmentAggregateDispatchStatuses.RetryWaiting)) return;
        DispatchStatus = EquipmentAggregateDispatchStatuses.Failed;
        DispatchNextRetryAt = null;
        DispatchLastErrorCode = "RETRY_EXHAUSTED";
        DispatchLastErrorMessage = "KM aggregate delivery retry limit reached.";
        DispatchLeaseToken = null;
        DispatchLeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private void EnsureDispatchLease(Guid lease)
    {
        if (DispatchStatus != EquipmentAggregateDispatchStatuses.Dispatching ||
            lease == Guid.Empty || DispatchLeaseToken != lease)
            throw new InvalidOperationException("Aggregate analysis dispatch lease does not match.");
    }

    private void ClearDispatchLease(DateTime now)
    {
        DispatchLeaseToken = null;
        DispatchLeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Trim().Where(x => !char.IsControl(x)).Take(max).ToArray());
}

public sealed class EquipmentAggregateAnalysisSnapshot
{
    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string ApparatusId { get; private set; } = string.Empty;
    public string ModuleCode { get; private set; } = string.Empty;
    public string ManifestHash { get; private set; } = string.Empty;
    public string AnalysisVersion { get; private set; } = string.Empty;
    public string SourceDocumentsJson { get; private set; } = "[]";
    public string ResultJson { get; private set; } = "{}";
    public DateTime AnalyzedAt { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public string ReviewStatus { get; private set; } = KmAnalysisReviewStatuses.PendingVerification;
    public string? ReviewedBy { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public string? ReviewNote { get; private set; }

    private EquipmentAggregateAnalysisSnapshot() { }

    public static EquipmentAggregateAnalysisSnapshot Create(EquipmentAggregateAnalysisRequest request,
        string resultJson, DateTime analyzedAt, DateTime receivedAt) => new()
    {
        Id = Guid.NewGuid(), RequestId = request.Id, ApparatusId = request.ApparatusId,
        ModuleCode = request.ModuleCode, ManifestHash = request.ManifestHash,
        AnalysisVersion = request.AnalysisVersion, SourceDocumentsJson = request.SourceDocumentsJson,
        ResultJson = resultJson, AnalyzedAt = analyzedAt, ReceivedAt = receivedAt
    };

    public void Review(string decision, string reviewedBy, string? note, DateTime now)
    {
        if (decision is not (KmAnalysisReviewStatuses.Verified or KmAnalysisReviewStatuses.Rejected) ||
            string.IsNullOrWhiteSpace(reviewedBy))
            throw new InvalidOperationException("Invalid aggregate analysis review.");
        ReviewStatus = decision;
        ReviewedBy = reviewedBy.Trim().ToLowerInvariant();
        ReviewedAt = now;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 2000)];
    }
}

public sealed class EquipmentAggregateAnalysisCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid RequestId { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

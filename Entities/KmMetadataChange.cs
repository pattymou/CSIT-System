namespace SIT.DepartmentSystem.Web.Entities;

public static class KmMetadataChangeStatuses
{
    public const string Pending = "Pending";
    public const string Dispatching = "Dispatching";
    public const string Sent = "Sent";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed class KmMetadataChange
{
    public Guid ChangeId { get; private set; }
    public Guid AnalysisResultId { get; private set; }
    public Guid ReviewRevisionId { get; private set; }
    public string SourceSystem { get; private set; } = "CSIT";
    public string SourceType { get; private set; } = string.Empty;
    public Guid SourceEntityId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; }
    public int SourceRevision { get; private set; }
    public string ClassificationJson { get; private set; } = string.Empty;
    public string AttributesJson { get; private set; } = string.Empty;
    public string ChangedFieldsJson { get; private set; } = string.Empty;
    public string PayloadSha256 { get; private set; } = string.Empty;
    public string Status { get; private set; } = KmMetadataChangeStatuses.Pending;
    public string? KmChangeJobId { get; private set; }
    public int RetryCount { get; private set; }
    public DateTime? NextRetryAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public bool? LastErrorRetryable { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string AuthorizedBy { get; private set; } = string.Empty;
    public DateTime AuthorizedAt { get; private set; }

    public KmDocumentAnalysisResult AnalysisResult { get; private set; } = null!;
    public KmAnalysisReviewRevision ReviewRevision { get; private set; } = null!;

    private KmMetadataChange() { }

    public static KmMetadataChange Create(Guid changeId, Guid analysisResultId, Guid reviewRevisionId,
        string sourceType, Guid sourceEntityId, Guid documentId, int version, int sourceRevision,
        string classificationJson, string attributesJson, string changedFieldsJson,
        string payloadSha256, string authorizedBy, DateTime now)
    {
        if (changeId == Guid.Empty || analysisResultId == Guid.Empty || reviewRevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceType) || sourceEntityId == Guid.Empty ||
            documentId == Guid.Empty || version < 1 || sourceRevision < 2 ||
            string.IsNullOrWhiteSpace(classificationJson) || string.IsNullOrWhiteSpace(attributesJson) ||
            string.IsNullOrWhiteSpace(changedFieldsJson) || payloadSha256.Length != 64 ||
            !payloadSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(authorizedBy))
            throw new InvalidOperationException("Invalid metadata change identity or evidence.");
        return new KmMetadataChange
        {
            ChangeId = changeId, AnalysisResultId = analysisResultId,
            ReviewRevisionId = reviewRevisionId, SourceType = sourceType.Trim(),
            SourceEntityId = sourceEntityId, DocumentId = documentId, Version = version,
            SourceRevision = sourceRevision, ClassificationJson = classificationJson,
            AttributesJson = attributesJson, ChangedFieldsJson = changedFieldsJson,
            PayloadSha256 = payloadSha256.ToLowerInvariant(), OccurredAt = now,
            CreatedAt = now, UpdatedAt = now, AuthorizedBy = authorizedBy.Trim().ToLowerInvariant(),
            AuthorizedAt = now
        };
    }

    public Guid Claim(DateTime now, TimeSpan lease)
    {
        if (!(Status == KmMetadataChangeStatuses.Pending ||
              Status == KmMetadataChangeStatuses.Failed && LastErrorRetryable == true && NextRetryAt <= now ||
              Status == KmMetadataChangeStatuses.Dispatching && LeaseExpiresAt <= now))
            throw new InvalidOperationException("Metadata change is not claimable.");
        Status = KmMetadataChangeStatuses.Dispatching;
        LeaseToken = Guid.NewGuid(); LeaseExpiresAt = now.Add(lease);
        RetryCount++; NextRetryAt = null; UpdatedAt = now;
        return LeaseToken.Value;
    }

    public void DeliveryAccepted(Guid lease, string jobId, DateTime now)
    {
        VerifyLease(lease);
        if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException("KM job ID is required.");
        if (KmChangeJobId is not null && KmChangeJobId != jobId.Trim())
            throw new InvalidOperationException("KM metadata change job correlation changed during retry.");
        Status = KmMetadataChangeStatuses.Sent; KmChangeJobId = jobId.Trim(); SentAt ??= now;
        LeaseToken = null; LeaseExpiresAt = null; LastErrorCode = null; LastErrorMessage = null;
        LastErrorRetryable = null; UpdatedAt = now;
    }

    public void DeliveryFailed(Guid lease, string code, string message, bool retryable,
        DateTime? nextRetryAt, DateTime now)
    {
        VerifyLease(lease);
        Status = KmMetadataChangeStatuses.Failed; LastErrorCode = code; LastErrorMessage = message;
        LastErrorRetryable = retryable; NextRetryAt = retryable ? nextRetryAt : null;
        LeaseToken = null; LeaseExpiresAt = null; UpdatedAt = now;
    }

    public void Processing(int appliedRevision, DateTime now)
    {
        if (Status is not (KmMetadataChangeStatuses.Sent or KmMetadataChangeStatuses.Processing) ||
            appliedRevision < 1 || appliedRevision > SourceRevision)
            throw new InvalidOperationException("Invalid metadata Processing callback.");
        Status = KmMetadataChangeStatuses.Processing; UpdatedAt = now;
    }

    public void Complete(int appliedRevision, DateTime now)
    {
        if (Status is not (KmMetadataChangeStatuses.Sent or KmMetadataChangeStatuses.Processing) ||
            appliedRevision != SourceRevision)
            throw new InvalidOperationException("Completed revision does not match the source revision.");
        Status = KmMetadataChangeStatuses.Completed; CompletedAt = now; UpdatedAt = now;
        LastErrorCode = null; LastErrorMessage = null; LastErrorRetryable = null; NextRetryAt = null;
    }

    public void CallbackFailed(int appliedRevision, string code, string message, bool retryable,
        DateTime? nextRetryAt, DateTime now)
    {
        if (Status is not (KmMetadataChangeStatuses.Sent or KmMetadataChangeStatuses.Processing) ||
            appliedRevision < 0 || appliedRevision > SourceRevision)
            throw new InvalidOperationException("Invalid metadata Failed callback.");
        Status = KmMetadataChangeStatuses.Failed; LastErrorCode = code; LastErrorMessage = message;
        LastErrorRetryable = retryable; NextRetryAt = retryable ? nextRetryAt : null; UpdatedAt = now;
    }

    private void VerifyLease(Guid lease)
    {
        if (Status != KmMetadataChangeStatuses.Dispatching || LeaseToken != lease)
            throw new InvalidOperationException("Metadata change lease is no longer owned.");
    }
}

public sealed class KmMetadataChangeCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid ChangeId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AppliedRevision { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public KmMetadataChange Change { get; set; } = null!;
}

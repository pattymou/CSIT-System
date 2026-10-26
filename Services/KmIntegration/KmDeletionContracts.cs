namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// Appendix B is a separate v1 contract. Keep every resource key, including null values.
public sealed record KmDeletionResource(
    string SourceType, Guid? DocumentFileId, Guid? DocumentId, int? Version,
    Guid? SourceEntityId, Guid? ApplicationId, Guid? RecordId)
{
    public bool IsValid() => SourceType switch
    {
        "DocumentLibraryFile" => DocumentFileId is { } id && id != Guid.Empty &&
            DocumentId is null && Version is null && SourceEntityId is null &&
            ((ApplicationId is null && RecordId is null) ||
             (ApplicationId is { } app && app != Guid.Empty &&
              RecordId is { } record && record != Guid.Empty)),
        "TestReport" or "VerificationApplicationAttachment" =>
            DocumentFileId is null && DocumentId is { } document && document != Guid.Empty &&
            Version is { } version && version >= 1 &&
            (SourceType != "VerificationApplicationAttachment" || version == 1) &&
            SourceEntityId is { } source && source != Guid.Empty &&
            ApplicationId is { } application && application != Guid.Empty &&
            RecordId is { } caseRecord && caseRecord != Guid.Empty,
        _ => false
    };
}

public sealed record KmDeletionRequest(
    string SchemaVersion, string SourceSystem, string EventType, Guid DeletionRequestId,
    DateTimeOffset RequestedAt, string ReasonCode, KmDeletionResource Resource)
{
    public bool IsValid() => SchemaVersion == "1.0" && SourceSystem == "CSIT" &&
        EventType == "DocumentDeletionRequested" && DeletionRequestId != Guid.Empty &&
        RequestedAt.Offset == TimeSpan.Zero && RequestedAt != default &&
        ReasonCode is ("VerificationApplicationDeleted" or "DocumentFileDeleted" or
            "TestReportVersionDeleted" or "AdministrativeCleanup") && Resource is not null &&
        Resource.IsValid();
}

public sealed record KmDeletionAccepted(Guid DeletionRequestId, string KmDeletionJobId, string Status);
public sealed record KmDeletionTimestamps(DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt);
public sealed record KmDeletionCleanup(string Metadata, string ExtractedText, string Chunks,
    string VectorIndex, string GraphRelations, string Cache, string StoredCopies)
{
    public bool IsVerified() => new[] { Metadata, ExtractedText, Chunks, VectorIndex,
        GraphRelations, Cache, StoredCopies }.All(x => x is "Deleted" or "NotPresent");
}
public sealed record KmDeletionResult(bool CleanupVerified, DateTimeOffset VerifiedAt,
    KmDeletionCleanup Cleanup);
public sealed record KmDeletionError(string Code, string Message, bool Retryable);
public sealed record KmDeletionCallback(
    string SchemaVersion, Guid CallbackId, string SourceSystem, Guid DeletionRequestId,
    string KmDeletionJobId, KmDeletionResource Resource, string Status,
    KmDeletionTimestamps Timestamps, KmDeletionResult? Result, KmDeletionError? Error)
{
    public bool IsValid() => SchemaVersion == "1.0" && CallbackId != Guid.Empty &&
        SourceSystem == "CSIT" && DeletionRequestId != Guid.Empty &&
        KmDeletionJobId is { Length: > 0 and <= 150 } &&
        Resource is not null && Resource.IsValid() &&
        Timestamps is not null && (Status switch
        {
            "Processing" => ValidUtc(Timestamps.StartedAt) && Result is null && Error is null,
            "Completed" => ValidUtc(Timestamps.CompletedAt) && Result is
                { CleanupVerified: true, Cleanup: not null } result &&
                ValidUtc(result.VerifiedAt) &&
                result.VerifiedAt <= Timestamps.CompletedAt &&
                result.Cleanup.IsVerified() && Error is null,
            "Failed" => ValidUtc(Timestamps.FailedAt) && Result is null && Error is
                { Code: { Length: > 0 and <= 100 }, Message: { Length: > 0 and <= 500 } },
            _ => false
        });

    private static bool ValidUtc(DateTimeOffset? value) =>
        value is { } timestamp && timestamp != default && timestamp.Offset == TimeSpan.Zero;
}

public sealed record KmDeletionDeliveryResult(bool Accepted, bool Retryable, string? JobId,
    string? ErrorCode)
{
    public static KmDeletionDeliveryResult Success(string jobId) => new(true, false, jobId, null);
    public static KmDeletionDeliveryResult Failure(bool retryable, string code) =>
        new(false, retryable, null, code);
}

namespace SIT.DepartmentSystem.Web.Entities;

public enum KmIntegrationStatus
{
    Pending,
    Dispatching,
    Sent,
    Processing,
    Completed,
    Failed
}

public sealed class KmIntegrationOutbox
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string SourceSystem { get; private set; } = string.Empty;
    public string SourceEntityType { get; private set; } = string.Empty;
    public Guid SourceEntityId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? CaseId { get; private set; }
    public Guid? TaskId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public DateTime CommittedAt { get; private set; }
    public KmIntegrationStatus Status { get; private set; }
    public string? KmJobId { get; private set; }
    public int RetryCount { get; private set; }
    public DateTime? NextRetryAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private KmIntegrationOutbox() { }

    public static KmIntegrationOutbox ForCommittedTestReport(ModuleCaseFile file, DateTime now)
    {
        if (file.FileKind != ModuleCaseFileKinds.TestReport ||
            file.ReviewStatus != TestReportReviewStatuses.Approved || !file.IsRawDataExported ||
            file.DocumentId == Guid.Empty || file.VersionNo < 1 || file.FileSize <= 0 ||
            string.IsNullOrWhiteSpace(file.FileName))
            throw new InvalidOperationException("Only a committed formal test report can enter the KM outbox.");
        return new KmIntegrationOutbox
        {
            Id = Guid.NewGuid(), EventId = Guid.NewGuid(), EventType = "TestReportCommitted",
            SourceSystem = "CSIT", SourceEntityType = "TestReport", SourceEntityId = file.Id,
            DocumentId = file.DocumentId, Version = file.VersionNo, ProjectId = file.RecordId,
            CaseId = file.CaseId, TaskId = file.TaskId, FileName = file.FileName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.FileSize, CommittedAt = now, Status = KmIntegrationStatus.Pending,
            CreatedAt = now, UpdatedAt = now
        };
    }

    public static KmIntegrationOutbox ForArchivedVerificationAttachment(
        VerificationApplication application,
        VerificationApplicationFile attachment,
        DateTime now)
    {
        var document = attachment.DocumentLibraryFile;
        if (application.Status != VerificationApplicationStatus.Accepted ||
            attachment.VerificationApplicationId != application.Id ||
            document is null || attachment.DocumentLibraryFileId != document.Id ||
            document.StorageArea != DocumentStorageAreas.Library ||
            document.FileSize <= 0 || string.IsNullOrWhiteSpace(document.StandardFileName))
            throw new InvalidOperationException(
                "Only a formally archived verification application attachment can enter the KM outbox.");

        return new KmIntegrationOutbox
        {
            Id = Guid.NewGuid(), EventId = Guid.NewGuid(),
            EventType = KmIntegrationEventTypes.VerificationApplicationAttachmentArchived,
            SourceSystem = "CSIT",
            SourceEntityType = KmIntegrationSourceEntityTypes.VerificationApplicationFile,
            SourceEntityId = attachment.Id, DocumentId = document.Id, Version = 1,
            ProjectId = null, CaseId = null, TaskId = null,
            FileName = document.OriginalFileName,
            ContentType = string.IsNullOrWhiteSpace(document.ContentType)
                ? "application/octet-stream" : document.ContentType,
            SizeBytes = document.FileSize, CommittedAt = now,
            Status = KmIntegrationStatus.Pending, CreatedAt = now, UpdatedAt = now
        };
    }

    public void Claim(Guid lease, DateTime now, TimeSpan duration)
    {
        if (lease == Guid.Empty || duration <= TimeSpan.Zero ||
            !(Status == KmIntegrationStatus.Pending ||
              (Status == KmIntegrationStatus.Failed && NextRetryAt <= now) ||
              (Status == KmIntegrationStatus.Dispatching && LeaseExpiresAt <= now)))
            throw new InvalidOperationException("KM outbox row is not claimable.");
        Status = KmIntegrationStatus.Dispatching;
        LeaseToken = lease;
        LeaseExpiresAt = now.Add(duration);
        if (LastAttemptAt is not null) RetryCount++;
        LastAttemptAt = now;
        NextRetryAt = null;
        UpdatedAt = now;
    }

    public void Sent(Guid lease, string jobId, DateTime now)
    {
        EnsureLease(lease);
        if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException("KM job ID is missing.");
        KmJobId = jobId.Trim();
        Status = KmIntegrationStatus.Sent;
        SentAt = now;
        LastErrorCode = null;
        LastErrorMessage = null;
        ClearLease(now);
    }

    public void DeliveryFailed(Guid lease, string code, string message, DateTime? nextRetryAt, DateTime now)
    {
        EnsureLease(lease);
        Status = KmIntegrationStatus.Failed;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        NextRetryAt = nextRetryAt;
        ClearLease(now);
    }

    public bool CanManualRetry(DateTime now) =>
        Status == KmIntegrationStatus.Failed && KmJobId is null &&
        (LeaseToken is null || (LeaseExpiresAt.HasValue && LeaseExpiresAt <= now)) &&
        SourceSystem == "CSIT" && EventType == "TestReportCommitted" &&
        SourceEntityType == "TestReport";

    public void RequeueManually(DateTime now)
    {
        if (!CanManualRetry(now))
            throw new InvalidOperationException("KM outbox is not eligible for manual retry.");
        Status = KmIntegrationStatus.Pending;
        NextRetryAt = null;
        LeaseToken = null;
        LeaseExpiresAt = null;
        // Keep LastError and RetryCount as delivery history. The audit preserves their pre-retry values.
        UpdatedAt = now;
    }

    public void CallbackProcessing(DateTime now)
    {
        if (Status is not (KmIntegrationStatus.Sent or KmIntegrationStatus.Processing))
            throw new InvalidOperationException("KM callback is out of order.");
        Status = KmIntegrationStatus.Processing;
        UpdatedAt = now;
    }

    public void CallbackCompleted(DateTime now)
    {
        if (Status is not (KmIntegrationStatus.Sent or KmIntegrationStatus.Processing or KmIntegrationStatus.Completed))
            throw new InvalidOperationException("KM callback is out of order.");
        Status = KmIntegrationStatus.Completed;
        CompletedAt ??= now;
        UpdatedAt = now;
    }

    public void CallbackFailed(string code, string message, DateTime now)
    {
        if (Status is not (KmIntegrationStatus.Sent or KmIntegrationStatus.Processing or KmIntegrationStatus.Failed))
            throw new InvalidOperationException("KM callback is out of order.");
        Status = KmIntegrationStatus.Failed;
        NextRetryAt = null;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        UpdatedAt = now;
    }

    private void EnsureLease(Guid lease)
    {
        if (Status != KmIntegrationStatus.Dispatching || lease == Guid.Empty || LeaseToken != lease)
            throw new InvalidOperationException("KM outbox lease does not match.");
    }

    private void ClearLease(DateTime now)
    {
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private static string Safe(string value, int maxLength) =>
        new string((value ?? string.Empty).Where(c => !char.IsControl(c)).Take(maxLength).ToArray());
}

public static class KmIntegrationEventTypes
{
    public const string TestReportCommitted = "TestReportCommitted";
    public const string VerificationApplicationAttachmentArchived =
        "VerificationApplicationAttachmentArchived";
}

public static class KmIntegrationSourceEntityTypes
{
    public const string TestReport = "TestReport";
    public const string VerificationApplicationFile = "VerificationApplicationFile";
}

namespace SIT.DepartmentSystem.Web.Entities;

public sealed class DocumentProcessingJob
{
    public Guid Id { get; set; }
    public Guid DocumentFileId { get; set; }
    public string Status { get; set; } = DocumentProcessingStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    public DateTime? KmStartedAt { get; set; }
    public DateTime? KmCompletedAt { get; set; }
    public DateTime? KmFailedAt { get; set; }
    public string? KmErrorCode { get; set; }
    public string? KmErrorMessage { get; set; }
    public bool? KmErrorRetryable { get; set; }
    public string? MetadataJson { get; set; }

    public string ArchiveStatus { get; set; } = DocumentArchiveStatuses.NotStarted;
    public int ArchiveAttemptCount { get; set; }
    public DateTime? ArchiveNextRetryAt { get; set; }
    public Guid? ArchiveLeaseToken { get; set; }
    public DateTime? ArchiveLeaseExpiresAt { get; set; }
    public DateTime? ArchiveLastAttemptAt { get; set; }
    public string? ArchiveLastError { get; set; }
    public string? ArchiveSha256 { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public DocumentLibraryFile DocumentFile { get; set; } = null!;
    public List<DocumentProcessingCallbackReceipt> CallbackReceipts { get; set; } = [];

    public void DeliveryAccepted(DateTime now)
    {
        if (Status is not (DocumentProcessingStatuses.Pending or DocumentProcessingStatuses.Accepted))
            throw new InvalidOperationException("Document processing job cannot accept this delivery transition.");
        Status = DocumentProcessingStatuses.Accepted;
        LastError = null;
        UpdatedAt = now;
    }

    public void DeliveryAttempted(int attemptCount, DateTime attemptedAt, string? error)
    {
        AttemptCount = Math.Max(AttemptCount, attemptCount);
        LastAttemptAt = attemptedAt;
        LastError = Safe(error, 2000);
        UpdatedAt = attemptedAt;
    }

    public void DeliveryExhausted(DateTime now, string code, string error)
    {
        if (Status is not (DocumentProcessingStatuses.Pending or DocumentProcessingStatuses.Accepted))
            throw new InvalidOperationException("Document processing job cannot fail delivery in its current state.");
        Status = DocumentProcessingStatuses.Failed;
        KmFailedAt ??= now;
        KmErrorCode = Safe(code, 100);
        KmErrorMessage = Safe(error, 2000);
        KmErrorRetryable = false;
        LastError = $"{KmErrorCode}: {KmErrorMessage}";
        UpdatedAt = now;
    }

    public void CallbackProcessing(DateTime startedAt, DateTime now)
    {
        startedAt = PostgreSqlTimestamp(startedAt);
        if (Status is not (DocumentProcessingStatuses.Accepted or DocumentProcessingStatuses.Processing))
            throw new InvalidOperationException("Document processing callback is out of order.");
        Status = DocumentProcessingStatuses.Processing;
        if (KmStartedAt.HasValue && KmStartedAt.Value != startedAt)
            throw new InvalidOperationException("Document processing callback conflicts with the recorded start time.");
        KmStartedAt ??= startedAt;
        UpdatedAt = now;
    }

    public void CallbackCompleted(DateTime? startedAt, DateTime completedAt, string metadataJson, DateTime now)
    {
        startedAt = startedAt.HasValue ? PostgreSqlTimestamp(startedAt.Value) : null;
        completedAt = PostgreSqlTimestamp(completedAt);
        if (Status is not (DocumentProcessingStatuses.Accepted or DocumentProcessingStatuses.Processing or DocumentProcessingStatuses.Completed))
            throw new InvalidOperationException("Document processing callback is out of order.");
        if (string.IsNullOrWhiteSpace(metadataJson))
            throw new InvalidOperationException("Completed document metadata is missing.");
        if (KmCompletedAt.HasValue && KmCompletedAt.Value != completedAt)
            throw new InvalidOperationException("Document processing callback conflicts with the recorded completion time.");
        if (KmStartedAt.HasValue && startedAt.HasValue && KmStartedAt.Value != startedAt.Value)
            throw new InvalidOperationException("Document processing callback conflicts with the recorded start time.");
        if (MetadataJson is not null && !string.Equals(MetadataJson, metadataJson, StringComparison.Ordinal))
            throw new InvalidOperationException("Document processing callback conflicts with the recorded metadata.");
        Status = DocumentProcessingStatuses.Completed;
        KmStartedAt ??= startedAt;
        KmCompletedAt ??= completedAt;
        MetadataJson ??= metadataJson;
        KmErrorCode = null;
        KmErrorMessage = null;
        KmErrorRetryable = null;
        if (ArchiveStatus == DocumentArchiveStatuses.NotStarted)
            ArchiveStatus = DocumentArchiveStatuses.Pending;
        UpdatedAt = now;
    }

    public void CallbackFailed(DateTime? startedAt, DateTime failedAt, string code, string message,
        bool retryable, DateTime now)
    {
        startedAt = startedAt.HasValue ? PostgreSqlTimestamp(startedAt.Value) : null;
        failedAt = PostgreSqlTimestamp(failedAt);
        if (Status is not (DocumentProcessingStatuses.Accepted or DocumentProcessingStatuses.Processing or DocumentProcessingStatuses.Failed))
            throw new InvalidOperationException("Document processing callback is out of order.");
        if (KmFailedAt.HasValue && KmFailedAt.Value != failedAt)
            throw new InvalidOperationException("Document processing callback conflicts with the recorded failure time.");
        if (KmStartedAt.HasValue && startedAt.HasValue && KmStartedAt.Value != startedAt.Value)
            throw new InvalidOperationException("Document processing callback conflicts with the recorded start time.");
        if (Status == DocumentProcessingStatuses.Failed &&
            (!string.Equals(KmErrorCode, Safe(code, 100), StringComparison.Ordinal) ||
             !string.Equals(KmErrorMessage, Safe(message, 2000), StringComparison.Ordinal) ||
             KmErrorRetryable != retryable))
            throw new InvalidOperationException("Document processing callback conflicts with the recorded failure.");
        Status = DocumentProcessingStatuses.Failed;
        KmStartedAt ??= startedAt;
        KmFailedAt ??= failedAt;
        KmErrorCode = Safe(code, 100);
        KmErrorMessage = Safe(message, 2000);
        KmErrorRetryable = retryable;
        LastError = $"{KmErrorCode}: {KmErrorMessage}";
        UpdatedAt = now;
    }

    public void ClaimArchive(Guid lease, DateTime now, TimeSpan duration)
    {
        if (lease == Guid.Empty || duration <= TimeSpan.Zero || Status != DocumentProcessingStatuses.Completed ||
            !(ArchiveStatus == DocumentArchiveStatuses.Pending ||
              (ArchiveStatus == DocumentArchiveStatuses.Failed && ArchiveNextRetryAt <= now) ||
              (ArchiveStatus == DocumentArchiveStatuses.Archiving && ArchiveLeaseExpiresAt <= now)))
            throw new InvalidOperationException("Document archive job is not claimable.");
        ArchiveStatus = DocumentArchiveStatuses.Archiving;
        ArchiveLeaseToken = lease;
        ArchiveLeaseExpiresAt = now.Add(duration);
        ArchiveAttemptCount++;
        ArchiveLastAttemptAt = now;
        ArchiveNextRetryAt = null;
        UpdatedAt = now;
    }

    public void ArchiveCompleted(Guid lease, string sha256, DateTime now)
    {
        EnsureArchiveLease(lease);
        ArchiveStatus = DocumentArchiveStatuses.Completed;
        ArchiveSha256 = sha256;
        ArchivedAt ??= now;
        ArchiveLastError = null;
        ClearArchiveLease(now);
    }

    public void ArchiveFailed(Guid lease, string error, DateTime? nextRetryAt, DateTime now)
    {
        EnsureArchiveLease(lease);
        ArchiveStatus = DocumentArchiveStatuses.Failed;
        ArchiveLastError = Safe(error, 2000);
        ArchiveNextRetryAt = nextRetryAt;
        ClearArchiveLease(now);
    }

    private void EnsureArchiveLease(Guid lease)
    {
        if (ArchiveStatus != DocumentArchiveStatuses.Archiving || lease == Guid.Empty || ArchiveLeaseToken != lease)
            throw new InvalidOperationException("Document archive lease does not match.");
    }

    private void ClearArchiveLease(DateTime now)
    {
        ArchiveLeaseToken = null;
        ArchiveLeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private static string? Safe(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return new string(value.Where(c => !char.IsControl(c)).Take(maxLength).ToArray());
    }

    // PostgreSQL timestamp with time zone stores microseconds; normalize before comparing
    // callbacks so a round trip cannot turn the same RFC 3339 instant into a conflict.
    private static DateTime PostgreSqlTimestamp(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);
}

public static class DocumentProcessingStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class DocumentArchiveStatuses
{
    public const string NotStarted = "NotStarted";
    public const string Pending = "Pending";
    public const string Archiving = "Archiving";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed class DocumentProcessingCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid DocumentProcessingJobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }

    public DocumentProcessingJob DocumentProcessingJob { get; set; } = null!;
}

using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Entities;

public static class DocumentLibraryFileDeletionStatuses
{
    public const string Preparing = "Preparing";
    public const string Pending = "Pending";
    public const string Dispatching = "Dispatching";
    public const string Accepted = "Accepted";
    public const string Processing = "Processing";
    public const string Failed = "Failed";
    public const string KmVerified = "KmVerified";
    public const string LocalCleanup = "LocalCleanup";
    public const string CleanupFailed = "CleanupFailed";
    public const string Completed = "Completed";

    public static bool IsActive(string status) => status != Completed;
}

public sealed class DocumentLibraryFileDeletionJob
{
    public Guid Id { get; private set; }
    public Guid DocumentFileId { get; private set; }
    public Guid FolderId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string RelativePath { get; private set; } = string.Empty;
    public string StorageArea { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public string Status { get; private set; } = DocumentLibraryFileDeletionStatuses.Preparing;
    public string? KmDeletionJobId { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public bool? LastErrorRetryable { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public DateTime? KmVerifiedAt { get; private set; }
    public DateTime? DbRecordsDeletedAt { get; private set; }
    public DateTime? NasFileRemovedAt { get; private set; }
    public DateTime? StagingFileRemovedAt { get; private set; }
    public DateTime? ApprovalStagingRemovedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public KmDeletionResource Resource => new(
        "DocumentLibraryFile", DocumentFileId, null, null, null, null, null);

    private DocumentLibraryFileDeletionJob() { }

    public static DocumentLibraryFileDeletionJob Create(
        DocumentLibraryFile file,
        string requestedBy,
        DateTime now)
    {
        if (file.Id == Guid.Empty || file.FolderId == Guid.Empty || file.FileSize <= 0 ||
            file.Sha256.Length != 64 || string.IsNullOrWhiteSpace(file.RelativePath) ||
            string.IsNullOrWhiteSpace(file.OriginalFileName) ||
            string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Trim().Length > 100)
            throw new InvalidOperationException("Invalid document library file deletion request.");

        return new DocumentLibraryFileDeletionJob
        {
            Id = Guid.NewGuid(),
            DocumentFileId = file.Id,
            FolderId = file.FolderId,
            FileName = file.OriginalFileName,
            RelativePath = file.RelativePath,
            StorageArea = file.StorageArea,
            FileSize = file.FileSize,
            Sha256 = file.Sha256,
            RequestedBy = requestedBy.Trim(),
            RequestedAt = now,
            UpdatedAt = now
        };
    }

    public KmDeletionRequest ToRequest() => new(
        "1.0", "CSIT", "DocumentDeletionRequested", Id,
        new DateTimeOffset(DateTime.SpecifyKind(RequestedAt, DateTimeKind.Utc)),
        "DocumentFileDeleted", Resource);

    public void WaitForPreparation(string code, string message, DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.Preparing)
            throw new InvalidOperationException("Document deletion is no longer preparing.");
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = true;
        UpdatedAt = now;
    }

    public void RequireKmDeletion(bool dispatchEnabled, DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.Preparing)
            throw new InvalidOperationException("Document deletion preparation is out of order.");
        Status = DocumentLibraryFileDeletionStatuses.Pending;
        LastErrorCode = dispatchEnabled ? null : "KM_DELETION_DISABLED";
        LastErrorMessage = dispatchEnabled ? null :
            "KM deletion dispatch is disabled; the document remains pending and traceable.";
        LastErrorRetryable = dispatchEnabled ? null : true;
        UpdatedAt = now;
    }

    public void AllowLocalCleanup(DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.Preparing)
            throw new InvalidOperationException("Document deletion preparation is out of order.");
        Status = DocumentLibraryFileDeletionStatuses.LocalCleanup;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        UpdatedAt = now;
    }

    public Guid ClaimDispatch(DateTime now, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero ||
            !(Status == DocumentLibraryFileDeletionStatuses.Pending ||
              Status == DocumentLibraryFileDeletionStatuses.Failed && LastErrorRetryable == true ||
              Status == DocumentLibraryFileDeletionStatuses.Dispatching && LeaseExpiresAt <= now))
            throw new InvalidOperationException("Document deletion is not dispatchable.");
        var lease = Guid.NewGuid();
        Status = DocumentLibraryFileDeletionStatuses.Dispatching;
        LeaseToken = lease;
        LeaseExpiresAt = now.Add(leaseDuration);
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        UpdatedAt = now;
        return lease;
    }

    public void DeliveryAccepted(Guid lease, string kmJobId, DateTime now)
    {
        EnsureLease(lease);
        if (string.IsNullOrWhiteSpace(kmJobId) ||
            KmDeletionJobId is not null && KmDeletionJobId != kmJobId)
            throw new InvalidOperationException("KM document deletion acceptance conflicts with persisted state.");
        KmDeletionJobId = kmJobId.Trim();
        Status = DocumentLibraryFileDeletionStatuses.Accepted;
        AttemptCount++;
        ClearLease(now);
    }

    public void DeliveryFailed(Guid lease, string code, bool retryable, DateTime now)
    {
        EnsureLease(lease);
        Status = DocumentLibraryFileDeletionStatuses.Failed;
        AttemptCount++;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = "KM deletion request was not accepted.";
        LastErrorRetryable = retryable;
        ClearLease(now);
    }

    public void ApplyCallback(KmDeletionCallback callback, DateTime now)
    {
        if (callback.DeletionRequestId != Id || callback.Resource != Resource ||
            KmDeletionJobId != callback.KmDeletionJobId || !callback.IsValid())
            throw new InvalidOperationException("Document deletion callback correlation failed.");
        if (Status == DocumentLibraryFileDeletionStatuses.Completed)
            throw new InvalidOperationException("Completed document deletion cannot move backwards.");
        if (Status is not (DocumentLibraryFileDeletionStatuses.Accepted or
            DocumentLibraryFileDeletionStatuses.Processing or DocumentLibraryFileDeletionStatuses.Failed))
            throw new InvalidOperationException("Document deletion callback is out of order.");

        Status = callback.Status == "Completed"
            ? DocumentLibraryFileDeletionStatuses.KmVerified
            : callback.Status;
        LastErrorCode = callback.Error?.Code;
        LastErrorMessage = Safe(callback.Error?.Message, 500);
        LastErrorRetryable = callback.Error?.Retryable;
        KmVerifiedAt = callback.Status == "Completed" ? callback.Result!.VerifiedAt.UtcDateTime : null;
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public void BeginLocalCleanup(DateTime now)
    {
        if (Status is not (DocumentLibraryFileDeletionStatuses.LocalCleanup or
            DocumentLibraryFileDeletionStatuses.KmVerified or
            DocumentLibraryFileDeletionStatuses.CleanupFailed))
            throw new InvalidOperationException("Document file is not ready for local cleanup.");
        Status = DocumentLibraryFileDeletionStatuses.LocalCleanup;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        UpdatedAt = now;
    }

    public void MarkDatabaseCleanupCompleted(DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Document local cleanup was not started.");
        DbRecordsDeletedAt ??= now;
        UpdatedAt = now;
    }

    public void MarkCleanupCompleted(DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.LocalCleanup ||
            DbRecordsDeletedAt is null)
            throw new InvalidOperationException("Document database cleanup is incomplete.");
        NasFileRemovedAt ??= now;
        StagingFileRemovedAt ??= now;
        ApprovalStagingRemovedAt ??= now;
        Status = DocumentLibraryFileDeletionStatuses.Completed;
        CompletedAt ??= now;
        UpdatedAt = now;
    }

    public void MarkCleanupFailed(string code, string message, DateTime now)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Document local cleanup was not started.");
        Status = DocumentLibraryFileDeletionStatuses.CleanupFailed;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = true;
        UpdatedAt = now;
    }

    private void EnsureLease(Guid lease)
    {
        if (Status != DocumentLibraryFileDeletionStatuses.Dispatching || lease == Guid.Empty ||
            LeaseToken != lease)
            throw new InvalidOperationException("Document deletion lease does not match.");
    }

    private void ClearLease(DateTime now)
    {
        LeaseToken = null;
        LeaseExpiresAt = null;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        UpdatedAt = now;
    }

    private static string? Safe(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(maxLength).ToArray());
}

public sealed class DocumentLibraryFileDeletionCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid DeletionJobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

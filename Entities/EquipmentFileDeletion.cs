using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Entities;

public static class EquipmentFileDeletionStatuses
{
    public const string Pending = "Pending";
    public const string Dispatching = "Dispatching";
    public const string Accepted = "Accepted";
    public const string Processing = "Processing";
    public const string Failed = "Failed";
    public const string KmVerified = "KmVerified";
    public const string LocalCleanup = "LocalCleanup";
    public const string CleanupFailed = "CleanupFailed";
    public const string Completed = "Completed";
}

public sealed class EquipmentFileDeletionJob
{
    public Guid Id { get; private set; }
    public Guid ApparatusFileId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; } = 1;
    public string EquipmentId { get; private set; } = string.Empty;
    public string SourceEntityType { get; private set; } = "ApparatusFile";
    public string FileName { get; private set; } = string.Empty;
    public string? Sha256 { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public string Status { get; private set; } = EquipmentFileDeletionStatuses.Pending;
    public string? KmDeletionJobId { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public bool? LastErrorRetryable { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public DateTime? KmVerifiedAt { get; private set; }
    public DateTime? DbRecordDeletedAt { get; private set; }
    public DateTime? PhysicalFileDeletedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public EquipmentDocumentDeletionResource Resource => new(
        SourceEntityType, EquipmentId, ApparatusFileId, DocumentId, Version);

    private EquipmentFileDeletionJob() { }

    public static EquipmentFileDeletionJob Create(ApparatusFile file, string requestedBy,
        bool requiresKmDeletion, DateTime now)
    {
        if (file.Id == Guid.Empty || string.IsNullOrWhiteSpace(file.ApparatusId) ||
            file.ApparatusId.Length > 30 || string.IsNullOrWhiteSpace(file.FileName) ||
            string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Trim().Length > 100)
            throw new InvalidOperationException("Invalid Equipment file deletion request.");
        return new()
        {
            Id = Guid.NewGuid(), ApparatusFileId = file.Id, DocumentId = file.Id,
            EquipmentId = file.ApparatusId, FileName = file.OriginalFileName ?? file.FileName,
            Sha256 = file.Sha256, RequestedBy = requestedBy.Trim(), RequestedAt = now,
            Status = requiresKmDeletion ? EquipmentFileDeletionStatuses.Pending
                : EquipmentFileDeletionStatuses.LocalCleanup,
            UpdatedAt = now
        };
    }

    public EquipmentDocumentDeletionRequest ToRequest() => new(
        "CSIT.EquipmentDocumentDeletion", "draft-1", "CSIT", Id,
        new DateTimeOffset(DateTime.SpecifyKind(RequestedAt, DateTimeKind.Utc)),
        "EquipmentAttachmentDeleted", Resource);

    public Guid ClaimDispatch(DateTime now, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero ||
            !(Status == EquipmentFileDeletionStatuses.Pending ||
              Status == EquipmentFileDeletionStatuses.Failed && LastErrorRetryable == true ||
              Status == EquipmentFileDeletionStatuses.Dispatching && LeaseExpiresAt <= now))
            throw new InvalidOperationException("Equipment deletion is not dispatchable.");
        var lease = Guid.NewGuid();
        Status = EquipmentFileDeletionStatuses.Dispatching;
        LeaseToken = lease;
        LeaseExpiresAt = now.Add(leaseDuration);
        UpdatedAt = now;
        return lease;
    }

    public void DeliveryAccepted(Guid lease, string kmJobId, DateTime now)
    {
        EnsureLease(lease);
        if (string.IsNullOrWhiteSpace(kmJobId) ||
            KmDeletionJobId is not null && KmDeletionJobId != kmJobId)
            throw new InvalidOperationException("KM Equipment deletion acceptance conflicts with persisted state.");
        KmDeletionJobId = kmJobId.Trim();
        Status = EquipmentFileDeletionStatuses.Accepted;
        AttemptCount++;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        ClearLease(now);
    }

    public void DeliveryFailed(Guid lease, string code, string? message, bool retryable, DateTime now)
    {
        EnsureLease(lease);
        Status = EquipmentFileDeletionStatuses.Failed;
        AttemptCount++;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = retryable;
        ClearLease(now);
    }

    public void ApplyCallback(EquipmentDocumentDeletionCallback callback, DateTime now)
    {
        if (callback.DeletionRequestId != Id || callback.Resource != Resource ||
            KmDeletionJobId != callback.KmDeletionJobId || !callback.IsValid())
            throw new InvalidOperationException("Equipment deletion callback correlation failed.");
        if (Status == EquipmentFileDeletionStatuses.Completed)
            throw new InvalidOperationException("Completed Equipment deletion cannot move backwards.");
        if (Status is not (EquipmentFileDeletionStatuses.Accepted or
            EquipmentFileDeletionStatuses.Processing or EquipmentFileDeletionStatuses.Failed))
            throw new InvalidOperationException("Equipment deletion callback is out of order.");
        Status = callback.Status == "Completed"
            ? EquipmentFileDeletionStatuses.KmVerified : callback.Status;
        LastErrorCode = callback.Error?.Code;
        LastErrorMessage = callback.Error?.Message;
        LastErrorRetryable = callback.Error?.Retryable;
        KmVerifiedAt = callback.Status == "Completed" ? callback.Result!.VerifiedAt.UtcDateTime : null;
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public void BeginLocalCleanup(DateTime now)
    {
        if (Status is not (EquipmentFileDeletionStatuses.LocalCleanup or
            EquipmentFileDeletionStatuses.KmVerified or EquipmentFileDeletionStatuses.CleanupFailed))
            throw new InvalidOperationException("Equipment file is not ready for local cleanup.");
        Status = EquipmentFileDeletionStatuses.LocalCleanup;
        LastErrorCode = null;
        LastErrorMessage = null;
        LastErrorRetryable = null;
        UpdatedAt = now;
    }

    public void MarkLocalCleanupCompleted(bool physicalFileDeleted, DateTime now)
    {
        if (Status != EquipmentFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Equipment local cleanup was not started.");
        DbRecordDeletedAt ??= now;
        if (physicalFileDeleted) PhysicalFileDeletedAt ??= now;
        Status = EquipmentFileDeletionStatuses.Completed;
        CompletedAt ??= now;
        UpdatedAt = now;
    }

    public void MarkCleanupFailed(string code, string? message, DateTime now)
    {
        if (Status != EquipmentFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Equipment local cleanup was not started.");
        Status = EquipmentFileDeletionStatuses.CleanupFailed;
        LastErrorCode = Safe(code, 100);
        LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = true;
        UpdatedAt = now;
    }

    private void EnsureLease(Guid lease)
    {
        if (Status != EquipmentFileDeletionStatuses.Dispatching || lease == Guid.Empty ||
            LeaseToken != lease)
            throw new InvalidOperationException("Equipment deletion lease does not match.");
    }

    private void ClearLease(DateTime now)
    {
        LeaseToken = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private static string? Safe(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(maxLength).ToArray());
}

public sealed class EquipmentFileDeletionCallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid DeletionJobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

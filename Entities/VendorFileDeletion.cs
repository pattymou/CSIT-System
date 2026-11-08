using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Entities;

public static class VendorFileDeletionStatuses
{
    public const string Pending = EquipmentFileDeletionStatuses.Pending;
    public const string Dispatching = EquipmentFileDeletionStatuses.Dispatching;
    public const string Accepted = EquipmentFileDeletionStatuses.Accepted;
    public const string Processing = EquipmentFileDeletionStatuses.Processing;
    public const string Failed = EquipmentFileDeletionStatuses.Failed;
    public const string KmVerified = EquipmentFileDeletionStatuses.KmVerified;
    public const string LocalCleanup = EquipmentFileDeletionStatuses.LocalCleanup;
    public const string CleanupFailed = EquipmentFileDeletionStatuses.CleanupFailed;
    public const string Completed = EquipmentFileDeletionStatuses.Completed;
}

public sealed class VendorFileDeletionJob
{
    public Guid Id { get; private set; }
    public Guid VendorFileId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; } = 1;
    public Guid VendorId { get; private set; }
    public string SourceType { get; private set; } = KmDocumentDeletionV2SourceTypes.VendorFile;
    public string FileName { get; private set; } = string.Empty;
    public string? Sha256 { get; private set; }
    public string ReasonCode { get; private set; } = KmDocumentDeletionReasonCodes.SourceDeleted;
    public Guid? CanonicalIngestionEventId { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public string Status { get; private set; } = VendorFileDeletionStatuses.Pending;
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

    public KmDocumentDeletionV2Resource Resource => new(
        SourceType, VendorFileId, DocumentId, Version, Sha256 ?? string.Empty,
        null, null, VendorId, null, CanonicalIngestionEventId ?? Guid.Empty,
        "DocumentVersion");

    private VendorFileDeletionJob() { }

    public static VendorFileDeletionJob Create(VendorFile file, string requestedBy,
        bool requiresKmDeletion, DateTime now, Guid? canonicalIngestionEventId = null,
        string reasonCode = KmDocumentDeletionReasonCodes.SourceDeleted)
    {
        if (file.Id == Guid.Empty || file.VendorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(file.OriginalFileName) ||
            string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Trim().Length > 100 ||
            !KmDocumentDeletionReasonCodes.IsValid(reasonCode) ||
            requiresKmDeletion && (canonicalIngestionEventId is null ||
                canonicalIngestionEventId == Guid.Empty || file.Sha256 is not { Length: 64 } ||
                !file.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("Invalid Vendor file deletion request.");
        return new()
        {
            Id = Guid.NewGuid(), VendorFileId = file.Id, DocumentId = file.Id,
            VendorId = file.VendorId, FileName = file.OriginalFileName, Sha256 = file.Sha256,
            ReasonCode = reasonCode, CanonicalIngestionEventId = canonicalIngestionEventId,
            RequestedBy = requestedBy.Trim(), RequestedAt = now,
            Status = requiresKmDeletion ? VendorFileDeletionStatuses.Pending :
                VendorFileDeletionStatuses.LocalCleanup, UpdatedAt = now
        };
    }

    public KmDocumentDeletionV2Request ToRequest() => new(
        "2.0", "CSIT", "DocumentDeletionRequested", Id,
        new DateTimeOffset(DateTime.SpecifyKind(RequestedAt, DateTimeKind.Utc)),
        ReasonCode, Resource);

    public Guid ClaimDispatch(DateTime now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || !(Status == VendorFileDeletionStatuses.Pending ||
            Status == VendorFileDeletionStatuses.Failed && LastErrorRetryable == true ||
            Status == VendorFileDeletionStatuses.Dispatching && LeaseExpiresAt <= now))
            throw new InvalidOperationException("Vendor deletion is not dispatchable.");
        var lease = Guid.NewGuid();
        Status = VendorFileDeletionStatuses.Dispatching;
        LeaseToken = lease; LeaseExpiresAt = now.Add(duration); UpdatedAt = now;
        return lease;
    }

    public void DeliveryAccepted(Guid lease, string kmJobId, DateTime now)
    {
        EnsureLease(lease);
        if (string.IsNullOrWhiteSpace(kmJobId) ||
            KmDeletionJobId is not null && KmDeletionJobId != kmJobId)
            throw new InvalidOperationException("KM Vendor deletion job ID conflicts with persisted state.");
        KmDeletionJobId = kmJobId.Trim(); Status = VendorFileDeletionStatuses.Accepted;
        AttemptCount++; LastErrorCode = null; LastErrorMessage = null;
        LastErrorRetryable = null; ClearLease(now);
    }

    public void DeliveryFailed(Guid lease, string code, string? message, bool retryable, DateTime now)
    {
        EnsureLease(lease); Status = VendorFileDeletionStatuses.Failed; AttemptCount++;
        LastErrorCode = Safe(code, 100); LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = retryable; ClearLease(now);
    }

    public void ApplyCallback(KmDocumentDeletionV2Callback callback, DateTime now)
    {
        if (callback.DeletionRequestId != Id || callback.Resource != Resource ||
            KmDeletionJobId != callback.KmDeletionJobId || !callback.IsValid())
            throw new InvalidOperationException("Vendor deletion callback correlation failed.");
        if (Status == VendorFileDeletionStatuses.Completed)
            throw new InvalidOperationException("Completed Vendor deletion cannot move backwards.");
        if (Status is not (VendorFileDeletionStatuses.Accepted or
            VendorFileDeletionStatuses.Processing or VendorFileDeletionStatuses.Failed))
            throw new InvalidOperationException("Vendor deletion callback is out of order.");
        Status = callback.Status == "Completed" ? VendorFileDeletionStatuses.KmVerified : callback.Status;
        LastErrorCode = callback.Error?.Code; LastErrorMessage = callback.Error?.Message;
        LastErrorRetryable = callback.Error?.Retryable;
        KmVerifiedAt = callback.Status == "Completed" ? callback.Result!.VerifiedAt.UtcDateTime : null;
        LeaseToken = null; LeaseExpiresAt = null; UpdatedAt = now;
    }

    public void BeginLocalCleanup(DateTime now)
    {
        if (Status is not (VendorFileDeletionStatuses.LocalCleanup or
            VendorFileDeletionStatuses.KmVerified or VendorFileDeletionStatuses.CleanupFailed))
            throw new InvalidOperationException("Vendor file is not ready for local cleanup.");
        Status = VendorFileDeletionStatuses.LocalCleanup; LastErrorCode = null;
        LastErrorMessage = null; LastErrorRetryable = null; UpdatedAt = now;
    }

    public void MarkLocalCleanupCompleted(bool physicalFileDeleted, DateTime now)
    {
        if (Status != VendorFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Vendor local cleanup was not started.");
        DbRecordDeletedAt ??= now;
        if (physicalFileDeleted) PhysicalFileDeletedAt ??= now;
        Status = VendorFileDeletionStatuses.Completed; CompletedAt ??= now; UpdatedAt = now;
    }

    public void MarkCleanupFailed(string code, string? message, DateTime now)
    {
        if (Status != VendorFileDeletionStatuses.LocalCleanup)
            throw new InvalidOperationException("Vendor local cleanup was not started.");
        Status = VendorFileDeletionStatuses.CleanupFailed;
        LastErrorCode = Safe(code, 100); LastErrorMessage = Safe(message, 500);
        LastErrorRetryable = true; UpdatedAt = now;
    }

    private void EnsureLease(Guid lease)
    {
        if (Status != VendorFileDeletionStatuses.Dispatching || lease == Guid.Empty || LeaseToken != lease)
            throw new InvalidOperationException("Vendor deletion lease does not match.");
    }

    private void ClearLease(DateTime now)
    {
        LeaseToken = null; LeaseExpiresAt = null; UpdatedAt = now;
    }

    private static string? Safe(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(maxLength).ToArray());
}

public sealed class KmDocumentDeletionV2CallbackReceipt
{
    public Guid CallbackId { get; set; }
    public Guid DeletionRequestId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

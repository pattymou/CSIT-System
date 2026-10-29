namespace SIT.DepartmentSystem.Web.Entities;

public static class EquipmentDeletionStatuses
{
    public const string WaitingForFiles = "WaitingForFiles";
    public const string Finalizing = "Finalizing";
    public const string CleanupFailed = "CleanupFailed";
    public const string Blocked = "Blocked";
    public const string Completed = "Completed";

    public static bool IsActive(string status) => status != Completed;
}

public sealed class EquipmentDeletionJob
{
    public Guid Id { get; private set; }
    public string EquipmentId { get; private set; } = string.Empty;
    public string EquipmentName { get; private set; } = string.Empty;
    public string? ProductsId { get; private set; }
    public string? StorageFolderName { get; private set; }
    public int AttachmentCount { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime RequestedAt { get; private set; }
    public string Status { get; private set; } = EquipmentDeletionStatuses.WaitingForFiles;
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DbRecordDeletedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public int CleanupAttemptCount { get; private set; }

    private EquipmentDeletionJob() { }

    public static EquipmentDeletionJob Create(Apparatus equipment, int attachmentCount,
        string requestedBy, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(equipment.Id) || equipment.Id.Length > 30 ||
            string.IsNullOrWhiteSpace(equipment.Name) || equipment.Name.Length > 200 ||
            attachmentCount < 0 || string.IsNullOrWhiteSpace(requestedBy) ||
            requestedBy.Trim().Length > 100)
            throw new InvalidOperationException("Invalid equipment deletion request.");
        return new EquipmentDeletionJob
        {
            Id = Guid.NewGuid(), EquipmentId = equipment.Id,
            EquipmentName = equipment.Name, ProductsId = equipment.ProductsId,
            StorageFolderName = equipment.StorageFolderName,
            AttachmentCount = attachmentCount, RequestedBy = requestedBy.Trim(),
            RequestedAt = now, UpdatedAt = now
        };
    }

    public void BeginFinalizing(DateTime now)
    {
        if (Status is not (EquipmentDeletionStatuses.WaitingForFiles or
            EquipmentDeletionStatuses.CleanupFailed or EquipmentDeletionStatuses.Blocked))
            throw new InvalidOperationException("Equipment deletion is not ready to finalize.");
        Status = EquipmentDeletionStatuses.Finalizing;
        LastErrorCode = null;
        LastErrorMessage = null;
        CleanupAttemptCount++;
        UpdatedAt = now;
    }

    public void MarkBlocked(string message, DateTime now)
    {
        if (Status == EquipmentDeletionStatuses.Completed)
            throw new InvalidOperationException("Completed equipment deletion cannot be blocked.");
        Status = EquipmentDeletionStatuses.Blocked;
        LastErrorCode = "DELETE_RESTRICTION";
        LastErrorMessage = Safe(message);
        UpdatedAt = now;
    }

    public void MarkCleanupFailed(string code, string message, DateTime now)
    {
        if (Status != EquipmentDeletionStatuses.Finalizing)
            throw new InvalidOperationException("Equipment cleanup was not started.");
        Status = EquipmentDeletionStatuses.CleanupFailed;
        LastErrorCode = string.IsNullOrWhiteSpace(code) ? "CLEANUP_FAILED" : code[..Math.Min(code.Length, 100)];
        LastErrorMessage = Safe(message);
        UpdatedAt = now;
    }

    public void MarkDbRecordDeleted(DateTime now)
    {
        if (Status != EquipmentDeletionStatuses.Finalizing)
            throw new InvalidOperationException("Equipment cleanup was not started.");
        DbRecordDeletedAt ??= now;
        UpdatedAt = now;
    }

    public void MarkCompleted(DateTime now)
    {
        if (Status != EquipmentDeletionStatuses.Finalizing || DbRecordDeletedAt is null)
            throw new InvalidOperationException("Equipment database cleanup is incomplete.");
        Status = EquipmentDeletionStatuses.Completed;
        CompletedAt ??= now;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = now;
    }

    private static string Safe(string value) =>
        new(value.Where(c => !char.IsControl(c)).Take(500).ToArray());
}

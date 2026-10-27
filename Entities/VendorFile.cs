namespace SIT.DepartmentSystem.Web.Entities;

public sealed class VendorFile
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string ArchiveStatus { get; set; } = FileArchiveStatuses.Pending;
    public string? ArchiveError { get; set; }
    public int ArchiveAttemptCount { get; set; }
    public DateTime? ArchiveNextRetryAt { get; set; }
    public DateTime ArchiveUpdatedAt { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string? MetadataJson { get; set; }
    public string? MetadataSource { get; set; }
    public string? MetadataVersion { get; set; }
    public string MetadataVerificationStatus { get; set; } = MetadataVerificationStatuses.NotApplicable;
}

public static class FileArchiveStatuses
{
    public const string Pending = "Pending";
    public const string Archived = "Archived";
    public const string Failed = "Failed";
}

public static class MetadataVerificationStatuses
{
    public const string NotApplicable = "NotApplicable";
    public const string PendingVerification = "PendingVerification";
    public const string Verified = "Verified";
    public const string Rejected = "Rejected";
}

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

    public DocumentLibraryFile DocumentFile { get; set; } = null!;
}

public static class DocumentProcessingStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

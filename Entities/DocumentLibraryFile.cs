namespace SIT.DepartmentSystem.Web.Entities;

public sealed class DocumentLibraryFile
{
    public Guid Id { get; set; }
    public Guid FolderId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StandardFileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string StorageArea { get; set; } = DocumentStorageAreas.Library;

    public DocumentFolder Folder { get; set; } = null!;
    public DocumentProcessingJob? ProcessingJob { get; set; }
}

public static class DocumentStorageAreas
{
    public const string Library = "Library";
    public const string Staging = "Staging";
}

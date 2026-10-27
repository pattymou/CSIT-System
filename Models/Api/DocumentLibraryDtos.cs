namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed class DocumentFolderDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public int DocumentCount { get; set; }
}

public sealed class DocumentFolderUpsertRequest
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
}

public sealed class DocumentLibraryFileDto
{
    public Guid Id { get; set; }
    public Guid? CsitJobId { get; set; }
    public string? KmJobId { get; set; }
    public Guid FolderId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StandardFileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string ProcessingStatus { get; set; } = string.Empty;
    public string ProcessingStatusDisplay { get; set; } = string.Empty;
    public string ProcessingStatusCssClass { get; set; } = string.Empty;
    public string ArchiveStatus { get; set; } = string.Empty;
    public string? ProcessingError { get; set; }
    public string? ArchiveError { get; set; }
    public bool MetadataAvailable { get; set; }
}

public sealed class DocumentUploadBatchResultDto
{
    public List<DocumentUploadResultDto> Results { get; set; } = [];
}

public sealed class DocumentUploadResultDto
{
    public string OriginalFileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DocumentLibraryFileDto? File { get; set; }
}

public sealed class SystemDocumentCatalogDto
{
    public List<SystemDocumentCategoryDto> Categories { get; set; } = [];
}

public sealed class SystemDocumentCategoryDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? EmptyMessage { get; set; }
    public List<SystemDocumentFileDto> Files { get; set; } = [];
}

public sealed class SystemDocumentFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

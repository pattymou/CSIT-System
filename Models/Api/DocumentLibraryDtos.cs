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
    public bool IsSystemManaged { get; set; }
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
    public string ReviewStatusDisplay { get; set; } = string.Empty;
    public string ReviewStatusTone { get; set; } = string.Empty;
    public string ArchiveStatusDisplay { get; set; } = string.Empty;
    public string ArchiveStatusTone { get; set; } = string.Empty;
    public string KmStatusDisplay { get; set; } = string.Empty;
    public string KmStatusTone { get; set; } = string.Empty;
    public string? ProcessingError { get; set; }
    public string? ArchiveError { get; set; }
    public bool MetadataAvailable { get; set; }
}

public sealed class DocumentArchiveStatusBatchRequest
{
    public List<Guid> FileIds { get; set; } = [];
}

public sealed class DocumentArchiveStatusDto
{
    public Guid FileId { get; set; }
    public string ArchiveStatus { get; set; } = string.Empty;
    public string ArchiveStatusDisplay { get; set; } = string.Empty;
    public string ArchiveStatusTone { get; set; } = string.Empty;
    public string? ArchiveError { get; set; }
    public bool ContinueTracking { get; set; }
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
    public Guid? SubmissionId { get; set; }
    public string ApprovalResult { get; set; } = string.Empty;
    public DocumentLibraryFileDto? File { get; set; }
}

public sealed class SystemDocumentCatalogDto
{
    public List<SystemDocumentFolderDto> Folders { get; set; } = [];
}

public sealed class SystemDocumentFolderDto
{
    public string Key { get; set; } = string.Empty;
    public string? ParentKey { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public int DocumentCount { get; set; }
    public bool CanUpload { get; set; }
    public string? UploadModelName { get; set; }
    public List<SystemDocumentFileDto> Files { get; set; } = [];
}

public sealed class SystemDocumentFileDto
{
    public Guid? FileId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public string? UploadedBy { get; set; }
    public string? ContentType { get; set; }
    public bool IsImage { get; set; }
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string ProcessingStatusDisplay { get; set; } = string.Empty;
    public string ProcessingStatusCssClass { get; set; } = string.Empty;
    public string ReviewStatusDisplay { get; set; } = string.Empty;
    public string ReviewStatusTone { get; set; } = string.Empty;
    public string ArchiveStatusDisplay { get; set; } = string.Empty;
    public string ArchiveStatusTone { get; set; } = string.Empty;
    public string ArchiveStatus { get; set; } = string.Empty;
    public string? ArchiveError { get; set; }
    public string KmStatusDisplay { get; set; } = string.Empty;
    public string KmStatusTone { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
}

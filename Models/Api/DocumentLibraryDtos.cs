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
    public Guid FolderId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StandardFileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

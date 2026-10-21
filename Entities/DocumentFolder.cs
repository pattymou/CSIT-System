namespace SIT.DepartmentSystem.Web.Entities;

public sealed class DocumentFolder
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DocumentFolder? Parent { get; set; }
    public List<DocumentFolder> Children { get; set; } = [];
    public List<DocumentLibraryFile> Files { get; set; } = [];
}

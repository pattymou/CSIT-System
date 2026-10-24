namespace SIT.DepartmentSystem.Web.Entities;

public class VerificationApplicationFile
{
    public Guid Id { get; set; }
    public Guid VerificationApplicationId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid? DocumentLibraryFileId { get; set; }

    public VerificationApplication VerificationApplication { get; set; } = null!;
    public DocumentLibraryFile? DocumentLibraryFile { get; set; }
}

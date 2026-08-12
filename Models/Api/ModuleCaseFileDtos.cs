namespace SIT.DepartmentSystem.Web.Models.Api;

public class ModuleCaseFileDto
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public Guid? CaseId { get; set; }
    public string CaseNo { get; set; } = string.Empty;
    public Guid? TaskId { get; set; }
    public string? TaskNo { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string? UploadEmp { get; set; }

    public DateTime CreatedAt { get; set; }
}

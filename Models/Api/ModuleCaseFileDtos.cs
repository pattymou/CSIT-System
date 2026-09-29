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
    public Guid DocumentId { get; set; }
    public int VersionNo { get; set; }
    public string FileKind { get; set; } = string.Empty;
    public string? ReviewStatus { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewComment { get; set; }
    public bool IsFinal { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public string? FinalizedBy { get; set; }
    public string? Sha256 { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TestReportReviewRequest
{
    public string Decision { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

public sealed class PendingTestReportDto : ModuleCaseFileDto
{
    public string ProjectNo { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string CaseName { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
}

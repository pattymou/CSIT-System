namespace SIT.DepartmentSystem.Web.Models.Api;

public class VerificationWorkListItemDto
{
    public Guid ApplicationId { get; set; }
    public Guid RecordId { get; set; }
    public string ApplicationNo { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public DateOnly? ExpectedEndDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public int Progress { get; set; }
    public bool RequiresSetupReview { get; set; }
}

public sealed class VerificationWorkDetailDto : VerificationWorkListItemDto
{
    public string? DeletionStatus { get; set; }
    public IReadOnlyList<SelectedVerificationTestItemDto> SelectedTestItems { get; set; } = [];
    public string? SnapshotMessage { get; set; }
    public IReadOnlyList<VerificationWorkTaskDto> Tasks { get; set; } = [];
}

public sealed class SelectedVerificationTestItemDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public Guid? TaskId { get; set; }
    public string? TaskStatus { get; set; }
    public IReadOnlyList<VerificationTestParameterDto> Parameters { get; set; } = [];
}

public sealed record EnableSelectedWorkResponse(Guid TaskId);

public sealed record VerificationTestParameterDto(string Name, IReadOnlyList<string> Values);

public sealed class VerificationWorkTaskDetailDto
{
    public string? DeletionStatus { get; set; }
    public Guid RecordId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ApplicationNo { get; set; } = string.Empty;
    public SelectedVerificationTestItemDto SelectedItem { get; set; } = null!;
    public VerificationWorkTaskDto Task { get; set; } = null!;
}

public sealed class VerificationWorkTaskDto
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? AssignEngineer { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateOnly? ActualCompletionDate { get; set; }
    public string? Note { get; set; }
    public string? SourceTestItemCode { get; set; }
    public IReadOnlyList<VerificationWorkReportDto> Reports { get; set; } = [];
}

public sealed class VerificationWorkReportDto
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int VersionNo { get; set; }
    public string? ReviewStatus { get; set; }
    public bool IsFinal { get; set; }
    public string FormalArchiveStatus { get; set; } = "NotCommitted";
    public string FormalArchiveStatusDisplay { get; set; } = "尚未正式入庫";
    public string KmAnalysisStatus { get; set; } = "NotSent";
    public string KmAnalysisStatusDisplay { get; set; } = "尚未送 KM";
    public string? KmAnalysisError { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UpdateVerificationWorkStatusRequest
{
    public string Status { get; set; } = string.Empty;
}

public sealed class UpdateVerificationWorkTaskRequest
{
    public string Status { get; set; } = string.Empty;
    public string? AssignEngineer { get; set; }
    public DateOnly? ActualCompletionDate { get; set; }
    public string? Note { get; set; }
}

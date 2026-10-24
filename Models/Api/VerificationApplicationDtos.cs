using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Models.Api;

public class CreateVerificationApplicationRequest : VerificationApplicationContentRequest;

public class UpdateVerificationApplicationRequest : VerificationApplicationContentRequest;

public abstract class VerificationApplicationContentRequest
{
    public Guid RoutingDepartmentOptionId { get; set; }
    public Guid TeamOptionId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? SubPu { get; set; }
    public string? Customer { get; set; }
    public string? ProductModel { get; set; }
    public DateOnly? RequestedFinishDate { get; set; }
    public string? ValidationRequirement { get; set; }
    public string? HardwareVersion { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public DateOnly? SampleReadyDate { get; set; }
    public string? JiraLink { get; set; }
    public string? Location { get; set; }
    public string? Npi { get; set; }
    public string? WirelessDrive { get; set; }
    public string? Chipset { get; set; }
    public string? SampleMacAddress { get; set; }
    public string? UtilityVersion { get; set; }
    public string? DspModel { get; set; }
    public ValidationRequestSubmissionDto? ValidationRequest { get; set; }
}

public class VerificationApplicationDto
{
    public Guid Id { get; set; }
    public string ApplicationNo { get; set; } = string.Empty;
    public Guid? RoutingDepartmentOptionId { get; set; }
    public string? RoutingDepartmentCode { get; set; }
    public string? RoutingDepartmentName { get; set; }
    public Guid? TeamOptionId { get; set; }
    public string? TeamCode { get; set; }
    public string? TeamName { get; set; }
    public string ApplicantAccount { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string ApplicantEmail { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string? ApplicantExtension { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? SubPu { get; set; }
    public string? Customer { get; set; }
    public string? ProductModel { get; set; }
    public DateOnly? RequestedFinishDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }
    public string? ValidationRequirement { get; set; }
    public string? HardwareVersion { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public DateOnly? SampleReadyDate { get; set; }
    public string? JiraLink { get; set; }
    public string? Location { get; set; }
    public string? Npi { get; set; }
    public string? WirelessDrive { get; set; }
    public string? Chipset { get; set; }
    public string? SampleMacAddress { get; set; }
    public string? UtilityVersion { get; set; }
    public string? DspModel { get; set; }
    public VerificationApplicationStatus Status { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessedBy { get; set; }
    public string? ProcessingNote { get; set; }
    public string? ModuleRecordNo { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<VerificationApplicationFileDto> Files { get; set; } = [];
    public ValidationRequestSubmissionDto? ValidationRequest { get; set; }
}

public class VerificationApplicationFileDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string? ModelName { get; set; }
    public Guid? DocumentFolderId { get; set; }
    public Guid? DocumentLibraryFileId { get; set; }
    public string FormalArchiveStatus { get; set; } = VerificationAttachmentArchiveStatuses.NotApplicable;
    public string? FormalArchiveError { get; set; }
    public string KmAnalysisStatus { get; set; } = VerificationAttachmentKmStatuses.NotApplicable;
    public string KmAnalysisStatusDisplay { get; set; } = "尚未正式入庫";
    public string? KmAnalysisError { get; set; }
}

public static class VerificationAttachmentKmStatuses
{
    public const string NotApplicable = "NotApplicable";
    public const string Disabled = "Disabled";
    public const string WaitingNotification = "WaitingNotification";
    public const string WaitingAnalysis = "WaitingAnalysis";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed class VerificationApplicationFileUploadResultDto
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public VerificationApplicationFileDto? File { get; set; }
}

public sealed class VerificationApplicationFileUploadBatchResultDto
{
    public List<VerificationApplicationFileUploadResultDto> Results { get; set; } = [];
}

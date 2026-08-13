using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Models.Api;

public class CreateVerificationApplicationRequest : VerificationApplicationContentRequest;

public class UpdateVerificationApplicationRequest : VerificationApplicationContentRequest;

public abstract class VerificationApplicationContentRequest
{
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
}

public class VerificationApplicationDto
{
    public Guid Id { get; set; }
    public string ApplicationNo { get; set; } = string.Empty;
    public string ModuleCode { get; set; } = string.Empty;
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
    public IReadOnlyList<VerificationApplicationFileDto> Files { get; set; } = [];
}

public class VerificationApplicationFileDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
}

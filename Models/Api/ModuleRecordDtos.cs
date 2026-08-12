namespace SIT.DepartmentSystem.Web.Models.Api;

public class ModuleRecordListItemDto
{
    public Guid Id { get; set; }
    public string RecordNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Customer { get; set; }
    public string Status { get; set; } = "Open";
    public int Progress { get; set; }

    public string? Team { get; set; }
    public string? Npi { get; set; }
    public string? HardwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? HardwareEngineer { get; set; }
    public string? SoftwareEngineer { get; set; }
    public string? Pjm { get; set; }
    public string? Location { get; set; }
    public string? RequestDepartment { get; set; }
    public string? RequestApplicant { get; set; }

    public string? SubPu { get; set; }
    public string? AssignOwner { get; set; }
    public string? MechanicalEngineer { get; set; }
    public string? Department { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? WirelessDrive { get; set; }
    public string? CustomerProductName { get; set; }
    public string? Chipset { get; set; }
    public string? SampleMacAddress { get; set; }
    public string? UtilityVersion { get; set; }
    public string? DspModel { get; set; }
    public string? DqaOwner { get; set; }
    public string? JiraLink { get; set; }
}

public class ModuleRecordDetailDto
{
    public Guid Id { get; set; }
    public Guid ModuleId { get; set; }

    public string RecordNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Customer { get; set; }

    public string? Owner { get; set; }
    public string? PmSales { get; set; }

    public string Status { get; set; } = "Open";
    public string? Result { get; set; }
    public int Progress { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }
    public DateOnly? SampleReadyDate { get; set; }

    public string? Note { get; set; }
    public string? ApplicantNote { get; set; }

    public string? Team { get; set; }
    public string? Npi { get; set; }
    public string? HardwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? HardwareEngineer { get; set; }
    public string? SoftwareEngineer { get; set; }
    public string? Pjm { get; set; }
    public string? Location { get; set; }
    public string? RequestDepartment { get; set; }
    public string? RequestApplicant { get; set; }

    public string? SubPu { get; set; }
    public string? AssignOwner { get; set; }
    public string? MechanicalEngineer { get; set; }
    public string? Department { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? WirelessDrive { get; set; }
    public string? CustomerProductName { get; set; }
    public string? Chipset { get; set; }
    public string? SampleMacAddress { get; set; }
    public string? UtilityVersion { get; set; }
    public string? DspModel { get; set; }
    public string? DqaOwner { get; set; }
    public string? JiraLink { get; set; }

    public List<string> NotifyUsers { get; set; } = new();
}

public class ModuleRecordUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Customer { get; set; }

    public string? Owner { get; set; }
    public string? PmSales { get; set; }

    public string Status { get; set; } = "Open";
    public string? Result { get; set; }
    public int Progress { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }
    public DateOnly? SampleReadyDate { get; set; }

    public string? Note { get; set; }
    public string? ApplicantNote { get; set; }

    public string? Team { get; set; }
    public string? Npi { get; set; }
    public string? HardwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? HardwareEngineer { get; set; }
    public string? SoftwareEngineer { get; set; }
    public string? Pjm { get; set; }
    public string? Location { get; set; }
    public string? RequestDepartment { get; set; }
    public string? RequestApplicant { get; set; }

    public string? SubPu { get; set; }
    public string? AssignOwner { get; set; }
    public string? MechanicalEngineer { get; set; }
    public string? Department { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? WirelessDrive { get; set; }
    public string? CustomerProductName { get; set; }
    public string? Chipset { get; set; }
    public string? SampleMacAddress { get; set; }
    public string? UtilityVersion { get; set; }
    public string? DspModel { get; set; }
    public string? DqaOwner { get; set; }
    public string? JiraLink { get; set; }

    public List<string> NotifyUsers { get; set; } = new();
}
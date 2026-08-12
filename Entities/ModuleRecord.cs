namespace SIT.DepartmentSystem.Web.Entities;

public class ModuleRecord
{
    public Guid Id { get; set; }
    public Guid ModuleId { get; set; }

    // 舊系統：申請單編號
    public string RecordNo { get; set; } = string.Empty;

    // 舊系統：專案名稱
    public string Name { get; set; } = string.Empty;

    // 舊系統：Customer
    public string? Customer { get; set; }

    // 公版原有欄位
    public string? Owner { get; set; }
    public string? PmSales { get; set; }

    // 舊系統：Open / Close / Hold
    public string Status { get; set; } = "Open";

    public string? Result { get; set; }
    public int Progress { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }
    public DateOnly? SampleReadyDate { get; set; }

    public string? Note { get; set; }
    public string? ApplicantNote { get; set; }

    // ===== 舊系統第一層欄位 =====
    public string? Team { get; set; }
    public string? Npi { get; set; }
    public string? HardwareVersion { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? HardwareEngineer { get; set; }
    public string? SoftwareEngineer { get; set; }
    public string? Pjm { get; set; }
    public string? Location { get; set; }              // 台北 / 吳江
    public string? RequestDepartment { get; set; }     // 申請人部門
    public string? RequestApplicant { get; set; }      // 申請人
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
    public string? NotifyUsers { get; set; }   // 先用逗號字串存

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ModuleEntity Module { get; set; } = null!;
    public ICollection<ModuleRecordCase> Cases { get; set; } = new List<ModuleRecordCase>();
}
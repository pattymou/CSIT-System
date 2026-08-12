namespace SIT.DepartmentSystem.Web.Entities;

public class ModuleRecordCase
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public string CaseNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string? Note { get; set; }
    public int SortOrder { get; set; }

    public string? WifiNo { get; set; }
    public string? BtNo { get; set; }
    public string? GcfNo { get; set; }
    public string? PtcrbNo { get; set; }

    public bool IsDraft { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ModuleRecord Record { get; set; } = null!;
    public ICollection<ModuleRecordTask> Tasks { get; set; } = new List<ModuleRecordTask>();
    public ICollection<ModuleCaseFile> Files { get; set; } = new List<ModuleCaseFile>();
}

namespace SIT.DepartmentSystem.Web.Entities;

public class ModuleRecordTask
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string TaskNo { get; set; } = string.Empty;

    // 舊系統欄位：子任務名稱
    public string Name { get; set; } = string.Empty;

    // 舊系統欄位：指派工程師，多人以逗號字串保存
    public string? AssignEngineer { get; set; }

    // 舊系統欄位：狀態 / 開始日期 / 預計完成日 / 結果判定 / 進度
    public string Status { get; set; } = "Open";
    public string? Result { get; set; }
    public int Progress { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }

    // 舊系統欄位：Sub PU / 機種名稱 / 實驗室名稱 / 報價金額 / 請款金額 / 備註
    public string? SubPu { get; set; }
    public string? ModelName { get; set; }
    public string? Lab { get; set; }
    public string? Quoted { get; set; }
    public string? Reimburse { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ModuleRecordCase Case { get; set; } = null!;
}

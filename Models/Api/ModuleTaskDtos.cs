namespace SIT.DepartmentSystem.Web.Models.Api;

public class ModuleTaskListItemDto
{
    public Guid Id { get; set; }

    // 🔥 一定要補
    public Guid CaseId { get; set; }

    public string TaskNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AssignEngineer { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public int Progress { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }
}

public class ModuleTaskDetailDto
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }

    // 🔥 關鍵（Task 檔案綁定靠這個）
    public string TaskNo { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? AssignEngineer { get; set; }

    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public int Progress { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }

    public string? SubPu { get; set; }
    public string? ModelName { get; set; }
    public string? Lab { get; set; }
    public string? Quoted { get; set; }
    public string? Reimburse { get; set; }
    public string? Note { get; set; }
}

public class ModuleTaskUpsertRequest
{
    // 🔥 核心（前端先拿 TaskNo 再上傳）
    public string? TaskNo { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? AssignEngineer { get; set; }

    public string Status { get; set; } = "Open";
    public string? Result { get; set; }
    public int Progress { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? ExpectedEndDate { get; set; }

    public string? SubPu { get; set; }
    public string? ModelName { get; set; }
    public string? Lab { get; set; }
    public string? Quoted { get; set; }
    public string? Reimburse { get; set; }
    public string? Note { get; set; }
}
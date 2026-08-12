namespace SIT.DepartmentSystem.Web.Models.Api;

public class ModuleCaseListItemDto
{
    public Guid Id { get; set; }
    public string CaseNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class ModuleCaseDetailDto
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public string CaseNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int SortOrder { get; set; }

    public string? WifiNo { get; set; }
    public string? BtNo { get; set; }
    public string? GcfNo { get; set; }
    public string? PtcrbNo { get; set; }
}

public class ModuleCaseUpsertRequest
{
    public string? CaseNo { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string? Note { get; set; }
    public int SortOrder { get; set; }

    public string? WifiNo { get; set; }
    public string? BtNo { get; set; }
    public string? GcfNo { get; set; }
    public string? PtcrbNo { get; set; }
}

public class NewCaseNoResponse
{
    public string CaseNo { get; set; } = string.Empty;
}

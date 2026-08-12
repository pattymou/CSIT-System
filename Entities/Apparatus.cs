namespace SIT.DepartmentSystem.Web.Entities;

public class Apparatus
{
    public string Id { get; set; } = string.Empty;

    // 公版資產管理分流用。
    // equipment = 設備管理，goods = 貨品管理，consumables = 耗材管理...
    public string ModuleCode { get; set; } = "equipment";

    public string? ProductsId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string? PartNo { get; set; }
    public string? Manufacturer { get; set; }
    public string? ManufacturerNumber { get; set; }

    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? ProcurementStaff { get; set; }

    public string? Imei { get; set; }
    public string? Os { get; set; }
    public string? OsVersion { get; set; }

    public DateOnly? InspectionDate { get; set; }
    public DateOnly? MaintenanceDate { get; set; }

    public string? Place { get; set; }

    public string? CostPrice { get; set; }
    public string? YearsUse { get; set; }
    public string? DaysUse { get; set; }
    public string? PriceUse { get; set; }

    public string? CustodianDepartment { get; set; }
    public string Custodian { get; set; } = string.Empty;
    public string? Agent { get; set; }

    public string ReservationStatus { get; set; } = "可借用";

    public string? Feature { get; set; }
    public string? Spec { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // PostgreSQL xmin concurrency token.
    // EF 會用它判斷資料是否已被其他人修改，避免後儲存者覆蓋前一個人的內容。
    public uint Xmin { get; set; }

    public List<ApparatusFile> Files { get; set; } = new();
}

namespace SIT.DepartmentSystem.Web.Models.Api;

public class ApparatusListItemDto
{
    public string Id { get; set; } = "";
    public string ModuleCode { get; set; } = "equipment";
    public string? ProductsId { get; set; }
    public string Name { get; set; } = "";
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? ReservationStatus { get; set; }
    public string? Place { get; set; }
    public string? Custodian { get; set; }
}

public class ApparatusDetailDto
{
    public string Id { get; set; } = "";
    public string ModuleCode { get; set; } = "equipment";
    public string? RowVersion { get; set; }
    public string? ProductsId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Kind { get; set; }
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
    public string? Custodian { get; set; }
    public string? Agent { get; set; }
    public string? ReservationStatus { get; set; }
    public string? Feature { get; set; }
    public string? Spec { get; set; }
    public string? Note { get; set; }

    public List<ApparatusFileDto> Files { get; set; } = new();
}

public class ApparatusUpsertRequest
{
    public string? Id { get; set; }
    public string? ModuleCode { get; set; }
    public string? RowVersion { get; set; }
    public string? ProductsId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Kind { get; set; }
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
    public string? Custodian { get; set; }
    public string? Agent { get; set; }
    public string? ReservationStatus { get; set; }
    public string? Feature { get; set; }
    public string? Spec { get; set; }
    public string? Note { get; set; }
}

public class ApparatusFileDto
{
    public Guid Id { get; set; }
    public string ApparatusId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string ImageUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class NewApparatusIdResponse
{
    public string Id { get; set; } = "";
}

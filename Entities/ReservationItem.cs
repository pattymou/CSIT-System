namespace SIT.DepartmentSystem.Web.Entities;

public class ReservationItem
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public string ApparatusId { get; set; } = string.Empty;
    public Guid? EquipmentGroupRequirementId { get; set; }
    public string? RequirementResourceTypeSnapshot { get; set; }
    public string? RequirementCapabilityTagSnapshot { get; set; }

    public string ApparatusName { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
    public string? Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Number { get; set; }
    public string? Place { get; set; }
    public string? Custodian { get; set; }
    public string? CustodianDepartment { get; set; }
    public string? PriceUse { get; set; }

    public Reservation Reservation { get; set; } = null!;
    public Apparatus Apparatus { get; set; } = null!;
    public EquipmentGroupRequirement? EquipmentGroupRequirement { get; set; }
}

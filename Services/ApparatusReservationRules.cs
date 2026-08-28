using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

internal static class ApparatusReservationRules
{
    public const string EquipmentModuleCode = "equipment";
    public const string BookableStatus = "可借用";

    public static bool IsBookable(Apparatus apparatus) =>
        IsBookable(apparatus.ModuleCode, apparatus.ReservationStatus);

    public static bool IsBookable(string? moduleCode, string? reservationStatus) =>
        string.Equals(moduleCode, EquipmentModuleCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reservationStatus, BookableStatus, StringComparison.Ordinal);
}

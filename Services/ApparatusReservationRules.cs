using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services;

internal static class ApparatusReservationRules
{
    public const string EquipmentModuleCode = "equipment";
    public const string BookableStatus = "可借用";
    public const string CalibratingStatus = "校驗中";
    public const string UnderMaintenanceStatus = "異常維修中";
    public const string UnavailableStatus = "不可借用";
    public const string ProcurementStatus = "採購中";

    public static bool IsBookable(Apparatus apparatus) =>
        IsBookable(apparatus.ModuleCode, apparatus.ReservationStatus);

    public static bool IsBookable(string? moduleCode, string? reservationStatus) =>
        string.Equals(moduleCode, EquipmentModuleCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reservationStatus, BookableStatus, StringComparison.Ordinal);

    public static EnvironmentReadinessIssueType GetReadinessIssueType(string? reservationStatus) => reservationStatus switch
    {
        CalibratingStatus => EnvironmentReadinessIssueType.DeviceCalibrating,
        UnderMaintenanceStatus => EnvironmentReadinessIssueType.DeviceUnderMaintenance,
        UnavailableStatus => EnvironmentReadinessIssueType.DeviceUnavailable,
        ProcurementStatus => EnvironmentReadinessIssueType.DeviceProcurement,
        _ => EnvironmentReadinessIssueType.DeviceNotBookable
    };
}

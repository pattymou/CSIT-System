using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

internal static class EnvironmentGroupDeviceRules
{
    public static EquipmentGroupCompletenessStatus GetCompletenessStatus(int totalDeviceCount, int inEnvironmentDeviceCount) =>
        totalDeviceCount == 0
            ? EquipmentGroupCompletenessStatus.Unconfigured
            : inEnvironmentDeviceCount == totalDeviceCount
                ? EquipmentGroupCompletenessStatus.Complete
                : EquipmentGroupCompletenessStatus.Incomplete;
}

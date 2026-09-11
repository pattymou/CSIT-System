namespace SIT.DepartmentSystem.Web.Models.Api;

public enum EnvironmentReadinessStatus
{
    Ready,
    NotReady
}

public enum EnvironmentReadinessIssueType
{
    GroupInactive,
    Unconfigured,
    DeviceMissing,
    DeviceNotPresent,
    DeviceNotEquipment,
    DeviceCalibrating,
    DeviceUnderMaintenance,
    DeviceUnavailable,
    DeviceProcurement,
    DeviceNotBookable
}

public sealed class EnvironmentReadinessDto
{
    public Guid EquipmentGroupId { get; set; }
    public string GroupCode { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public EnvironmentReadinessStatus Status { get; set; }
    public int TotalDeviceCount { get; set; }
    public int PresentDeviceCount { get; set; }
    public List<EnvironmentReadinessIssueDto> Issues { get; set; } = new();
}

public sealed class EnvironmentReadinessIssueDto
{
    public EnvironmentReadinessIssueType Type { get; set; }
    public string? ApparatusId { get; set; }
    public string? ApparatusName { get; set; }
    public string? ApparatusProductsId { get; set; }
    public string? ApparatusReservationStatus { get; set; }
    public string Message { get; set; } = string.Empty;
}

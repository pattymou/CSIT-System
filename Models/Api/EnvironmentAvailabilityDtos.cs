using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Models.Api;

public sealed class EnvironmentAvailabilityDto
{
    public Guid EquipmentGroupId { get; set; }
    public string GroupCode { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsTimeAvailable { get; set; }
    public int MembershipDeviceCount { get; set; }
    public int BlockingReservationCount { get; set; }
    public int BlockingDeviceCount { get; set; }
    public List<EnvironmentAvailabilityConflictDto> Conflicts { get; set; } = new();
}

public sealed class EnvironmentAvailabilityConflictDto
{
    public Guid ReservationId { get; set; }
    public string ReservationNo { get; set; } = string.Empty;
    public ReservationStatus ReservationStatus { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public ReservationMode ReservationMode { get; set; }
    public List<EnvironmentAvailabilityRelatedDeviceDto> RelatedDevices { get; set; } = new();
}

public sealed class EnvironmentAvailabilityRelatedDeviceDto
{
    public string ApparatusId { get; set; } = string.Empty;
    public string ApparatusName { get; set; } = string.Empty;
    public string? ProductsId { get; set; }
}

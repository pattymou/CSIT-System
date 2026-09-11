using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IEnvironmentAvailabilityService
{
    Task<EnvironmentAvailabilityDto> GetAvailabilityAsync(
        Guid equipmentGroupId,
        DateTime startTime,
        DateTime endTime,
        Guid? excludeReservationId = null,
        CancellationToken cancellationToken = default);
}

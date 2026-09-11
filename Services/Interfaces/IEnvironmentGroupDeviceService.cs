using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IEnvironmentGroupDeviceService
{
    Task<List<EquipmentGroupDeviceDto>> ListDevicesAsync(Guid groupId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<List<EquipmentGroupDeviceCandidateDto>> ListCandidatesAsync(Guid groupId, string? keyword, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<List<EquipmentGroupDeviceDto>> AddDevicesAsync(Guid groupId, AddEquipmentGroupDevicesRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<bool> DeleteDeviceAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<EquipmentGroupDeviceDto> UpdatePresenceAsync(Guid id, UpdateEquipmentGroupDevicePresenceRequest request, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ApparatusEnvironmentAssignmentDto?> GetApparatusAssignmentAsync(string apparatusId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task EnsureDirectReservationAllowedAsync(IReadOnlyCollection<string> apparatusIds, CancellationToken cancellationToken = default);
}

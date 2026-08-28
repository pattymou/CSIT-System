using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface ISystemOptionService
{
    Task<List<SystemOptionDto>> GetAllAsync(string? category);

    Task<List<SystemOptionDto>> GetEnabledByCategoryAsync(string category);

    Task<bool> TeamExistsAsync(Guid teamOptionId, CancellationToken cancellationToken = default);

    Task<List<TeamRoutingDto>> GetTeamRoutingsAsync(CancellationToken cancellationToken = default);

    Task<TeamLeaderResolution> GetActiveTeamLeaderAsync(Guid teamOptionId, CancellationToken cancellationToken = default);

    Task<Guid> CreateTeamRoutingAsync(TeamRoutingUpsertRequest request, CancellationToken cancellationToken = default);

    Task<bool> UpdateTeamRoutingAsync(Guid id, TeamRoutingUpsertRequest request, CancellationToken cancellationToken = default);

    Task<Guid> CreateAsync(SystemOptionUpsertRequest request);

    Task<bool> UpdateAsync(Guid id, SystemOptionUpsertRequest request);

    Task<bool> DeleteAsync(Guid id);
}

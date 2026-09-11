using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IEnvironmentReadinessService
{
    Task<EnvironmentReadinessDto> GetReadinessAsync(
        Guid equipmentGroupId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, EnvironmentReadinessDto>> GetReadinessForGroupsAsync(
        IReadOnlyCollection<Guid> equipmentGroupIds,
        CancellationToken cancellationToken = default);
}

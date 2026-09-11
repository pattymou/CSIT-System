using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IApparatusResourceCapabilityService
{
    Task<IReadOnlyList<ApparatusResourceCapabilityDto>> GetByApparatusAsync(
        string apparatusId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApparatusResourceCapabilityDto>> ReplaceAsync(
        string apparatusId,
        IReadOnlyCollection<ApparatusResourceCapabilityInput> mappings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceMatchingCatalogValueDto>> GetCatalogValuesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetMatchingApparatusIdsAsync(
        string resourceType,
        string? capabilityTag,
        IReadOnlyCollection<string>? apparatusIds = null,
        CancellationToken cancellationToken = default);
}

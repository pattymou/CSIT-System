using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IPlannedTestItemService
{
    Task<Guid> CreateAsync(PlannedTestItemCreateRequest request, CancellationToken cancellationToken = default);
    Task<PlannedTestItemDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<PlannedTestItemDto>> ListByModuleRecordAsync(Guid moduleRecordId, CancellationToken cancellationToken = default);
    Task<bool> ChangeStatusAsync(Guid id, PlannedTestItemStatus status, CancellationToken cancellationToken = default);
}

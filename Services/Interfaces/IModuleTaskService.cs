using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IModuleTaskService
{
    Task<ListResponseDto<ModuleTaskListItemDto>> GetListAsync(Guid caseId, int page, int pageSize, string? status);
    Task<ModuleTaskDetailDto?> GetByIdAsync(Guid id);
    Task<Guid> CreateAsync(Guid caseId, ModuleTaskUpsertRequest request);
    Task<bool> UpdateAsync(Guid id, ModuleTaskUpsertRequest request);
    Task<bool> DeleteAsync(Guid id);
}
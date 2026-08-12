using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IModuleCaseService
{
    Task<ListResponseDto<ModuleCaseListItemDto>> GetListAsync(Guid recordId, int page, int pageSize, string? status);
    Task<ModuleCaseDetailDto?> GetByIdAsync(Guid id);
    Task<NewCaseNoResponse> GetNewCaseNoAsync(Guid recordId);
    Task<Guid> CreateAsync(Guid recordId, ModuleCaseUpsertRequest request);
    Task<bool> UpdateAsync(Guid id, ModuleCaseUpsertRequest request);
    Task<bool> DeleteAsync(Guid id);
}

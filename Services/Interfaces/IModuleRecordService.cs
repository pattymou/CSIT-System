using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IModuleRecordService
{
    Task<ListResponseDto<ModuleRecordListItemDto>> GetListAsync(string moduleCode, int page, int pageSize, string? status);

    Task<ListResponseDto<ModuleRecordListItemDto>> GetProjectViewListAsync(
        string moduleCode,
        string status,
        string site,
        string? search,
        int page,
        int pageSize);

    Task<ModuleRecordDetailDto?> GetByIdAsync(Guid id);
    Task<Guid> CreateAsync(string moduleCode, ModuleRecordUpsertRequest request);
    Task<bool> UpdateAsync(Guid id, ModuleRecordUpsertRequest request);
    Task<bool> DeleteAsync(Guid id);
}
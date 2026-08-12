using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IModuleService
{
    Task<List<ModuleListItemDto>> GetEnabledModulesAsync();
    Task<List<ModuleListItemDto>> GetAllAsync();
    Task<ModuleDetailDto?> GetByCodeAsync(string code);
    Task<Guid> CreateAsync(ModuleUpsertRequest request);
    Task<bool> UpdateAsync(Guid id, ModuleUpsertRequest request);
    Task<List<ModuleRecordListItemDto>> GetModuleRecordsAsync(Guid moduleId, string status);
}
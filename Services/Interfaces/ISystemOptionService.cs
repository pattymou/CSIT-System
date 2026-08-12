using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface ISystemOptionService
{
    Task<List<SystemOptionDto>> GetAllAsync(string? category);

    Task<List<SystemOptionDto>> GetEnabledByCategoryAsync(string category);

    Task<Guid> CreateAsync(SystemOptionUpsertRequest request);

    Task<bool> UpdateAsync(Guid id, SystemOptionUpsertRequest request);

    Task<bool> DeleteAsync(Guid id);
}
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IVerificationCategoryService
{
    Task<Guid> CreateAsync(VerificationCategoryUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(Guid id, VerificationCategoryUpsertRequest request, CancellationToken cancellationToken = default);
    Task<VerificationCategoryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<VerificationCategoryDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<List<VerificationCategoryOptionDto>> ListActiveOptionsAsync(CancellationToken cancellationToken = default);
}

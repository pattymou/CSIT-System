using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IVendorService
{
    Task<List<VendorListItemDto>> SearchAsync(string? keyword, bool includeDisabled, CancellationToken cancellationToken = default);
    Task<List<VendorOptionDto>> OptionsAsync(Guid? includeVendorId = null, CancellationToken cancellationToken = default);
    Task<VendorDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(VendorUpsertRequest request, string actor, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(Guid id, VendorUpsertRequest request, string actor, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task<List<ManufacturerMatchReportItemDto>> ManufacturerMatchReportAsync(CancellationToken cancellationToken = default);
}

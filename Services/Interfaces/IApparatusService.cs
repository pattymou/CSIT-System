using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IApparatusService
{
    Task<string> GenerateNewIdAsync();

    Task<List<ApparatusListItemDto>> GetListAsync(string moduleCode, string? keyword, string? kind);
    Task<ApparatusDetailDto?> GetByIdAsync(string moduleCode, string id);
    Task<string> CreateAsync(string moduleCode, ApparatusUpsertRequest request);
    Task<bool> UpdateAsync(string moduleCode, string id, ApparatusUpsertRequest request);
    Task<bool> DeleteAsync(string moduleCode, string id);

    Task<List<ApparatusFileDto>> GetFilesAsync(string moduleCode, string apparatusId);
    Task UploadFilesAsync(string moduleCode, string apparatusId, IReadOnlyList<IFormFile> files, string? uploadEmp);
    Task<(byte[] Content, string FileName, string ContentType)> GetFileContentAsync(Guid fileId);
    Task<bool> DeleteFileAsync(Guid fileId);
}

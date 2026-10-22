using Microsoft.AspNetCore.Http;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IDocumentLibraryService
{
    Task<List<DocumentFolderDto>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateFolderAsync(DocumentFolderUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> UpdateFolderAsync(Guid id, DocumentFolderUpsertRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<DocumentLibraryFileDto>> GetFilesAsync(Guid folderId, CancellationToken cancellationToken = default);
    Task<DocumentLibraryFileDto> UploadAsync(Guid folderId, IFormFile file, string uploadedBy, CancellationToken cancellationToken = default);
    Task<DocumentDownload?> OpenDownloadAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task<SystemDocumentCatalogDto> GetSystemDocumentsAsync(CancellationToken cancellationToken = default);
}

public sealed record DocumentDownload(Stream Stream, string OriginalFileName, string? ContentType);

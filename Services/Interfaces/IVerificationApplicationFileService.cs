using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IVerificationApplicationFileService
{
    Task<IReadOnlyList<VerificationApplicationFileDto>> GetAsync(
        Guid applicationId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<VerificationApplicationFileDto> UploadAsync(
        Guid applicationId, IFormFile file, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(
        Guid applicationId, Guid fileId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<DocumentDownload?> OpenDownloadAsync(
        Guid fileId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

using System.Security.Claims;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IVerificationApplicationService
{
    Task<VerificationApplicationDto> CreateDraftAsync(
        ClaimsPrincipal user,
        CreateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> UpdateDraftAsync(Guid id, ClaimsPrincipal user, UpdateVerificationApplicationRequest request, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> SubmitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> ReturnAsync(Guid id, ClaimsPrincipal user, string? note, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> RejectAsync(Guid id, ClaimsPrincipal user, string? note, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> AcceptAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto?> GetForApplicantAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto?> GetForLeaderReviewAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ListResponseDto<VerificationApplicationDto>> ListForApplicantAsync(ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ListResponseDto<VerificationApplicationDto>> ListForLeaderReviewAsync(ClaimsPrincipal user, int page, int pageSize, CancellationToken cancellationToken = default);
}

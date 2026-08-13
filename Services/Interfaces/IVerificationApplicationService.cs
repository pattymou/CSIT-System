using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IVerificationApplicationService
{
    Task<VerificationApplicationDto> CreateDraftAsync(
        ApplicantSnapshot applicant,
        VerificationApplicationTarget target,
        CreateVerificationApplicationRequest request,
        CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> UpdateDraftAsync(Guid id, UpdateVerificationApplicationRequest request, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> SubmitAsync(Guid id, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> ReturnAsync(Guid id, string processedBy, string? note, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> RejectAsync(Guid id, string processedBy, string? note, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto> AcceptAsync(Guid id, string processedBy, CancellationToken cancellationToken = default);
    Task<VerificationApplicationDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ListResponseDto<VerificationApplicationDto>> ListAsync(VerificationApplicationStatus? status, string? applicantAccount, int page, int pageSize, CancellationToken cancellationToken = default);
}

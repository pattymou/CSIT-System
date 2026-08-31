using System.Security.Claims;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IReservationService
{
    Task<IReadOnlyList<ReservationEnvironmentOptionDto>> GetEnvironmentOptionsAsync(CancellationToken cancellationToken = default);
    Task<ReservationApplicationOptionsDto> GetApplicationOptionsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> CreateAsync(ClaimsPrincipal user, CreateReservationRequest request, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> UpdateAsync(Guid id, ClaimsPrincipal user, UpdateReservationRequest request, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto?> GetByIdAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReservationListDto>> GetListAsync(
        ClaimsPrincipal user,
        ReservationStatus? status = null,
        bool active = false,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReservationListDto>> GetStaffListAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationOverviewPageDto> GetOverviewAsync(ReservationOverviewQuery query, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> SubmitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> ApproveAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> RejectAsync(Guid id, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> CancelAsync(Guid id, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> CheckoutAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationDetailDto> ReturnAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationExtensionRequestDto> RequestExtensionAsync(Guid id, ClaimsPrincipal user, ReservationExtensionCreateRequest request, CancellationToken cancellationToken = default);
    Task<ReservationExtensionRequestDto> ApproveExtensionAsync(Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationExtensionRequestDto> RejectExtensionAsync(Guid extensionId, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default);
    Task<ReservationExtensionRequestDto> CancelExtensionAsync(Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReservationExtensionRequestDto>> GetPendingExtensionsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationOverdueResponseDto> GetOverdueAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    Task<ReservationPolicySettings> GetPolicySettingsAsync(CancellationToken cancellationToken = default);
}

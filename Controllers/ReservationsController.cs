using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/reservations")]
[Authorize]
public class ReservationsController : ControllerBase
{
    private readonly IReservationService _reservationService;
    private readonly IApparatusService _apparatusService;

    public ReservationsController(IReservationService reservationService, IApparatusService apparatusService)
    {
        _reservationService = reservationService;
        _apparatusService = apparatusService;
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateReservationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _reservationService.UpdateAsync(id, User, request, cancellationToken));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ToErrorResult(ex);
        }
    }

    [HttpGet("apparatus")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> GetBookableApparatus(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        var items = await _apparatusService.GetListAsync(
            ApparatusReservationRules.EquipmentModuleCode,
            keyword,
            kind: null);
        return Ok(items.Where(x => ApparatusReservationRules.IsBookable(x.ModuleCode, x.ReservationStatus)));
    }

    [HttpGet("environment-options")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> GetEnvironmentOptions(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetEnvironmentOptionsAsync(cancellationToken));

    [HttpGet("application-options")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> GetApplicationOptions(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetApplicationOptionsAsync(User, cancellationToken));

    [HttpPost]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> Create(
        [FromBody] CreateReservationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reservationService.CreateAsync(User, request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ToErrorResult(ex);
        }
    }

    [HttpGet]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _reservationService.GetListAsync(User, cancellationToken));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ToErrorResult(ex);
        }
    }

    [HttpGet("review")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public async Task<IActionResult> StaffList(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetStaffListAsync(User, cancellationToken));

    [HttpGet("overview")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> Overview([FromQuery] ReservationOverviewQuery query, CancellationToken cancellationToken)
    {
        try { return Ok(await _reservationService.GetOverviewAsync(query, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToErrorResult(ex); }
    }

    [HttpGet("policy-settings")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> PolicySettings(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetPolicySettingsAsync(cancellationToken));

    [HttpGet("extensions/pending")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public async Task<IActionResult> PendingExtensions(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetPendingExtensionsAsync(User, cancellationToken));

    [HttpGet("overdue")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public async Task<IActionResult> Overdue(CancellationToken cancellationToken) =>
        Ok(await _reservationService.GetOverdueAsync(User, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reservationService.GetByIdAsync(id, User, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ToErrorResult(ex);
        }
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public Task<IActionResult> Submit(Guid id, CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.SubmitAsync(id, User, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.ApproveAsync(id, User, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public Task<IActionResult> Reject(
        Guid id,
        [FromBody] ReservationTransitionRequest request,
        CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.RejectAsync(id, User, request.Reason, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public Task<IActionResult> Cancel(
        Guid id,
        [FromBody] ReservationTransitionRequest request,
        CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.CancelAsync(id, User, request.Reason, cancellationToken));

    [HttpPost("{id:guid}/checkout")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public Task<IActionResult> Checkout(Guid id, CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.CheckoutAsync(id, User, cancellationToken));

    [HttpPost("{id:guid}/return")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public Task<IActionResult> Return(Guid id, CancellationToken cancellationToken) =>
        RunTransitionAsync(() => _reservationService.ReturnAsync(id, User, cancellationToken));

    [HttpPost("{id:guid}/extensions")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> RequestExtension(
        Guid id, [FromBody] ReservationExtensionCreateRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _reservationService.RequestExtensionAsync(id, User, request, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToErrorResult(ex); }
    }

    [HttpPost("extensions/{extensionId:guid}/approve")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public async Task<IActionResult> ApproveExtension(Guid extensionId, CancellationToken cancellationToken)
    {
        try { return Ok(await _reservationService.ApproveExtensionAsync(extensionId, User, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToErrorResult(ex); }
    }

    [HttpPost("extensions/{extensionId:guid}/reject")]
    [Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
    public async Task<IActionResult> RejectExtension(
        Guid extensionId, [FromBody] ReservationExtensionReviewRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _reservationService.RejectExtensionAsync(extensionId, User, request.Reason, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToErrorResult(ex); }
    }

    [HttpPost("extensions/{extensionId:guid}/cancel")]
    [Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
    public async Task<IActionResult> CancelExtension(Guid extensionId, CancellationToken cancellationToken)
    {
        try { return Ok(await _reservationService.CancelExtensionAsync(extensionId, User, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToErrorResult(ex); }
    }

    private async Task<IActionResult> RunTransitionAsync(Func<Task<ReservationDetailDto>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ToErrorResult(ex);
        }
    }

    private IActionResult ToErrorResult(Exception exception) => exception switch
    {
        KeyNotFoundException => NotFound(new { error = exception.Message }),
        UnauthorizedAccessException => StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message }),
        InvalidOperationException => Conflict(new { error = exception.Message }),
        ArgumentException => BadRequest(new { error = exception.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static bool IsExpected(Exception exception) =>
        exception is KeyNotFoundException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException;
}

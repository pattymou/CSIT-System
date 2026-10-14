using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.Administration)]
[Route("api/files/{fileId:guid}/km-sync")]
public sealed class KmManualRetryController(KmManualRetryService retryService) : ControllerBase
{
    public sealed record RetryRequest(string Reason);

    [HttpPost("retry")]
    [BusinessWrite]
    public async Task<IActionResult> Retry(Guid fileId, [FromBody] RetryRequest request,
        CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue("account") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        try
        {
            await retryService.RetryKmSyncAsync(fileId, actor, request?.Reason ?? string.Empty,
                cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KmManualRetryConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (KmManualRetryDocumentException ex) { return UnprocessableEntity(new { error = ex.Message }); }
    }
}

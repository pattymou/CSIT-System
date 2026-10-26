using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.DeletionStatusWritePolicy)]
[Route("api/integrations/km/v1/document-deletion-status")]
public sealed class KmDeletionStatusController(KmDeletionCallbackService callbacks) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] KmDeletionCallback callback,
        CancellationToken cancellationToken) =>
        await callbacks.ApplyAsync(callback, cancellationToken) switch
        {
            KmDeletionCallbackOutcome.Accepted => NoContent(),
            KmDeletionCallbackOutcome.Invalid => BadRequest(new { error = "invalid_callback" }),
            KmDeletionCallbackOutcome.NotFound => NotFound(new { error = "unknown_deletion_request" }),
            _ => Conflict(new { error = "callback_mismatch" })
        };
}

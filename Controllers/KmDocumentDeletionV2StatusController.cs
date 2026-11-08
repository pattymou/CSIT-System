using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.DeletionStatusWritePolicy)]
[Route("api/integrations/km/v2/document-deletions/status")]
public sealed class KmDocumentDeletionV2StatusController(
    KmDocumentDeletionV2CallbackService callbacks) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] KmDocumentDeletionV2Callback callback,
        CancellationToken cancellationToken) =>
        await callbacks.ApplyAsync(callback, cancellationToken) switch
        {
            KmDeletionCallbackOutcome.Accepted => NoContent(),
            KmDeletionCallbackOutcome.Invalid => BadRequest(new { error = "invalid_callback" }),
            KmDeletionCallbackOutcome.NotFound => NotFound(new { error = "unknown_deletion_request" }),
            _ => Conflict(new { error = "callback_mismatch" })
        };
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/verification-work/{recordId:guid}/deletion-preview")]
public sealed class VerificationCaseDeletionController(
    IVerificationCaseDeletionPreviewService previews) : ControllerBase
{
    [HttpGet]
    [BusinessWrite]
    public async Task<ActionResult<VerificationCaseDeletionPreviewDto>> Get(
        Guid recordId, CancellationToken cancellationToken)
    {
        try { return Ok(await previews.GetAsync(recordId, User, cancellationToken)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}

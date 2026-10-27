using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/verification-work/{recordId:guid}/deletion-preview")]
public sealed class VerificationCaseDeletionController(
    IVerificationCaseDeletionPreviewService previews,
    VerificationCaseDeletionConfirmationService confirmations,
    VerificationCaseLocalCleanupService cleanup) : ControllerBase
{
    public sealed record ConfirmationRequest(string ApplicationNo, string Reason);

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

    [HttpPost("confirm")]
    [BusinessWrite]
    public async Task<IActionResult> Confirm(Guid recordId, ConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await confirmations.ConfirmAsync(recordId, request.ApplicationNo,
                request.Reason, User, cancellationToken);
            return Ok(new { job.Id, job.Status });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("cleanup")]
    [BusinessWrite]
    public async Task<IActionResult> Clean(Guid recordId, CancellationToken cancellationToken)
    {
        try
        {
            await cleanup.BeginAsync(recordId, User, cancellationToken);
            var job = await cleanup.ProcessAsync(recordId, User, cancellationToken);
            return Ok(new { job.Id, job.Status, job.DbCleanedAt, job.CompletedAt });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        catch (IOException) { return StatusCode(503, "File cleanup failed; retry is safe."); }
    }

    [HttpPost("cleanup/retry")]
    [BusinessWrite]
    public async Task<IActionResult> Retry(Guid recordId, CancellationToken cancellationToken)
    {
        try
        {
            var job = await cleanup.ProcessAsync(recordId, User, cancellationToken);
            return Ok(new { job.Id, job.Status, job.DbCleanedAt, job.CompletedAt });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        catch (IOException) { return StatusCode(503, "File cleanup failed; retry is safe."); }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/verification-work")]
public sealed class VerificationWorkController(IVerificationWorkManagementService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VerificationWorkListItemDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpGet("{recordId:guid}")]
    public async Task<ActionResult<VerificationWorkDetailDto>> Get(Guid recordId, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(recordId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("{recordId:guid}/status")]
    [BusinessWrite]
    public async Task<IActionResult> UpdateStatus(
        Guid recordId, UpdateVerificationWorkStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await service.UpdateRecordStatusAsync(recordId, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPut("tasks/{taskId:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> UpdateTask(
        Guid taskId, UpdateVerificationWorkTaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await service.UpdateTaskAsync(taskId, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}

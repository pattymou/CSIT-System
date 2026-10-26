using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/verification-work")]
public sealed class VerificationWorkController(
    IVerificationWorkManagementService service,
    IVerificationApplicationService applications) : ControllerBase
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

    [HttpGet("{recordId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<VerificationWorkTaskDetailDto>> GetTask(
        Guid recordId, Guid taskId, CancellationToken cancellationToken)
    {
        var result = await service.GetTaskAsync(recordId, taskId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("{recordId:guid}/status")]
    [BusinessWrite]
    public async Task<IActionResult> UpdateStatus(
        Guid recordId, UpdateVerificationWorkStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await service.UpdateRecordStatusAsync(recordId, request, User, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("{recordId:guid}/selected/{itemCode}/enable")]
    [BusinessWrite]
    public async Task<ActionResult<EnableSelectedWorkResponse>> EnableSelectedWork(
        Guid recordId, string itemCode, CancellationToken cancellationToken)
    {
        try
        {
            var detail = await service.GetAsync(recordId, cancellationToken);
            if (detail is null) return NotFound();
            var selected = detail.SelectedTestItems.SingleOrDefault(x =>
                string.Equals(x.Code, itemCode, StringComparison.OrdinalIgnoreCase));
            if (selected is null) return NotFound();
            if (selected.TaskId is Guid existingTaskId)
                return Ok(new EnableSelectedWorkResponse(existingTaskId));
            if (!await applications.CanManageAcceptedAsync(detail.ApplicationId, User, cancellationToken))
                return Forbid();
            await applications.EnableSelectedWorkAsync(detail.ApplicationId, User, cancellationToken);
            var updated = await service.GetAsync(recordId, cancellationToken);
            var taskId = updated?.SelectedTestItems.SingleOrDefault(x =>
                string.Equals(x.Code, itemCode, StringComparison.OrdinalIgnoreCase))?.TaskId;
            return taskId is Guid id
                ? Ok(new EnableSelectedWorkResponse(id))
                : Conflict("The selected item could not be linked to a unique verification task.");
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
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

    [HttpPut("{recordId:guid}/tasks/{taskId:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> UpdateTaskForRecord(
        Guid recordId, Guid taskId, UpdateVerificationWorkTaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await service.UpdateTaskForRecordAsync(recordId, taskId, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}

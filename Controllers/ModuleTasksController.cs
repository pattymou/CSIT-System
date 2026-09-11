using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api")]
public class ModuleTasksController : ControllerBase
{
    private readonly IModuleTaskService _service;

    public ModuleTasksController(IModuleTaskService service)
    {
        _service = service;
    }

    [HttpGet("cases/{caseId:guid}/tasks")]
    public async Task<ActionResult<ListResponseDto<ModuleTaskListItemDto>>> GetList(
        Guid caseId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null)
    {
        var result = await _service.GetListAsync(caseId, page, pageSize, status);
        return Ok(result);
    }

    [HttpGet("tasks/{id:guid}")]
    public async Task<ActionResult<ModuleTaskDetailDto>> GetById(Guid id)
    {
        var result = await _service.GetByIdAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("cases/{caseId:guid}/tasks")]
    public async Task<ActionResult> Create(Guid caseId, [FromBody] ModuleTaskUpsertRequest request)
    {
        var id = await _service.CreateAsync(caseId, request);
        return Ok(new { id });
    }

    [HttpPut("tasks/{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] ModuleTaskUpsertRequest request)
    {
        var ok = await _service.UpdateAsync(id, request);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpDelete("tasks/{id:guid}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpGet("/api/cases/{caseId:guid}/tasks/new-id")]
    public IActionResult GetNewTaskNo(Guid caseId)
    {
        var taskNo = $"TSK-{DateTime.Now:yyyyMMddHHmmss}";
        Console.WriteLine($"[ModuleTasksController] New TaskNo generated. caseId={caseId}, taskNo={taskNo}");

        return Ok(new
        {
            TaskNo = taskNo
        });
    }
}

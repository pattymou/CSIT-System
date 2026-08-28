using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api")]
public class ModuleRecordsController : ControllerBase
{
    private readonly IModuleRecordService _service;

    public ModuleRecordsController(IModuleRecordService service)
    {
        _service = service;
    }

    [HttpGet("modules/{moduleCode}/records")]
    public async Task<ActionResult<ListResponseDto<ModuleRecordListItemDto>>> GetList(
        string moduleCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null)
    {
        var result = await _service.GetListAsync(moduleCode, page, pageSize, status);
        return Ok(result);
    }

    [HttpGet("records/{id:guid}")]
    public async Task<ActionResult<ModuleRecordDetailDto>> GetById(Guid id)
    {
        var result = await _service.GetByIdAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("modules/{moduleCode}/records")]
    public async Task<ActionResult> Create(string moduleCode, [FromBody] ModuleRecordUpsertRequest request)
    {
        var id = await _service.CreateAsync(moduleCode, request);
        return Ok(new { id });
    }

    [HttpPut("records/{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] ModuleRecordUpsertRequest request)
    {
        var ok = await _service.UpdateAsync(id, request);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpDelete("records/{id:guid}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound();
        return NoContent();
    }
}

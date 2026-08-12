using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api")]
public class ModuleCasesController : ControllerBase
{
    private readonly IModuleCaseService _service;

    public ModuleCasesController(IModuleCaseService service)
    {
        _service = service;
    }

    [HttpGet("records/{recordId:guid}/cases")]
    public async Task<ActionResult<ListResponseDto<ModuleCaseListItemDto>>> GetList(
        Guid recordId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null)
    {
        var result = await _service.GetListAsync(recordId, page, pageSize, status);
        return Ok(result);
    }

    [HttpGet("records/{recordId:guid}/cases/new-id")]
    public async Task<ActionResult<NewCaseNoResponse>> GetNewCaseNo(Guid recordId)
    {
        var result = await _service.GetNewCaseNoAsync(recordId);
        return Ok(result);
    }

    [HttpGet("cases/{id:guid}")]
    public async Task<ActionResult<ModuleCaseDetailDto>> GetById(Guid id)
    {
        var result = await _service.GetByIdAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("records/{recordId:guid}/cases")]
    public async Task<ActionResult> Create(Guid recordId, [FromBody] ModuleCaseUpsertRequest request)
    {
        var id = await _service.CreateAsync(recordId, request);
        return Ok(new { id });
    }

    [HttpPut("cases/{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] ModuleCaseUpsertRequest request)
    {
        var ok = await _service.UpdateAsync(id, request);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpDelete("cases/{id:guid}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound();
        return NoContent();
    }
}

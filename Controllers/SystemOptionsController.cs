using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/system-options")]
public class SystemOptionsController : ControllerBase
{
    private readonly ISystemOptionService _service;

    public SystemOptionsController(ISystemOptionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? category)
    {
        try
        {
            var result = await _service.GetAllAsync(category);
            return Ok(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemOptionsController] GetAll failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("category/{category}")]
    public async Task<IActionResult> GetByCategory(string category)
    {
        try
        {
            var result = await _service.GetEnabledByCategoryAsync(category);
            return Ok(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemOptionsController] GetByCategory failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("team-routings")]
    public async Task<ActionResult<List<TeamRoutingDto>>> GetTeamRoutings(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetTeamRoutingsAsync(cancellationToken));
    }

    [HttpPost("team-routings")]
    public async Task<IActionResult> CreateTeamRouting(
        [FromBody] TeamRoutingUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _service.CreateTeamRoutingAsync(request, cancellationToken);
            return Ok(new { id });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("team-routings/{id:guid}")]
    public async Task<IActionResult> UpdateTeamRouting(
        Guid id,
        [FromBody] TeamRoutingUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _service.UpdateTeamRoutingAsync(id, request, cancellationToken)
                ? NoContent()
                : NotFound("找不到 Team Leader 設定");
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SystemOptionUpsertRequest request)
    {
        try
        {
            var id = await _service.CreateAsync(request);
            return Ok(new { id });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemOptionsController] Create failed: {ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SystemOptionUpsertRequest request)
    {
        try
        {
            var ok = await _service.UpdateAsync(id, request);
            return ok ? NoContent() : NotFound("找不到參數");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemOptionsController] Update failed: {ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var ok = await _service.DeleteAsync(id);
            return ok ? NoContent() : NotFound("找不到參數");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemOptionsController] Delete failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }
}

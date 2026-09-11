using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/modules")]
public class ModulesController : ControllerBase
{
    private readonly IModuleService _service;

    public ModulesController(IModuleService service)
    {
        _service = service;
    }

    [HttpGet("enabled")]
    public async Task<ActionResult<List<ModuleListItemDto>>> GetEnabled()
    {
        var result = await _service.GetEnabledModulesAsync();
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<List<ModuleListItemDto>>> GetAll()
    {
        var result = await _service.GetAllAsync();
        return Ok(result);
    }

    [HttpGet("{code}")]
    public async Task<ActionResult<ModuleDetailDto>> GetByCode(string code)
    {
        var result = await _service.GetByCodeAsync(code);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] ModuleUpsertRequest request)
    {
        var id = await _service.CreateAsync(request);
        return Ok(new { id });
    }
}

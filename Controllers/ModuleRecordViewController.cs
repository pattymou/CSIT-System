using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/module-record-view")]
public class ModuleRecordViewController : ControllerBase
{
    private readonly IModuleRecordService _service;

    public ModuleRecordViewController(IModuleRecordService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponseDto<ModuleRecordListItemDto>>> Get(
        [FromQuery] string moduleCode,
        [FromQuery] string status = "Open",
        [FromQuery] string site = "TP",
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var result = await _service.GetProjectViewListAsync(moduleCode, status, site, search, page, pageSize);
        return Ok(result);
    }
}

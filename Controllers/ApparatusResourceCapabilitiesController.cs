using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/apparatus/{apparatusId}/resource-capabilities")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class ApparatusResourceCapabilitiesController : ControllerBase
{
    private readonly IApparatusResourceCapabilityService _service;

    public ApparatusResourceCapabilitiesController(IApparatusResourceCapabilityService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Get(string apparatusId, CancellationToken cancellationToken)
    {
        try { return Ok(await _service.GetByApparatusAsync(apparatusId, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    [HttpPut]
    public async Task<IActionResult> Replace(
        string apparatusId,
        [FromBody] ReplaceApparatusResourceCapabilitiesRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _service.ReplaceAsync(apparatusId, request.Mappings, cancellationToken)); }
        catch (Exception ex) when (IsExpected(ex)) { return ToError(ex); }
    }

    private IActionResult ToError(Exception ex) => ex switch
    {
        KeyNotFoundException => NotFound(new { error = ex.Message }),
        InvalidOperationException => Conflict(new { error = ex.Message }),
        ArgumentException => BadRequest(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static bool IsExpected(Exception ex) => ex is KeyNotFoundException or InvalidOperationException or ArgumentException;
}

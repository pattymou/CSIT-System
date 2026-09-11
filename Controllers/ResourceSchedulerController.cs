using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/resource-scheduler")]
[Authorize]
[Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
public sealed class ResourceSchedulerController : ControllerBase
{
    private readonly IResourceSchedulerService _service;

    public ResourceSchedulerController(IResourceSchedulerService service)
    {
        _service = service;
    }

    [HttpPost("propose")]
    public async Task<IActionResult> Propose(
        [FromBody] ResourceSchedulerProposalRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _service.ProposeAsync(User, request, cancellationToken)); }
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

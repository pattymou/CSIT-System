using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/directory")]
public sealed class DirectoryController(LabOwnerDirectory labOwnerDirectory) : ControllerBase
{
    [HttpGet("lab-owners")]
    public async Task<ActionResult<IReadOnlyList<LabOwnerOptionDto>>> GetLabOwners(
        [FromQuery] string? location,
        CancellationToken cancellationToken)
    {
        return Ok(await labOwnerDirectory.GetForLocationAsync(location, cancellationToken));
    }
}

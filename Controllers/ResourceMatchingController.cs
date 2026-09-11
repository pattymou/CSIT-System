using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/resource-matching")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class ResourceMatchingController : ControllerBase
{
    private readonly IApparatusResourceCapabilityService _service;

    public ResourceMatchingController(IApparatusResourceCapabilityService service)
    {
        _service = service;
    }

    [HttpGet("catalog-values")]
    public async Task<IActionResult> GetCatalogValues(CancellationToken cancellationToken) =>
        Ok(await _service.GetCatalogValuesAsync(cancellationToken));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.AdminOnly)]
[Route("api/administration/km-metadata/results")]
public sealed class KmMetadataAdministrationController(KmAnalysisQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? sourceType,
        [FromQuery] string? fileName, [FromQuery] string? kmJobId,
        [FromQuery] bool? hasWarnings, [FromQuery] bool? missingSummary,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await service.ListAdminAsync(User, sourceType, fileName, kmJobId,
                hasWarnings, missingSummary, page, pageSize, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{resultId:guid}")]
    public async Task<IActionResult> Get(Guid resultId, CancellationToken cancellationToken)
    {
        var result = await service.GetAdminAsync(User, resultId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}

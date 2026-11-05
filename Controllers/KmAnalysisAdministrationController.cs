using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api/administration/km-analysis/results")]
public sealed class KmAnalysisAdministrationController(KmAnalysisQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? sourceType,
        [FromQuery] Guid? sourceEntityId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        try { return Ok(await service.ListAsync(User, sourceType, sourceEntityId, page, pageSize, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{resultId:guid}")]
    public async Task<IActionResult> Get(Guid resultId, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(User, resultId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{resultId:guid}/reviews")]
    [BusinessWrite]
    public async Task<IActionResult> Review(Guid resultId, [FromBody] KmAnalysisReviewRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(await service.ReviewAsync(User, resultId, request, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}

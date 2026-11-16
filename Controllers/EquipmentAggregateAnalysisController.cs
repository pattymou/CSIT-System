using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
[Route("api/equipment-aggregate-analysis")]
public sealed class EquipmentAggregateAnalysisController(EquipmentAggregateAnalysisService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string apparatusId,
        [FromQuery] string moduleCode = "equipment", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apparatusId) || string.IsNullOrWhiteSpace(moduleCode)) return BadRequest();
        var result = await service.GetAsync(apparatusId.Trim(), moduleCode.Trim().ToLowerInvariant(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{snapshotId:guid}/reviews")]
    [Authorize(Policy = SystemAuthorization.Policies.AdminOnly)]
    [BusinessWrite]
    public async Task<IActionResult> Review(Guid snapshotId,
        [FromBody] EquipmentAggregateAnalysisReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await service.ReviewAsync(snapshotId, User, request, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

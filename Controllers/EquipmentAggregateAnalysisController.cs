using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
[Route("api/equipment-aggregate-analysis")]
public sealed class EquipmentAggregateAnalysisController(
    EquipmentAggregateAnalysisService service, AppDbContext db,
    ILogger<EquipmentAggregateAnalysisController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string apparatusId,
        [FromQuery] string moduleCode = "equipment", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apparatusId) || string.IsNullOrWhiteSpace(moduleCode)) return BadRequest();
        var id = apparatusId.Trim();
        var module = moduleCode.Trim().ToLowerInvariant();
        var result = await service.GetAsync(id, module, cancellationToken);
        if (result is not null) return Ok(result);
        var idExists = await db.Apparatuses.AsNoTracking().AnyAsync(x => x.Id == id, cancellationToken);
        logger.LogWarning(
            "Equipment aggregate analysis apparatus lookup missed. TraceId={TraceId} ApparatusId={ApparatusId} ModuleCode={ModuleCode} IdExists={IdExists}",
            Activity.Current?.Id ?? HttpContext.TraceIdentifier, SafeDiagnosticValue(id),
            SafeDiagnosticValue(module), idExists);
        return NotFound();
    }

    private static string SafeDiagnosticValue(string value) =>
        value.Length <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? value : "Invalid";

    [HttpPost("reanalyze")]
    [Authorize(Policy = SystemAuthorization.Policies.AdminOnly)]
    [BusinessWrite]
    public async Task<IActionResult> Reanalyze(
        [FromBody] EquipmentAggregateReanalysisRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApparatusId) ||
            string.IsNullOrWhiteSpace(request.ModuleCode)) return BadRequest();
        var actor = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        try
        {
            var id = await service.RequestReanalysisAsync(request.ApparatusId.Trim(),
                request.ModuleCode.Trim().ToLowerInvariant(), request.RequestId,
                request.ExpectedManifestHash, actor, cancellationToken);
            return Accepted(new { requestId = id });
        }
        catch (EquipmentAggregateAnalysisException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Error });
        }
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

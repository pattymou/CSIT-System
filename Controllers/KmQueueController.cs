using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/administration/km-queue")]
[Authorize(Policy = SystemAuthorization.Policies.KmQueueRead)]
public sealed class KmQueueController(KmQueueQueryService queues,
    KmManualReanalysisService reanalysis) : ControllerBase
{
    public sealed record ReanalysisRequest(Guid RequestId);

    [HttpPost("ingestion/{id:guid}/reanalyze")]
    [Authorize(Policy = SystemAuthorization.Policies.AdminOnly)]
    [BusinessWrite]
    public async Task<IActionResult> Reanalyze(Guid id, [FromBody] ReanalysisRequest request,
        CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue("account") ??
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        try
        {
            var attemptId = await reanalysis.CreateAsync(id, request?.RequestId ?? Guid.Empty,
                actor, cancellationToken);
            return Accepted(new { attemptId });
        }
        catch (KmReanalysisRejectedException ex) { return Conflict(new { error = ex.Message }); }
        catch (DbUpdateException) { return Conflict(new { error = "另一個重新分析請求已建立，請更新佇列。" }); }
    }
    [HttpGet("ingestion")]
    public async Task<ActionResult<KmQueuePageDto>> Ingestion([FromQuery] string filter = KmQueueFilters.Unfinished,
        [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        await ReadAsync(() => queues.GetIngestionAsync(filter, page, cancellationToken), cancellationToken);

    [HttpGet("deletion")]
    public async Task<ActionResult<KmQueuePageDto>> Deletion([FromQuery] string filter = KmQueueFilters.Unfinished,
        [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        await ReadAsync(() => queues.GetDeletionAsync(filter, page, cancellationToken), cancellationToken);

    private async Task<ActionResult<KmQueuePageDto>> ReadAsync(
        Func<Task<KmQueuePageDto>> read, CancellationToken cancellationToken)
    {
        try { return Ok(await read()); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            HttpContext.Items[KmQueueRequestDiagnostics.ErrorTypeItem] =
                $"{ex.GetType().Name}/{ex.GetBaseException().GetType().Name}";
            var postgres = ex.GetBaseException() as Npgsql.PostgresException;
            if (postgres is not null)
                HttpContext.Items[KmQueueRequestDiagnostics.SqlStateItem] = postgres.SqlState;
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "km_queue_unavailable" });
        }
    }
}

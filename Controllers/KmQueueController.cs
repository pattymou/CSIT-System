using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/administration/km-queue")]
[Authorize(Policy = SystemAuthorization.Policies.KmQueueRead)]
public sealed class KmQueueController(KmQueueQueryService queues) : ControllerBase
{
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

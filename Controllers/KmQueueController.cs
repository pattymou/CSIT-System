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
        Ok(await queues.GetIngestionAsync(filter, page, cancellationToken));

    [HttpGet("deletion")]
    public async Task<ActionResult<KmQueuePageDto>> Deletion([FromQuery] string filter = KmQueueFilters.Unfinished,
        [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        Ok(await queues.GetDeletionAsync(filter, page, cancellationToken));
}

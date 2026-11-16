using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services.KmIntegration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.AnalysisStatusWritePolicy)]
[Route("api/integrations/km/v2")]
public sealed class KmAnalysisCallbackController(
    KmAnalysisCallbackService callbacks,
    KmMetadataChangeCallbackService metadataChanges,
    EquipmentAggregateAnalysisService equipmentAggregate) : ControllerBase
{
    [HttpPost("document-jobs/status")]
    public Task<IActionResult> DocumentStatus([FromBody] KmV2DocumentJobStatusCallback callback,
        CancellationToken cancellationToken) => ApplyAsync(
        () => callbacks.ApplyDocumentStatusAsync(callback, cancellationToken));

    [HttpPost("analysis-results")]
    public Task<IActionResult> AnalysisResult([FromBody] KmV2AnalysisResultCallback callback,
        CancellationToken cancellationToken) => ApplyAsync(
        () => callbacks.ApplyTestReportResultAsync(callback, cancellationToken));

    [HttpPost("document-metadata-changes/status")]
    public Task<IActionResult> MetadataChangeStatus(
        [FromBody] KmMetadataChangeStatusCallback callback,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var key) ||
            key.Count != 1 || !Guid.TryParse(key[0], out var id) || id != callback.CallbackId)
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_idempotency_key" }));
        return ApplyAsync(() => metadataChanges.ApplyAsync(callback, cancellationToken));
    }

    [HttpPost("equipment-aggregate-analyses/status")]
    public async Task<IActionResult> EquipmentAggregateStatus(
        [FromBody] EquipmentAggregateAnalysisCallback callback,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var key) || key.Count != 1 ||
            !Guid.TryParse(key[0], out var id) || id != callback.CallbackId)
            return BadRequest(new { error = "invalid_idempotency_key" });
        var payload = JsonSerializer.Serialize(callback, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        try
        {
            await equipmentAggregate.ApplyCallbackAsync(callback, hash, cancellationToken);
            return NoContent();
        }
        catch (EquipmentAggregateAnalysisException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Error });
        }
    }

    private static async Task<IActionResult> ApplyAsync(Func<Task> action)
    {
        try { await action(); return new NoContentResult(); }
        catch (KmAnalysisCallbackException ex)
        {
            return new ObjectResult(new { error = ex.Error }) { StatusCode = ex.StatusCode };
        }
    }
}

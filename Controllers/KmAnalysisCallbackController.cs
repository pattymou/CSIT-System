using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = KmIntegrationAuthorization.AnalysisStatusWritePolicy)]
[Route("api/integrations/km/v2")]
public sealed class KmAnalysisCallbackController(KmAnalysisCallbackService callbacks) : ControllerBase
{
    [HttpPost("document-jobs/status")]
    public Task<IActionResult> DocumentStatus([FromBody] KmV2DocumentJobStatusCallback callback,
        CancellationToken cancellationToken) => ApplyAsync(
        () => callbacks.ApplyDocumentStatusAsync(callback, cancellationToken));

    [HttpPost("analysis-results")]
    public Task<IActionResult> AnalysisResult([FromBody] KmV2AnalysisResultCallback callback,
        CancellationToken cancellationToken) => ApplyAsync(
        () => callbacks.ApplyTestReportResultAsync(callback, cancellationToken));

    private static async Task<IActionResult> ApplyAsync(Func<Task> action)
    {
        try { await action(); return new NoContentResult(); }
        catch (KmAnalysisCallbackException ex)
        {
            return new ObjectResult(new { error = ex.Error }) { StatusCode = ex.StatusCode };
        }
    }
}

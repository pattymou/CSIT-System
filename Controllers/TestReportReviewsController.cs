using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.Administration)]
[Route("api/test-reports")]
public sealed class TestReportReviewsController(ICaseFileService fileService) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<List<PendingTestReportDto>>> GetPending() =>
        Ok(await fileService.GetPendingTestReportsAsync());

    [HttpPost("{fileId:guid}/review")]
    [BusinessWrite]
    public async Task<IActionResult> Review(Guid fileId, TestReportReviewRequest request)
    {
        try
        {
            var reviewer = User.FindFirstValue("account")
                ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.Identity?.Name
                ?? "System";
            await fileService.ReviewTestReportAsync(fileId, request, reviewer);
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/supervisor-submissions")]
public sealed class SupervisorApprovalFilesController(ApprovalSubmissionService submissions) : ControllerBase
{
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var file = await submissions.OpenFileAsync(id, User, cancellationToken);
            return File(file.Stream, file.ContentType, file.FileName);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (InvalidOperationException) { return NotFound(); }
    }
}

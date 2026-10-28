using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/vendors")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class VendorsController(
    IVendorService service,
    VendorApprovalUploadService approvalUploads) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<VendorListItemDto>>> Search([FromQuery] string? keyword,
        [FromQuery] bool includeDisabled = true, CancellationToken cancellationToken = default) =>
        Ok(await service.SearchAsync(keyword, includeDisabled, cancellationToken));

    [HttpGet("options")]
    public async Task<ActionResult<List<VendorOptionDto>>> Options([FromQuery] Guid? includeVendorId,
        CancellationToken cancellationToken) => Ok(await service.OptionsAsync(includeVendorId, cancellationToken));

    [HttpGet("manufacturer-match-report")]
    public async Task<ActionResult<List<ManufacturerMatchReportItemDto>>> ManufacturerMatchReport(
        CancellationToken cancellationToken) => Ok(await service.ManufacturerMatchReportAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VendorDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    [BusinessWrite]
    public async Task<IActionResult> Create(VendorUpsertRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(new { id = await service.CreateAsync(request, Actor(), cancellationToken) }); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPut("{id:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> Update(Guid id, VendorUpsertRequest request, CancellationToken cancellationToken)
    {
        try { return await service.UpdateAsync(id, request, Actor(), cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpDelete("{id:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try { return await service.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("{id:guid}/files")]
    [BusinessWrite]
    public async Task<IActionResult> Upload(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await approvalUploads.UploadAsync(id, Request.Form.Files.ToList(), User, cancellationToken);
            return result.Decision == ApprovalUploadDecisionKind.Pending
                ? Accepted(new VendorUploadResponseDto
                {
                    Status = "PendingApproval", OperationIds = result.OperationIds.ToList()
                })
                : Ok(new VendorUploadResponseDto
                {
                    Status = "Archived", OperationIds = result.OperationIds.ToList()
                });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (SharedIdentityProvisioningException ex) { return StatusCode(503, ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("files/{fileId:guid}/content")]
    public async Task<IActionResult> Download(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            var file = await service.DownloadAsync(fileId, cancellationToken);
            return File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: true);
        }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
    }

    [HttpDelete("files/{fileId:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> DeleteFile(Guid fileId, CancellationToken cancellationToken)
    {
        try { return await service.DeleteFileAsync(fileId, cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    private string Actor() => User.FindFirstValue("account") ?? User.Identity?.Name ?? "SYSTEM";
}

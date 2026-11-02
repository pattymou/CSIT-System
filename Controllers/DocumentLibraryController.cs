using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/document-library")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class DocumentLibraryController(
    IDocumentLibraryService service,
    DocumentLibraryApprovalUploadService approvalUploads,
    DocumentLibraryDeletionAuthorizationService deletionAuthorization,
    DocumentLibraryFileDeletionService deletionService) : ControllerBase
{
    [HttpGet("folders")]
    public async Task<ActionResult<List<DocumentFolderDto>>> GetFolders(CancellationToken cancellationToken) =>
        Ok(await service.GetFoldersAsync(cancellationToken));

    [HttpGet("system-documents")]
    public async Task<ActionResult<SystemDocumentCatalogDto>> GetSystemDocuments(CancellationToken cancellationToken) =>
        Ok(await service.GetSystemDocumentsAsync(cancellationToken));

    [HttpPost("folders")]
    [Authorize(Policy = SystemAuthorization.Policies.Administration)]
    [BusinessWrite]
    public async Task<IActionResult> CreateFolder(
        [FromBody] DocumentFolderUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(new { id = await service.CreateFolderAsync(request, cancellationToken) }); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPut("folders/{id:guid}")]
    [Authorize(Policy = SystemAuthorization.Policies.Administration)]
    [BusinessWrite]
    public async Task<IActionResult> UpdateFolder(
        Guid id,
        [FromBody] DocumentFolderUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try { return await service.UpdateFolderAsync(id, request, cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpDelete("folders/{id:guid}")]
    [Authorize(Policy = SystemAuthorization.Policies.Administration)]
    [BusinessWrite]
    public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken cancellationToken)
    {
        try { return await service.DeleteFolderAsync(id, cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("folders/{folderId:guid}/files")]
    public async Task<IActionResult> GetFiles(Guid folderId, CancellationToken cancellationToken)
    {
        try { return Ok(await service.GetFilesAsync(folderId, cancellationToken)); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("file-statuses")]
    public async Task<IActionResult> GetFileStatuses(
        [FromBody] DocumentArchiveStatusBatchRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(await service.GetArchiveStatusesAsync(request.FileIds, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("folders/{folderId:guid}/files")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> Upload(Guid folderId, CancellationToken cancellationToken)
    {
        if (Request.Form.Files.Count == 0) return BadRequest("請至少選擇一個檔案。");
        var result = new DocumentUploadBatchResultDto();
        foreach (var file in Request.Form.Files)
        {
            try
            {
                var outcome = await approvalUploads.UploadAsync(folderId, file, User, cancellationToken);
                result.Results.Add(new DocumentUploadResultDto
                {
                    OriginalFileName = Path.GetFileName(file.FileName),
                    Success = true,
                    Message = outcome.Message,
                    SubmissionId = outcome.SubmissionId,
                    ApprovalResult = outcome.Kind.ToString(),
                    File = outcome.File
                });
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException)
            {
                result.Results.Add(new DocumentUploadResultDto
                {
                    OriginalFileName = Path.GetFileName(file.FileName),
                    Success = false,
                    Message = ex.Message
                });
            }
        }
        return Ok(result);
    }

    [HttpPost("system-folders/product-information/files")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> UploadProductInformation(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        if (Request.Form.Files.Count == 0) return BadRequest("請至少選擇一個檔案。");
        Guid folderId;
        try { folderId = await service.GetOrCreateProductInformationFolderAsync(modelName, cancellationToken); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException)
        { return BadRequest(ex.Message); }
        var result = new DocumentUploadBatchResultDto();
        foreach (var file in Request.Form.Files)
        {
            try
            {
                var outcome = await approvalUploads.UploadAsync(folderId, file, User, cancellationToken);
                result.Results.Add(new DocumentUploadResultDto
                {
                    OriginalFileName = Path.GetFileName(file.FileName),
                    Success = true,
                    Message = outcome.Message,
                    SubmissionId = outcome.SubmissionId,
                    ApprovalResult = outcome.Kind.ToString(),
                    File = outcome.File
                });
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException)
            {
                result.Results.Add(new DocumentUploadResultDto
                {
                    OriginalFileName = Path.GetFileName(file.FileName),
                    Success = false,
                    Message = ex.Message
                });
            }
        }
        return Ok(result);
    }

    [HttpGet("files/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var download = await service.OpenDownloadAsync(id, cancellationToken);
            return download is null
                ? NotFound()
                : File(download.Stream, download.ContentType ?? "application/octet-stream", download.OriginalFileName);
        }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
    }

    [HttpGet("files/{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var preview = await service.OpenDownloadAsync(id, cancellationToken);
            if (preview is null) return NotFound();
            if (!DocumentLibraryRules.IsPreviewableImage(preview.ContentType, preview.OriginalFileName))
            {
                await preview.Stream.DisposeAsync();
                return StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }
            return File(preview.Stream, preview.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
        }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
    }

    [HttpPost("files/{id:guid}/deletion")]
    [Authorize(Policy = SystemAuthorization.Policies.Administration)]
    [BusinessWrite]
    public async Task<IActionResult> RequestDeletion(Guid id,
        CancellationToken cancellationToken)
    {
        if (!await deletionAuthorization.CanDeleteAsync(User, cancellationToken)) return Forbid();
        try
        {
            var actor = ApprovalIdentity.Account(User);
            var result = await deletionService.RequestAsync(id, actor, cancellationToken);
            if (result is null) return NotFound();
            return result.Completed
                ? Ok(result)
                : AcceptedAtAction(nameof(GetDeletion), new { requestId = result.RequestId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "deletion_blocked", message = ex.Message });
        }
    }

    [HttpGet("file-deletions/{requestId:guid}")]
    [Authorize(Policy = SystemAuthorization.Policies.Administration)]
    [BusinessWrite]
    public async Task<IActionResult> GetDeletion(Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!await deletionAuthorization.CanDeleteAsync(User, cancellationToken)) return Forbid();
        var result = await deletionService.GetAsync(requestId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}

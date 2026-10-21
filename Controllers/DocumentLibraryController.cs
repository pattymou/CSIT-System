using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/document-library")]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public sealed class DocumentLibraryController(IDocumentLibraryService service) : ControllerBase
{
    [HttpGet("folders")]
    public async Task<ActionResult<List<DocumentFolderDto>>> GetFolders(CancellationToken cancellationToken) =>
        Ok(await service.GetFoldersAsync(cancellationToken));

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

    [HttpGet("folders/{folderId:guid}/files")]
    public async Task<IActionResult> GetFiles(Guid folderId, CancellationToken cancellationToken)
    {
        try { return Ok(await service.GetFilesAsync(folderId, cancellationToken)); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("folders/{folderId:guid}/files")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> Upload(Guid folderId, CancellationToken cancellationToken)
    {
        if (Request.Form.Files.Count == 0) return BadRequest("請至少選擇一個檔案。");
        var account = User.FindFirstValue("account")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "System";
        var result = new DocumentUploadBatchResultDto();
        foreach (var file in Request.Form.Files)
        {
            try
            {
                var uploaded = await service.UploadAsync(folderId, file, account, cancellationToken);
                result.Results.Add(new DocumentUploadResultDto
                {
                    OriginalFileName = uploaded.OriginalFileName,
                    Success = true,
                    Message = "已接收／待 KM 處理",
                    File = uploaded
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
}

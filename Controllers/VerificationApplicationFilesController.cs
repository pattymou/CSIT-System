using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/verification-applications/{applicationId:guid}/files")]
[Authorize(Policy = SystemAuthorization.Policies.ReservationUser)]
public sealed class VerificationApplicationFilesController(
    IVerificationApplicationFileService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid applicationId, CancellationToken cancellationToken)
    {
        try { return Ok(await service.GetAsync(applicationId, User, cancellationToken)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> Upload(Guid applicationId, CancellationToken cancellationToken)
    {
        if (Request.Form.Files.Count == 0) return BadRequest("請至少選擇一個檔案。");
        var result = new VerificationApplicationFileUploadBatchResultDto();
        foreach (var file in Request.Form.Files)
        {
            try
            {
                var uploaded = await service.UploadAsync(applicationId, file, User, cancellationToken);
                result.Results.Add(new()
                {
                    FileName = uploaded.FileName,
                    Success = true,
                    Message = "附件已保存。",
                    File = uploaded
                });
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException)
            {
                result.Results.Add(new()
                {
                    FileName = Path.GetFileName(file.FileName),
                    Success = false,
                    Message = ex.Message
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }
        return Ok(result);
    }

    [HttpDelete("{fileId:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> Delete(
        Guid applicationId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.DeleteAsync(applicationId, fileId, User, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("~/api/verification-application-files/{fileId:guid}/download")]
    public async Task<IActionResult> Download(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            var download = await service.OpenDownloadAsync(fileId, User, cancellationToken);
            return download is null
                ? NotFound()
                : File(download.Stream, download.ContentType ?? "application/octet-stream", download.OriginalFileName);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (FileNotFoundException ex) { return NotFound(ex.Message); }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
public class CaseFilesController : ControllerBase
{
    private readonly ICaseFileService _service;
    private readonly IAuthorizationService _authorizationService;

    public CaseFilesController(ICaseFileService service, IAuthorizationService authorizationService)
    {
        _service = service;
        _authorizationService = authorizationService;
    }

    [HttpGet("api/cases/{caseId:guid}/files")]
    public async Task<ActionResult<List<ModuleCaseFileDto>>> GetFiles(Guid caseId)
    {
        var result = await _service.GetFilesAsync(caseId);
        return Ok(result);
    }

    [HttpGet("api/records/{recordId:guid}/cases/{caseNo}/attachments")]
    public async Task<ActionResult<List<ModuleCaseFileDto>>> GetFilesByCaseNo(Guid recordId, string caseNo)
    {
        var result = await _service.GetFilesByCaseNoAsync(recordId, Uri.UnescapeDataString(caseNo));
        return Ok(result);
    }

    //[HttpGet("api/tasks/{taskId:guid}/files")]
    //public async Task<ActionResult<List<ModuleCaseFileDto>>> GetTaskFiles(Guid taskId)
    //{
    //    var result = await _service.GetTaskFilesAsync(taskId);
    //    return Ok(result);
    //}

    [HttpPost("api/cases/{caseId:guid}/files")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> Upload(Guid caseId)
    {
        var files = Request.Form.Files.ToList();
        var uploadEmp = GetUploadEmp();
        Console.WriteLine($"[CaseFilesController] Upload by CaseId. caseId={caseId}, files={files.Count}, uploadEmp={uploadEmp}");

        try
        {
            await _service.UploadAsync(caseId, files, uploadEmp);
            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaseFilesController] Upload failed. caseId={caseId}, error={ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("api/records/{recordId:guid}/cases/upload/{caseNo}/attachments")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> UploadByCaseNo(Guid recordId, string caseNo)
    {
        var decodedCaseNo = Uri.UnescapeDataString(caseNo);
        var files = Request.Form.Files.ToList();
        var uploadEmp = GetUploadEmp();
        Console.WriteLine($"[CaseFilesController] Upload by CaseNo. recordId={recordId}, caseNo={decodedCaseNo}, files={files.Count}, uploadEmp={uploadEmp}");

        try
        {
            await _service.UploadByCaseNoAsync(recordId, decodedCaseNo, files, uploadEmp);
            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaseFilesController] Upload failed. recordId={recordId}, caseNo={decodedCaseNo}, error={ex}");
            return BadRequest(ex.Message);
        }
    }

    //[HttpPost("api/tasks/{taskId:guid}/files")]
    //[RequestSizeLimit(200_000_000)]
    //[RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    //public async Task<IActionResult> UploadTaskReport(Guid taskId)
    //{
    //    var files = Request.Form.Files.ToList();
    //    var uploadEmp = GetUploadEmp();
    //    Console.WriteLine($"[CaseFilesController] Upload task report. taskId={taskId}, files={files.Count}, uploadEmp={uploadEmp}");

    //    try
    //    {
    //        await _service.UploadTaskReportAsync(taskId, files, uploadEmp);
    //        return Ok();
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine($"[CaseFilesController] Upload task report failed. taskId={taskId}, error={ex}");
    //        return BadRequest(ex.Message);
    //    }
    //}

    [HttpGet("api/files/{fileId:guid}/download")]
    public async Task<IActionResult> Download(Guid fileId)
    {
        var file = await _service.DownloadAsync(fileId);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpDelete("api/files/{fileId:guid}")]
    [BusinessWrite]
    public async Task<IActionResult> Delete(Guid fileId)
    {
        try
        {
            var canDeleteApprovedFinal = (await _authorizationService.AuthorizeAsync(
                User,
                SystemAuthorization.Policies.Administration)).Succeeded;
            var deletedBy = User.FindFirstValue("account")
                ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.Identity?.Name;
            var ok = await _service.DeleteAsync(fileId, canDeleteApprovedFinal, deletedBy);
            return ok ? Ok() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private string GetUploadEmp()
    {
        var name = User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "System" : name;
    }
}

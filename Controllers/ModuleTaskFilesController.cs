using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using System.Security.Claims;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api")]
public class ModuleTaskFilesController : ControllerBase
{
    private readonly ICaseFileService _fileService;
    private readonly IAuthorizationService _authorizationService;

    public ModuleTaskFilesController(ICaseFileService fileService, IAuthorizationService authorizationService)
    {
        _fileService = fileService;
        _authorizationService = authorizationService;
    }

    // Task 一般附件：已建立 TaskId
    [HttpPost("tasks/{taskId:guid}/files")]
    [BusinessWrite]
    public async Task<IActionResult> UploadTaskFiles(Guid taskId)
    {
        try
        {
            var files = Request.Form.Files;

            if (files == null || files.Count == 0)
                return BadRequest("沒有收到檔案");

            await _fileService.UploadTaskFilesAsync(
                taskId,
                files.ToList(),
                uploadEmp: GetUploadEmp());

            return Ok("上傳成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskFiles failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("tasks/{taskId:guid}/files")]
    public async Task<IActionResult> GetTaskFiles(Guid taskId)
    {
        var files = await _fileService.GetTaskFilesAsync(taskId);
        return Ok(files);
    }

    // Task 一般附件：Task 尚未正式儲存，先用 TaskNo 綁檔案
    [HttpPost("cases/{caseId:guid}/tasks/upload/{taskNo}/files")]
    [BusinessWrite]
    public async Task<IActionResult> UploadTaskFilesByTaskNo(Guid caseId, string taskNo)
    {
        try
        {
            var files = Request.Form.Files;

            if (files == null || files.Count == 0)
                return BadRequest("沒有收到檔案");

            await _fileService.UploadTaskFilesByTaskNoAsync(
                caseId,
                taskNo,
                files.ToList(),
                uploadEmp: GetUploadEmp());

            return Ok("上傳成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskFilesByTaskNo failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("cases/{caseId:guid}/tasks/{taskNo}/files")]
    public async Task<IActionResult> GetTaskFilesByTaskNo(Guid caseId, string taskNo)
    {
        var files = await _fileService.GetTaskFilesByTaskNoAsync(caseId, taskNo);
        return Ok(files);
    }

    // Task 測試報告：已建立 TaskId
    [HttpPost("tasks/{taskId:guid}/test-reports")]
    [BusinessWrite]
    public async Task<IActionResult> UploadTaskReports(Guid taskId)
    {
        try
        {
            var files = Request.Form.Files;

            if (files == null || files.Count == 0)
                return BadRequest("沒有收到檔案");

            await _fileService.UploadTaskReportAsync(
                taskId,
                files.ToList(),
                uploadEmp: GetUploadEmp(),
                autoApprove: await CanReviewAsync());

            return Ok("測試報告上傳成功");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskReports failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    // Task 測試報告：Task 尚未正式儲存，先用 TaskNo 綁檔案
    [HttpPost("cases/{caseId:guid}/tasks/upload/{taskNo}/test-reports")]
    [BusinessWrite]
    public async Task<IActionResult> UploadTaskReportsByTaskNo(Guid caseId, string taskNo)
    {
        try
        {
            var files = Request.Form.Files;

            if (files == null || files.Count == 0)
                return BadRequest("沒有收到檔案");

            await _fileService.UploadTaskReportByTaskNoAsync(
                caseId,
                taskNo,
                files.ToList(),
                uploadEmp: GetUploadEmp(),
                autoApprove: await CanReviewAsync());

            return Ok("測試報告上傳成功");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskReportsByTaskNo failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("files/{fileId:guid}/versions")]
    [BusinessWrite]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> UploadNewVersion(Guid fileId)
    {
        try
        {
            var files = Request.Form.Files;
            if (files.Count != 1)
                return BadRequest("上傳新版時一次只能選擇一個檔案。");

            await _fileService.UploadNewTestReportVersionAsync(
                fileId,
                files[0],
                GetUploadEmp(),
                await CanReviewAsync());
            return Ok("測試報告新版上傳成功");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<bool> CanReviewAsync() =>
        (await _authorizationService.AuthorizeAsync(
            User,
            SystemAuthorization.Policies.Administration)).Succeeded;

    private string GetUploadEmp()
    {
        var name = User?.FindFirstValue("account")
            ?? User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "System" : name;
    }
}

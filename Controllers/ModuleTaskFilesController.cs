using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Authorize(Policy = SystemAuthorization.Policies.CsitStaff)]
[Route("api")]
public class ModuleTaskFilesController : ControllerBase
{
    private readonly ICaseFileService _fileService;

    public ModuleTaskFilesController(ICaseFileService fileService)
    {
        _fileService = fileService;
    }

    // Task 一般附件：已建立 TaskId
    [HttpPost("tasks/{taskId:guid}/files")]
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
                uploadEmp: "SYSTEM");

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
                uploadEmp: "SYSTEM");

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
                uploadEmp: "SYSTEM");

            return Ok("測試報告上傳成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskReports failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    // Task 測試報告：Task 尚未正式儲存，先用 TaskNo 綁檔案
    [HttpPost("cases/{caseId:guid}/tasks/upload/{taskNo}/test-reports")]
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
                uploadEmp: "SYSTEM");

            return Ok("測試報告上傳成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleTaskFilesController] UploadTaskReportsByTaskNo failed: {ex}");
            return StatusCode(500, ex.Message);
        }
    }
}

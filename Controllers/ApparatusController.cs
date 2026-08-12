using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
public class ApparatusController : ControllerBase
{
    private readonly IApparatusService _service;

    public ApparatusController(IApparatusService service)
    {
        _service = service;
    }

    // 舊設備管理相容路由：固定 equipment
    [HttpGet("api/apparatus/new-id")]
    [HttpGet("api/assets/{moduleCode}/new-id")]
    public async Task<IActionResult> GetNewId(string? moduleCode = null)
    {
        var id = await _service.GenerateNewIdAsync();
        return Ok(new NewApparatusIdResponse { Id = id });
    }

    [HttpGet("api/apparatus")]
    public Task<IActionResult> GetLegacyList([FromQuery] string? keyword, [FromQuery] string? kind)
    {
        return GetList("equipment", keyword, kind);
    }

    [HttpGet("api/assets/{moduleCode}")]
    public async Task<IActionResult> GetList(string moduleCode, [FromQuery] string? keyword, [FromQuery] string? kind)
    {
        try
        {
            moduleCode = NormalizeModuleCode(moduleCode);

            Console.WriteLine($"[ApparatusController] GetList start. moduleCode={moduleCode}, keyword={keyword}, kind={kind}");

            var result = await _service.GetListAsync(moduleCode, keyword, kind);

            Console.WriteLine($"[ApparatusController] GetList success. moduleCode={moduleCode}, count={result.Count}");

            return Ok(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusController] GetList failed: {ex}");
            return StatusCode(500, ex.ToString());
        }
    }

    [HttpGet("api/apparatus/{id}")]
    public Task<IActionResult> GetLegacyById(string id)
    {
        return GetById("equipment", id);
    }

    [HttpGet("api/assets/{moduleCode}/{id}")]
    public async Task<IActionResult> GetById(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var result = await _service.GetByIdAsync(moduleCode, id);

        if (result == null)
            return NotFound("找不到資料");

        return Ok(result);
    }

    [HttpPost("api/apparatus")]
    public Task<IActionResult> LegacyCreate([FromBody] ApparatusUpsertRequest request)
    {
        return Create("equipment", request);
    }

    [HttpPost("api/assets/{moduleCode}")]
    public async Task<IActionResult> Create(string moduleCode, [FromBody] ApparatusUpsertRequest request)
    {
        try
        {
            moduleCode = NormalizeModuleCode(moduleCode);
            request.ModuleCode = moduleCode;

            var id = await _service.CreateAsync(moduleCode, request);
            return Ok(new { id });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusController] Create failed: {ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("api/apparatus/{id}")]
    public Task<IActionResult> LegacyUpdate(string id, [FromBody] ApparatusUpsertRequest request)
    {
        return Update("equipment", id, request);
    }

    [HttpPut("api/assets/{moduleCode}/{id}")]
    public async Task<IActionResult> Update(string moduleCode, string id, [FromBody] ApparatusUpsertRequest request)
    {
        try
        {
            moduleCode = NormalizeModuleCode(moduleCode);
            request.ModuleCode = moduleCode;

            var ok = await _service.UpdateAsync(moduleCode, id, request);
            return ok ? NoContent() : NotFound("找不到資料");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusController] Update failed: {ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("api/apparatus/{id}")]
    public Task<IActionResult> LegacyDelete(string id)
    {
        return Delete("equipment", id);
    }

    [HttpDelete("api/assets/{moduleCode}/{id}")]
    public async Task<IActionResult> Delete(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var ok = await _service.DeleteAsync(moduleCode, id);
        return ok ? NoContent() : NotFound("找不到資料");
    }

    [HttpGet("api/apparatus/{id}/files")]
    public Task<IActionResult> LegacyGetFiles(string id)
    {
        return GetFiles("equipment", id);
    }

    [HttpGet("api/assets/{moduleCode}/{id}/files")]
    public async Task<IActionResult> GetFiles(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var files = await _service.GetFilesAsync(moduleCode, id);
        return Ok(files);
    }

    [HttpPost("api/apparatus/{id}/files")]
    public Task<IActionResult> LegacyUploadFiles(string id)
    {
        return UploadFiles("equipment", id);
    }

    [HttpPost("api/assets/{moduleCode}/{id}/files")]
    public async Task<IActionResult> UploadFiles(string moduleCode, string id)
    {
        try
        {
            moduleCode = NormalizeModuleCode(moduleCode);

            var files = Request.Form.Files;

            if (files == null || files.Count == 0)
                return BadRequest("沒有收到檔案");

            await _service.UploadFilesAsync(moduleCode, id, files.ToList(), "SYSTEM");
            return Ok("上傳成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusController] UploadFiles failed: {ex}");
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("api/apparatus/files/{fileId:guid}/content")]
    [HttpGet("api/assets/files/{fileId:guid}/content")]
    public async Task<IActionResult> GetFileContent(Guid fileId)
    {
        try
        {
            var file = await _service.GetFileContentAsync(fileId);
            return File(file.Content, file.ContentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusController] GetFileContent failed: {ex}");
            return NotFound(ex.Message);
        }
    }

    [HttpDelete("api/apparatus/files/{fileId:guid}")]
    [HttpDelete("api/assets/files/{fileId:guid}")]
    public async Task<IActionResult> DeleteFile(Guid fileId)
    {
        var ok = await _service.DeleteFileAsync(fileId);
        return ok ? NoContent() : NotFound("找不到檔案");
    }

    private static string NormalizeModuleCode(string? moduleCode)
    {
        return string.IsNullOrWhiteSpace(moduleCode) ? "equipment" : moduleCode.Trim();
    }
}

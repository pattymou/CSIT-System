using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ModuleCaseService : IModuleCaseService
{
    private readonly AppDbContext _db;
    private readonly ICaseFileService _caseFileService;

    public ModuleCaseService(AppDbContext db, ICaseFileService caseFileService)
    {
        _db = db;
        _caseFileService = caseFileService;
    }

    public async Task<ListResponseDto<ModuleCaseListItemDto>> GetListAsync(Guid recordId, int page, int pageSize, string? status)
    {
        var query = _db.ModuleRecordCases.Where(x => x.RecordId == recordId && !x.IsDraft);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ModuleCaseListItemDto
            {
                Id = x.Id,
                CaseNo = x.CaseNo,
                Name = x.Name,
                Status = x.Status
            })
            .ToListAsync();

        return new ListResponseDto<ModuleCaseListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ModuleCaseDetailDto?> GetByIdAsync(Guid id)
    {
        return await _db.ModuleRecordCases
            .Where(x => x.Id == id)
            .Select(x => new ModuleCaseDetailDto
            {
                Id = x.Id,
                RecordId = x.RecordId,
                CaseNo = x.CaseNo,
                Name = x.Name,
                Status = x.Status,
                Note = x.Note,
                SortOrder = x.SortOrder,
                WifiNo = x.WifiNo,
                BtNo = x.BtNo,
                GcfNo = x.GcfNo,
                PtcrbNo = x.PtcrbNo
            })
            .FirstOrDefaultAsync();
    }

    public async Task<NewCaseNoResponse> GetNewCaseNoAsync(Guid recordId)
    {
        var exists = await _db.ModuleRecords.AnyAsync(x => x.Id == recordId);
        if (!exists)
        {
            throw new InvalidOperationException("找不到主單");
        }

        var caseNo = $"CAS-{DateTime.Now:yyyyMMddHHmmssfff}";
        Console.WriteLine($"[ModuleCaseService] New CaseNo generated: recordId={recordId}, caseNo={caseNo}");

        return new NewCaseNoResponse
        {
            CaseNo = caseNo
        };
    }

    public async Task<Guid> CreateAsync(Guid recordId, ModuleCaseUpsertRequest request)
    {
        var recordExists = await _db.ModuleRecords.AnyAsync(x => x.Id == recordId);
        if (!recordExists)
        {
            throw new InvalidOperationException("找不到主單");
        }

        var caseNo = string.IsNullOrWhiteSpace(request.CaseNo)
            ? $"CAS-{DateTime.Now:yyyyMMddHHmmssfff}"
            : request.CaseNo.Trim();

        var entity = new ModuleRecordCase
        {
            Id = Guid.NewGuid(),
            RecordId = recordId,
            CaseNo = caseNo,
            Name = NormalizeName(request.Name, caseNo),
            Status = NormalizeStatus(request.Status),
            Note = request.Note,
            SortOrder = request.SortOrder,
            WifiNo = request.WifiNo,
            BtNo = request.BtNo,
            GcfNo = request.GcfNo,
            PtcrbNo = request.PtcrbNo,
            IsDraft = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ModuleRecordCases.Add(entity);
        await _db.SaveChangesAsync();

        await _caseFileService.BindFilesToCaseAsync(recordId, caseNo, entity.Id, entity.Name);
        await _caseFileService.RebuildProjectRawDataAsync(recordId);

        Console.WriteLine($"[ModuleCaseService] Case created: id={entity.Id}, recordId={recordId}, caseNo={caseNo}, name={entity.Name}");
        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, ModuleCaseUpsertRequest request)
    {
        var entity = await _db.ModuleRecordCases.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return false;

        var oldName = entity.Name;
        entity.Name = NormalizeName(request.Name, entity.CaseNo);
        entity.Status = NormalizeStatus(request.Status);
        entity.Note = request.Note;
        entity.SortOrder = request.SortOrder;
        entity.WifiNo = request.WifiNo;
        entity.BtNo = request.BtNo;
        entity.GcfNo = request.GcfNo;
        entity.PtcrbNo = request.PtcrbNo;
        entity.IsDraft = false;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _caseFileService.BindFilesToCaseAsync(entity.RecordId, entity.CaseNo, entity.Id, entity.Name);

        if (!string.Equals(oldName, entity.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _caseFileService.MoveCaseFolderAsync(entity.RecordId, entity.CaseNo, oldName, entity.Name);
        }

        await _caseFileService.RebuildProjectRawDataAsync(entity.RecordId);

        Console.WriteLine($"[ModuleCaseService] Case updated: id={entity.Id}, caseNo={entity.CaseNo}, name={entity.Name}");
        return true;
    }

    public async Task<bool> DeleteAsync(Guid caseId)
    {
        Console.WriteLine($"[ModuleCaseService] Delete case start. caseId={caseId}");

        var caseEntity = await _db.ModuleRecordCases
            .FirstOrDefaultAsync(x => x.Id == caseId);

        if (caseEntity == null)
        {
            Console.WriteLine($"[ModuleCaseService] Case not found. caseId={caseId}");
            return false;
        }

        var recordId = caseEntity.RecordId;

        var tasks = await _db.ModuleRecordTasks
            .Where(x => x.CaseId == caseId)
            .ToListAsync();

        // ✅ 先刪 Case 底下所有檔案
        // 包含：
        // 1. Case 一般附件
        // 2. Task 測試報告
        // 3. NAS RAW DATA / rawdata.json
        await _caseFileService.DeleteFilesByCaseIdAsync(caseId);

        // ✅ 再刪 Task
        _db.ModuleRecordTasks.RemoveRange(tasks);

        // ✅ 最後刪 Case
        _db.ModuleRecordCases.Remove(caseEntity);

        await _db.SaveChangesAsync();

        await _caseFileService.RebuildProjectRawDataAsync(recordId);

        Console.WriteLine($"[ModuleCaseService] Delete case done. caseId={caseId}, tasks={tasks.Count}");

        return true;
    }

    private static void DeletePhysicalFile(ModuleCaseFile file)
    {
        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            Console.WriteLine($"[DELETE FILE] {fullPath}");
        }
        else
        {
            Console.WriteLine($"[DELETE FILE SKIPPED] File not found: {fullPath}");
        }

        DeleteFolderIfEmpty(file.FilePath);
    }

    private static void DeleteFolderIfEmpty(string folderPath)
    {
        if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
        {
            Directory.Delete(folderPath);
            Console.WriteLine($"[DELETE EMPTY FOLDER] {folderPath}");
        }
    }

    private static string NormalizeName(string? name, string fallback)
    {
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    private static string NormalizeStatus(string? status)
    {
        return string.IsNullOrWhiteSpace(status) ? "Open" : status.Trim();
    }
}

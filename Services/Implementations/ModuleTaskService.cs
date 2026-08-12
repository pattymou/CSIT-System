using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ModuleTaskService : IModuleTaskService
{
    private readonly AppDbContext _db;
    private readonly ICaseFileService _caseFileService;

    public ModuleTaskService(AppDbContext db, ICaseFileService caseFileService)
    {
        _db = db;
        _caseFileService = caseFileService;
    }

    public async Task<ListResponseDto<ModuleTaskListItemDto>> GetListAsync(Guid caseId, int page, int pageSize, string? status)
    {
        var query = _db.ModuleRecordTasks.Where(x => x.CaseId == caseId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ModuleTaskListItemDto
            {
                Id = x.Id,
                TaskNo = x.TaskNo,
                Name = x.Name,
                AssignEngineer = x.AssignEngineer,
                Status = x.Status,
                Result = x.Result,
                Progress = x.Progress,
                StartDate = x.StartDate,
                ExpectedEndDate = x.ExpectedEndDate
            })
            .ToListAsync();

        return new ListResponseDto<ModuleTaskListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ModuleTaskDetailDto?> GetByIdAsync(Guid id)
    {
        return await _db.ModuleRecordTasks
            .Where(x => x.Id == id)
            .Select(x => new ModuleTaskDetailDto
            {
                Id = x.Id,
                CaseId = x.CaseId,
                TaskNo = x.TaskNo,
                Name = x.Name,
                AssignEngineer = x.AssignEngineer,
                Status = x.Status,
                Result = x.Result,
                Progress = x.Progress,
                StartDate = x.StartDate,
                ExpectedEndDate = x.ExpectedEndDate,
                SubPu = x.SubPu,
                ModelName = x.ModelName,
                Lab = x.Lab,
                Quoted = x.Quoted,
                Reimburse = x.Reimburse,
                Note = x.Note
            })
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> CreateAsync(Guid caseId, ModuleTaskUpsertRequest request)
    {
        var recordId = await _db.ModuleRecordCases
            .Where(x => x.Id == caseId)
            .Select(x => x.RecordId)
            .FirstAsync();

        var taskNo = string.IsNullOrWhiteSpace(request.TaskNo)
            ? $"TSK-{DateTime.Now:yyyyMMddHHmmss}"
            : request.TaskNo;

        var entity = new ModuleRecordTask
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            TaskNo = taskNo,
            Name = request.Name,
            AssignEngineer = request.AssignEngineer,
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Open" : request.Status,
            Result = request.Result,
            Progress = request.Progress,
            StartDate = request.StartDate,
            ExpectedEndDate = request.ExpectedEndDate,
            SubPu = request.SubPu,
            ModelName = request.ModelName,
            Lab = request.Lab,
            Quoted = request.Quoted,
            Reimburse = request.Reimburse,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ModuleRecordTasks.Add(entity);
        await _db.SaveChangesAsync();

        await _caseFileService.BindFilesToTaskAsync(caseId, taskNo, entity.Id);
        await _caseFileService.RebuildProjectRawDataAsync(recordId);

        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, ModuleTaskUpsertRequest request)
    {
        var entity = await _db.ModuleRecordTasks
            .Include(x => x.Case)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return false;

        entity.Name = request.Name;
        entity.AssignEngineer = request.AssignEngineer;
        entity.Status = string.IsNullOrWhiteSpace(request.Status) ? "Open" : request.Status;
        entity.Result = request.Result;
        entity.Progress = request.Progress;
        entity.StartDate = request.StartDate;
        entity.ExpectedEndDate = request.ExpectedEndDate;
        entity.SubPu = request.SubPu;
        entity.ModelName = request.ModelName;
        entity.Lab = request.Lab;
        entity.Quoted = request.Quoted;
        entity.Reimburse = request.Reimburse;
        entity.Note = request.Note;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _caseFileService.RebuildProjectRawDataAsync(entity.Case.RecordId);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid taskId)
    {
        Console.WriteLine($"[ModuleTaskService] Delete task start. taskId={taskId}");

        var task = await _db.ModuleRecordTasks
            .Include(x => x.Case)
            .FirstOrDefaultAsync(x => x.Id == taskId);

        if (task == null)
        {
            Console.WriteLine($"[ModuleTaskService] Task not found. taskId={taskId}");
            return false;
        }

        var recordId = task.Case.RecordId;

        // ✅🔥 就加在這裡（刪 Task 前）
        await _caseFileService.DeleteFilesByTaskIdAsync(taskId);

        _db.ModuleRecordTasks.Remove(task);

        await _db.SaveChangesAsync();

        await _caseFileService.RebuildProjectRawDataAsync(recordId);

        Console.WriteLine($"[ModuleTaskService] Delete task done. taskId={taskId}");
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
}

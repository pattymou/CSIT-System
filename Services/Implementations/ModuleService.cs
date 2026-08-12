using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ModuleService : IModuleService
{
    private readonly AppDbContext _db;

    public ModuleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ModuleListItemDto>> GetEnabledModulesAsync()
    {
        return await _db.Modules
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .Select(x => new ModuleListItemDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                DisplayName = x.DisplayName,
                RoutePrefix = x.RoutePrefix,
                Icon = x.Icon,
                IsEnabled = x.IsEnabled,
                SortOrder = x.SortOrder
            })
            .ToListAsync();
    }

    public async Task<List<ModuleListItemDto>> GetAllAsync()
    {
        return await _db.Modules
            .OrderBy(x => x.SortOrder)
            .Select(x => new ModuleListItemDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                DisplayName = x.DisplayName,
                RoutePrefix = x.RoutePrefix,
                Icon = x.Icon,
                IsEnabled = x.IsEnabled,
                SortOrder = x.SortOrder
            })
            .ToListAsync();
    }

    public async Task<ModuleDetailDto?> GetByCodeAsync(string code)
    {
        return await _db.Modules
            .Where(x => x.Code == code)
            .Select(x => new ModuleDetailDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                DisplayName = x.DisplayName,
                RoutePrefix = x.RoutePrefix,
                Icon = x.Icon,
                Description = x.Description,
                IsEnabled = x.IsEnabled,
                SortOrder = x.SortOrder
            })
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> CreateAsync(ModuleUpsertRequest request)
    {
        var entity = new ModuleEntity
        {
            Id = Guid.NewGuid(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            DisplayName = request.DisplayName.Trim(),
            RoutePrefix = request.RoutePrefix.Trim(),
            Icon = request.Icon,
            Description = request.Description,
            IsEnabled = request.IsEnabled,
            SortOrder = request.SortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Modules.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, ModuleUpsertRequest request)
    {
        var entity = await _db.Modules.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return false;

        entity.Code = request.Code.Trim();
        entity.Name = request.Name.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.RoutePrefix = request.RoutePrefix.Trim();
        entity.Icon = request.Icon;
        entity.Description = request.Description;
        entity.IsEnabled = request.IsEnabled;
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<ModuleRecordListItemDto>> GetModuleRecordsAsync(Guid moduleId, string status)
    {
        Console.WriteLine($"[GetModuleRecords] moduleId={moduleId}, status={status}");

        return await _db.ModuleRecords
            .Where(x => x.ModuleId == moduleId && x.Status == status)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ModuleRecordListItemDto
            {
                Id = x.Id,
                RecordNo = x.RecordNo,
                Name = x.Name,
                Customer = x.Customer,
                Status = x.Status,
                Progress = x.Progress
            })
            .ToListAsync();
    }
}
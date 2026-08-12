using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class SystemOptionService : ISystemOptionService
{
    private readonly AppDbContext _db;

    public SystemOptionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<SystemOptionDto>> GetAllAsync(string? category)
    {
        Console.WriteLine($"[SystemOptionService] GetAllAsync start. category={category}");

        var query = _db.SystemOptions.AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(x => x.Category == category);
        }

        var result = await query
            .OrderBy(x => x.Category)
            .ThenBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .Select(x => ToDto(x))
            .ToListAsync();

        Console.WriteLine($"[SystemOptionService] GetAllAsync done. count={result.Count}");

        return result;
    }

    public async Task<List<SystemOptionDto>> GetEnabledByCategoryAsync(string category)
    {
        Console.WriteLine($"[SystemOptionService] GetEnabledByCategoryAsync start. category={category}");

        if (string.IsNullOrWhiteSpace(category))
            throw new InvalidOperationException("Category 不可空白");

        var result = await _db.SystemOptions
            .Where(x => x.Category == category && x.IsEnabled)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .Select(x => ToDto(x))
            .ToListAsync();

        Console.WriteLine($"[SystemOptionService] GetEnabledByCategoryAsync done. category={category}, count={result.Count}");

        return result;
    }

    public async Task<Guid> CreateAsync(SystemOptionUpsertRequest request)
    {
        Console.WriteLine($"[SystemOptionService] CreateAsync start. category={request.Category}, name={request.Name}");

        ValidateRequest(request);

        var value = string.IsNullOrWhiteSpace(request.Value)
            ? request.Name.Trim()
            : request.Value.Trim();

        var exists = await _db.SystemOptions.AnyAsync(x =>
            x.Category == request.Category.Trim() &&
            x.Value == value);

        if (exists)
            throw new InvalidOperationException($"參數已存在：{request.Category} / {value}");

        var entity = new SystemOption
        {
            Id = Guid.NewGuid(),
            Category = request.Category.Trim(),
            Name = request.Name.Trim(),
            Value = value,
            Sort = request.Sort,
            IsEnabled = request.IsEnabled,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SystemOptions.Add(entity);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[SystemOptionService] CreateAsync done. id={entity.Id}");

        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, SystemOptionUpsertRequest request)
    {
        Console.WriteLine($"[SystemOptionService] UpdateAsync start. id={id}");

        ValidateRequest(request);

        var entity = await _db.SystemOptions.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            Console.WriteLine($"[SystemOptionService] UpdateAsync failed. not found. id={id}");
            return false;
        }

        var value = string.IsNullOrWhiteSpace(request.Value)
            ? request.Name.Trim()
            : request.Value.Trim();

        var exists = await _db.SystemOptions.AnyAsync(x =>
            x.Id != id &&
            x.Category == request.Category.Trim() &&
            x.Value == value);

        if (exists)
            throw new InvalidOperationException($"參數已存在：{request.Category} / {value}");

        entity.Category = request.Category.Trim();
        entity.Name = request.Name.Trim();
        entity.Value = value;
        entity.Sort = request.Sort;
        entity.IsEnabled = request.IsEnabled;
        entity.Note = request.Note;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        Console.WriteLine($"[SystemOptionService] UpdateAsync done. id={id}");

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        Console.WriteLine($"[SystemOptionService] DeleteAsync start. id={id}");

        var entity = await _db.SystemOptions.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            Console.WriteLine($"[SystemOptionService] DeleteAsync failed. not found. id={id}");
            return false;
        }

        _db.SystemOptions.Remove(entity);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[SystemOptionService] DeleteAsync done. id={id}");

        return true;
    }

    private static void ValidateRequest(SystemOptionUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Category))
            throw new InvalidOperationException("參數類別不可空白");

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException("顯示名稱不可空白");
    }

    private static SystemOptionDto ToDto(SystemOption x)
    {
        return new SystemOptionDto
        {
            Id = x.Id,
            Category = x.Category,
            Name = x.Name,
            Value = x.Value,
            Sort = x.Sort,
            IsEnabled = x.IsEnabled,
            Note = x.Note
        };
    }
}
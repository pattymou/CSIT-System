using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VerificationCategoryService(AppDbContext db) : IVerificationCategoryService
{
    public async Task<Guid> CreateAsync(VerificationCategoryUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = await ValidateAsync(request, null, cancellationToken);
        var now = DateTime.UtcNow;
        var entity = new VerificationCategory { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(entity, request, values);
        db.VerificationCategories.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, VerificationCategoryUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = await db.VerificationCategories.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        var values = await ValidateAsync(request, id, cancellationToken);
        Apply(entity, request, values);
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<VerificationCategoryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await db.VerificationCategories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity
            ? Map(entity)
            : null;

    public async Task<List<VerificationCategoryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await db.VerificationCategories.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken)).Select(Map).ToList();

    public Task<List<VerificationCategoryOptionDto>> ListActiveOptionsAsync(CancellationToken cancellationToken = default) =>
        db.VerificationCategories.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .Select(x => new VerificationCategoryOptionDto { Id = x.Id, Code = x.Code, Name = x.Name })
            .ToListAsync(cancellationToken);

    private async Task<(string Code, string Name, string ModuleCode, string LeaderAccount, string? LeaderDisplayName)> ValidateAsync(
        VerificationCategoryUpsertRequest request,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        var code = Required(request.Code, nameof(request.Code)).ToUpperInvariant();
        var name = Required(request.Name, nameof(request.Name));
        var moduleCode = Required(request.ModuleCode, nameof(request.ModuleCode));
        var leaderAccount = Required(request.LeaderAccount, nameof(request.LeaderAccount)).ToLowerInvariant();
        var leaderDisplayName = Clean(request.LeaderDisplayName);
        if (request.DisplayOrder < 0) throw new ArgumentOutOfRangeException(nameof(request.DisplayOrder), "DisplayOrder cannot be negative.");

        if (await db.VerificationCategories.AnyAsync(x => x.Code == code && (!excludedId.HasValue || x.Id != excludedId.Value), cancellationToken))
            throw new InvalidOperationException($"Verification category code already exists: {code}.");

        if (!await db.Modules.AnyAsync(x => x.Code == moduleCode && x.IsEnabled, cancellationToken))
            throw new InvalidOperationException($"ModuleCode must reference an enabled module: {moduleCode}.");

        return (code, name, moduleCode, leaderAccount, leaderDisplayName);
    }

    private static void Apply(
        VerificationCategory entity,
        VerificationCategoryUpsertRequest request,
        (string Code, string Name, string ModuleCode, string LeaderAccount, string? LeaderDisplayName) values)
    {
        entity.Code = values.Code;
        entity.Name = values.Name;
        entity.ModuleCode = values.ModuleCode;
        entity.LeaderAccount = values.LeaderAccount;
        entity.LeaderDisplayName = values.LeaderDisplayName;
        entity.IsActive = request.IsActive;
        entity.DisplayOrder = request.DisplayOrder;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static VerificationCategoryDto Map(VerificationCategory entity) => new()
    {
        Id = entity.Id,
        Code = entity.Code,
        Name = entity.Name,
        ModuleCode = entity.ModuleCode,
        LeaderAccount = entity.LeaderAccount,
        LeaderDisplayName = entity.LeaderDisplayName,
        IsActive = entity.IsActive,
        DisplayOrder = entity.DisplayOrder,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}

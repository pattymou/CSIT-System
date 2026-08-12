using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class MenuManagementService : IMenuManagementService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public MenuManagementService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<MenuSectionDto>> GetSectionsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.MenuSections
            .OrderBy(x => x.SortOrder)
            .Select(x => new MenuSectionDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                Icon = x.Icon,
                SortOrder = x.SortOrder,
                IsEnabled = x.IsEnabled
            })
            .ToListAsync();
    }

    public async Task<List<MenuItemDto>> GetItemsBySectionAsync(Guid sectionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.MenuItems
            .Where(x => x.SectionId == sectionId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new MenuItemDto
            {
                Id = x.Id,
                SectionId = x.SectionId,
                Code = x.Code,
                Title = x.Title,
                RoutePath = x.RoutePath,
                Icon = x.Icon,
                SortOrder = x.SortOrder,
                IsEnabled = x.IsEnabled,
                AdminOnly = x.AdminOnly,
                ModuleCode = x.ModuleCode,
                UseStandardTemplate = x.UseStandardTemplate,
                TemplateType = x.TemplateType
            })
            .ToListAsync();
    }

    public async Task<MenuItemDto?> GetItemByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.MenuItems
            .Where(x => x.Id == id)
            .Select(x => new MenuItemDto
            {
                Id = x.Id,
                SectionId = x.SectionId,
                Code = x.Code,
                Title = x.Title,
                RoutePath = x.RoutePath,
                Icon = x.Icon,
                SortOrder = x.SortOrder,
                IsEnabled = x.IsEnabled,
                AdminOnly = x.AdminOnly,
                ModuleCode = x.ModuleCode,
                UseStandardTemplate = x.UseStandardTemplate,
                TemplateType = x.TemplateType
            })
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> CreateItemAsync(Guid sectionId, MenuItemUpsertRequest request)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var code = NormalizeRequired(request.Code, "Code");
        var title = NormalizeRequired(request.Title, "子選單名稱");
        var routePath = NormalizeRequired(request.RoutePath, "路徑");
        var moduleCode = NormalizeNullable(request.ModuleCode);
        var templateType = NormalizeTemplateType(request.TemplateType);

        ValidateTemplateRule(templateType, code, routePath, moduleCode);

        await ValidateDuplicateAsync(
            db: db,
            currentId: null,
            code: code,
            routePath: routePath,
            moduleCode: moduleCode,
            templateType: templateType);

        var entity = new MenuItem
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
            Code = code,
            Title = title,
            RoutePath = routePath,
            Icon = NormalizeIcon(request.Icon),
            SortOrder = request.SortOrder,
            IsEnabled = request.IsEnabled,
            AdminOnly = request.AdminOnly,
            ModuleCode = moduleCode,
            TemplateType = templateType,
            UseStandardTemplate = templateType == "ThreeLevel",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.MenuItems.Add(entity);
        await db.SaveChangesAsync();

        Console.WriteLine($"[MenuManagementService] Created menu item. id={entity.Id}, title={entity.Title}, code={entity.Code}, route={entity.RoutePath}, moduleCode={entity.ModuleCode}, templateType={entity.TemplateType}");

        return entity.Id;
    }

    public async Task<bool> UpdateItemAsync(Guid id, MenuItemUpsertRequest request)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = await db.MenuItems.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            Console.WriteLine($"[MenuManagementService] Update skipped. item not found. id={id}");
            return false;
        }

        var code = NormalizeRequired(request.Code, "Code");
        var title = NormalizeRequired(request.Title, "子選單名稱");
        var routePath = NormalizeRequired(request.RoutePath, "路徑");
        var moduleCode = NormalizeNullable(request.ModuleCode);
        var templateType = NormalizeTemplateType(request.TemplateType);

        ValidateTemplateRule(templateType, code, routePath, moduleCode);

        await ValidateDuplicateAsync(
            db: db,
            currentId: id,
            code: code,
            routePath: routePath,
            moduleCode: moduleCode,
            templateType: templateType);

        entity.Code = code;
        entity.Title = title;
        entity.RoutePath = routePath;
        entity.Icon = NormalizeIcon(request.Icon);
        entity.SortOrder = request.SortOrder;
        entity.IsEnabled = request.IsEnabled;
        entity.AdminOnly = request.AdminOnly;
        entity.ModuleCode = moduleCode;
        entity.TemplateType = templateType;
        entity.UseStandardTemplate = templateType == "ThreeLevel";
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        Console.WriteLine($"[MenuManagementService] Updated menu item. id={entity.Id}, title={entity.Title}, code={entity.Code}, route={entity.RoutePath}, moduleCode={entity.ModuleCode}, templateType={entity.TemplateType}");

        return true;
    }

    public async Task<bool> DeleteItemAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = await db.MenuItems.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            Console.WriteLine($"[MenuManagementService] Delete skipped. item not found. id={id}");
            return false;
        }

        db.MenuItems.Remove(entity);
        await db.SaveChangesAsync();

        Console.WriteLine($"[MenuManagementService] Deleted menu item. id={id}");

        return true;
    }

    private static async Task ValidateDuplicateAsync(
        AppDbContext db,
        Guid? currentId,
        string code,
        string routePath,
        string? moduleCode,
        string? templateType)
    {
        var duplicateCode = await db.MenuItems
            .Where(x => currentId == null || x.Id != currentId.Value)
            .FirstOrDefaultAsync(x => x.Code == code);

        if (duplicateCode != null)
        {
            throw new InvalidOperationException($"Code 已存在：{code}。請改用其他 Code。");
        }

        var duplicateRoute = await db.MenuItems
            .Where(x => currentId == null || x.Id != currentId.Value)
            .FirstOrDefaultAsync(x => x.RoutePath == routePath);

        if (duplicateRoute != null)
        {
            throw new InvalidOperationException($"路徑已存在：{routePath}。請改用其他路徑。");
        }

        if (templateType == "Asset" && !string.IsNullOrWhiteSpace(moduleCode))
        {
            var duplicateAssetModule = await db.MenuItems
                .Where(x => currentId == null || x.Id != currentId.Value)
                .FirstOrDefaultAsync(x =>
                    x.TemplateType == "Asset" &&
                    x.ModuleCode == moduleCode);

            if (duplicateAssetModule != null)
            {
                throw new InvalidOperationException($"公版資產管理的 Module Code 已存在：{moduleCode}。請改用其他 Module Code。");
            }
        }
    }

    private static void ValidateTemplateRule(
        string? templateType,
        string code,
        string routePath,
        string? moduleCode)
    {
        if (templateType == "ThreeLevel")
        {
            if (string.IsNullOrWhiteSpace(moduleCode))
            {
                throw new InvalidOperationException("公版三層管理必須填 Module Code。");
            }

            var expectedRoute = $"/modules/{code}";
            if (!string.Equals(routePath, expectedRoute, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"公版三層管理的路徑應為：{expectedRoute}");
            }
        }

        if (templateType == "Asset")
        {
            if (string.IsNullOrWhiteSpace(moduleCode))
            {
                throw new InvalidOperationException("公版資產管理必須填 Module Code。");
            }

            if (!string.Equals(code, moduleCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("公版資產管理的 Code 與 Module Code 建議一致，例如 goods / goods。");
            }

            var expectedRoute = $"/assets/{moduleCode}";
            if (!string.Equals(routePath, expectedRoute, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"公版資產管理的路徑應為：{expectedRoute}");
            }
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} 不可空白。");
        }

        return value.Trim();
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeIcon(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "▣" : value.Trim();
    }

    private static string? NormalizeTemplateType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();

        return normalized switch
        {
            "ThreeLevel" => "ThreeLevel",
            "Asset" => "Asset",
            "Apparatus" => "Asset",
            _ => null
        };
    }
}
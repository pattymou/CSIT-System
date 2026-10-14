using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

public static class RoutingDepartmentOptions
{
    public static async Task<SystemOption> RequireAsync(
        AppDbContext db, Guid? optionId, CancellationToken cancellationToken)
    {
        var option = await db.SystemOptions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == optionId && x.Category == SystemOptionCategories.Department && x.IsEnabled,
            cancellationToken);
        if (option is null || !DepartmentFamilyMatcher.IsDa40Family(option.Value))
            throw new InvalidOperationException("請選擇啟用中的 DA40 family 部門。");
        return option;
    }
}

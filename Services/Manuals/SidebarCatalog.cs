using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services;

namespace SIT.DepartmentSystem.Web.Services.Manuals;

public sealed record SidebarEntry(string Code, string Title, string Route, string? Icon);
public sealed record SidebarGroup(string Code, string Name, string? Icon, IReadOnlyList<SidebarEntry> Entries);

/// <summary>The single source for the internal sidebar and manual chapter navigation.</summary>
public static class SidebarCatalog
{
    public static SidebarGroup RdServices { get; } = new("rd-services", "RD 服務", null,
        [new("home", "首頁", "/rd", null),
         new("verification-applications", "系統驗證申請單", "/verification-applications", null),
         new("reservations", "設備預約", "/reservations", null)]);

    public static async Task<IReadOnlyList<SidebarGroup>> LoadAsync(
        IMenuManagementService menus, IAuthorizationService authorization, ClaimsPrincipal user)
    {
        var isAdmin = user.IsInRole("Admin");
        var canAdmin = (await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.Administration)).Succeeded;
        var canReadQueue = (await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.KmQueueRead)).Succeeded;
        var groups = new List<SidebarGroup>();
        groups.Add(canAdmin
            ? new("supervisor", "SUPERVISOR APPROVAL", null,
                [new("supervisor-review", "主管待審", "/supervisor-review", null)])
            : new("my-items", "我的項目", null,
                [new("my-submissions", "我的提交項目", "/my-submissions", null)]));

        var sections = (await menus.GetSectionsAsync()).Where(x => x.IsEnabled)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToList();
        foreach (var section in sections)
        {
            var isAdministration = IsAdministration(section);
            if (isAdministration && !canAdmin) continue;
            var items = (await menus.GetItemsBySectionAsync(section.Id))
                .Where(x => x.IsEnabled && (!x.AdminOnly || isAdmin))
                .Where(x => !isAdministration || !x.RoutePath.Equals("/system/menu-management", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToList();
            if (isAdministration && !items.Any(x => x.RoutePath.Equals("/administration/km-metadata", StringComparison.OrdinalIgnoreCase)))
                items.Add(new MenuItemDto { Code = "km-metadata", Title = "KM 分析檢視", RoutePath = "/administration/km-metadata", Icon = "◇", SortOrder = 900, IsEnabled = true, AdminOnly = true });
            var entries = items.Where(x => !x.AdminOnly || isAdmin)
                .Select(x => new SidebarEntry(x.Code, DisplayTitle(x), DisplayRoute(x), x.Icon)).ToList();
            if (isAdministration)
            {
                if (canReadQueue) entries.Add(new("km-queue", "KM 處理佇列", "/administration/km-queue", "⇄"));
                entries.Add(new("accounts", "帳號管理", "/administration/accounts", "↪"));
            }
            if (entries.Count > 0) groups.Add(new(section.Code, section.Name, section.Icon, entries));
        }
        return groups;
    }

    private static bool IsAdministration(MenuSectionDto section) =>
        section.Code.Equals("administration", StringComparison.OrdinalIgnoreCase) ||
        section.Name.Equals("ADMINISTRATION", StringComparison.OrdinalIgnoreCase);

    private static string DisplayTitle(MenuItemDto item) =>
        item.RoutePath.Equals("/reservation-review", StringComparison.OrdinalIgnoreCase) ? "設備預約管理" : item.Title;
    private static string DisplayRoute(MenuItemDto item) =>
        item.RoutePath.Equals("/reservation-review", StringComparison.OrdinalIgnoreCase) ? "/staff/reservations" : item.RoutePath;

    public static bool IsOperationalRoute(string route) => route.StartsWith('/') && !route.StartsWith("//")
        && !route.Equals("/help/manual", StringComparison.OrdinalIgnoreCase)
        && !route.Equals("/rd/help/manual", StringComparison.OrdinalIgnoreCase);
}

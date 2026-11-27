using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.Manuals;
using SIT.DepartmentSystem.Web.Components.Pages;

namespace ApprovalFramework.Tests;

public sealed class ManualContentCoverageTests
{
    private static readonly string[] ExpectedInternalRoutes =
    [
        "/supervisor-review", "/my-submissions", "/validation-requests",
        "/validation-request-applications", "/assets/equipment", "/staff/reservations",
        "/documents", "/vendors", "/validation-request-settings",
        "/system/system-options", "/system/document-folders",
        "/administration/km-metadata", "/administration/km-queue", "/administration/accounts"
    ];

    [Fact]
    public async Task CurrentMenuHasReviewedContentInSidebarOrderAndNoRetiredChapters()
    {
        var menu = new CurrentMenu();
        var auth = new TestAuthorization();
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        var groups = await SidebarCatalog.LoadAsync(menu, auth, admin);
        var entries = groups.SelectMany(g => g.Entries).Where(e => SidebarCatalog.IsOperationalRoute(e.Route)).ToList();
        Assert.Equal(ExpectedInternalRoutes.Where(x => x != "/my-submissions"), entries.Select(x => x.Route));
        Assert.All(entries, e => Assert.True(ManualContent.Internal.ContainsKey(e.Route), e.Route));
        Assert.Equal("驗證測試申請單", entries.Single(x => x.Route == "/validation-requests").Title);
        Assert.Equal("設備預約管理", entries.Single(x => x.Route == "/staff/reservations").Title);
        Assert.DoesNotContain(entries, x => x.Title is "專案管理" or "認證管理" or "貨品管理");
        Assert.DoesNotContain(entries, x => x.Route == "/system/menu-management");

        var staff = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        var staffGroups = await SidebarCatalog.LoadAsync(menu, auth, staff);
        var staffRoutes = staffGroups.SelectMany(x => x.Entries).Where(e => SidebarCatalog.IsOperationalRoute(e.Route)).Select(x => x.Route).ToArray();
        Assert.Equal(new[] { "/my-submissions", "/validation-requests", "/validation-request-applications", "/assets/equipment", "/staff/reservations", "/documents", "/vendors" }, staffRoutes);
        Assert.Equal(ExpectedInternalRoutes.Order(), ManualContent.Internal.Keys.Order());
    }

    [Fact]
    public void RdCatalogMatchesSidebarAndNeverReferencesInternalAssets()
    {
        Assert.Equal(new[] { "首頁", "系統驗證申請單", "設備預約" }, SidebarCatalog.RdServices.Entries.Select(x => x.Title));
        Assert.Equal(SidebarCatalog.RdServices.Entries.Select(x => x.Route), ManualContent.Rd.Keys);
        Assert.All(ManualContent.Rd.Values.SelectMany(x => x.Steps), step =>
            Assert.True(step.Image is null || step.Image.StartsWith("rd/", StringComparison.Ordinal), step.Image));
    }

    [Fact]
    public void BothManualPagesRetainServerAuthorizationPolicies()
    {
        var staff = Assert.Single(typeof(SystemManual).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
        var rd = Assert.Single(typeof(RdSystemManual).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
        Assert.Equal(SystemAuthorization.Policies.CsitStaff, staff.Policy);
        Assert.Equal(SystemAuthorization.Policies.RdApplicant, rd.Policy);
    }

    [Fact]
    public void EveryScreenshotIsReferencedByTheCorrectManualAndExistsAsPng()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../ManualAssets"));
        var references = ManualContent.Internal.Values.SelectMany(x => x.Steps).Select(x => x.Image)
            .Concat(ManualContent.Rd.Values.SelectMany(x => x.Steps).Select(x => x.Image))
            .Where(x => x is not null).Cast<string>().Append("internal/identity-login.png")
            .Append("shared/signed-out.png").Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(files, references);
        foreach (var path in references)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(bytes.Length > 1000, path);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8));
        }
    }

    private sealed class CurrentMenu : IMenuManagementService
    {
        private static readonly Guid Department = Guid.Parse("03592515-18b5-492b-b4c8-759f4450b257");
        private static readonly Guid Administration = Guid.Parse("87899b34-a83a-40c6-af79-97b2dd35f13f");
        private static readonly Guid Help = Guid.Parse("e2d0060a-2c84-45a2-8ec3-c6da02157035");
        public Task<List<MenuSectionDto>> GetSectionsAsync() => Task.FromResult(new List<MenuSectionDto>
        {
            new() { Id = Department, Code = "department", Name = "Department", SortOrder = 1, IsEnabled = true },
            new() { Id = Administration, Code = "administration", Name = "Administration", SortOrder = 2, IsEnabled = true },
            new() { Id = Help, Code = "SYSTEM_HELP", Name = "SYSTEM HELP", SortOrder = 999, IsEnabled = true }
        });
        public Task<List<MenuItemDto>> GetItemsBySectionAsync(Guid sectionId) => Task.FromResult(sectionId == Department
            ? new List<MenuItemDto>
            {
                Item(Department, "validation_requests", "驗證測試申請單", "/validation-requests", -9),
                Item(Department, "validation_request_applications", "驗證申請專區", "/validation-request-applications", -8),
                Item(Department, "verification", "專案管理", "/modules/verification", 1, false),
                Item(Department, "certification", "認證管理", "/modules/certification", 2, false),
                Item(Department, "equipment", "設備管理", "/assets/equipment", 3),
                Item(Department, "goods", "貨品管理", "/assets/goods", 4, false),
                Item(Department, "reservation-review", "設備預約管理", "/reservation-review", 5),
                Item(Department, "document_library", "文件專區", "/documents", 15),
                Item(Department, "vendors", "廠商資訊管理", "/vendors", 25)
            }
            : sectionId == Administration
            ? new List<MenuItemDto>
            {
                Item(Administration, "validation_request_settings", "驗證申請單設定", "/validation-request-settings", -9, true, true),
                Item(Administration, "menu-management", "選單管理", "/system/menu-management", 1, true, true),
                Item(Administration, "system-options", "參數管理", "/system/system-options", 2, true, true),
                Item(Administration, "document_folder_management", "文件資料夾管理", "/system/document-folders", 12, true, true)
            }
            : new List<MenuItemDto> { Item(Help, "MANUAL", "操作手冊", "/help/manual", 1) });
        private static MenuItemDto Item(Guid section, string code, string title, string route, int order, bool enabled = true, bool adminOnly = false) =>
            new() { SectionId = section, Code = code, Title = title, RoutePath = route, SortOrder = order, IsEnabled = enabled, AdminOnly = adminOnly };
        public Task<MenuItemDto?> GetItemByIdAsync(Guid id) => Task.FromResult<MenuItemDto?>(null);
        public Task<Guid> CreateItemAsync(Guid sectionId, MenuItemUpsertRequest request) => Task.FromResult(Guid.Empty);
        public Task<bool> UpdateItemAsync(Guid id, MenuItemUpsertRequest request) => Task.FromResult(false);
        public Task<bool> DeleteItemAsync(Guid id) => Task.FromResult(false);
    }

    private sealed class TestAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(user.IsInRole("Admin") ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(user.IsInRole("Admin") ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}

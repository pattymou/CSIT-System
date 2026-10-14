using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIT.DepartmentSystem.Web.Components.Layout;
using SIT.DepartmentSystem.Web.Components.Pages;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace ApprovalFramework.Tests;

public sealed class SidebarVisibilityTests
{
    [Fact]
    public void MySubmissionsKeepsLegacyRoute()
    {
        var routes = typeof(MySubmissions).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Select(x => x.Template).ToArray();
        Assert.Contains("/my-submissions", routes);
        Assert.Contains("/my-rejected-submissions", routes);
    }

    [Theory]
    [InlineData("Engineer", false, false)]
    [InlineData("Team Leader", false, true)]
    [InlineData("Admin", true, true)]
    public async Task NavigationFollowsAdministrationPolicy(
        string identity, bool hasAdminRole, bool administrationAuthorized)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, identity) };
        if (hasAdminRole)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMenuManagementService, EmptyMenuService>();
        services.AddSingleton<AuthenticationStateProvider>(new FixedAuthenticationStateProvider(user));
        services.AddSingleton<IAuthorizationService>(new FixedAuthorizationService(administrationAuthorized));
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<DynamicSidebar>()).ToHtmlString());

        Assert.Equal(administrationAuthorized, html.Contains("href=\"/supervisor-review\""));
        Assert.Equal(!administrationAuthorized, html.Contains("href=\"/my-submissions\""));
        Assert.Equal(!administrationAuthorized, html.Contains("我的項目"));
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FixedAuthorizationService(bool administrationAuthorized) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == SystemAuthorization.Policies.Administration && administrationAuthorized
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());
    }

    private sealed class EmptyMenuService : IMenuManagementService
    {
        public Task<List<MenuSectionDto>> GetSectionsAsync() => Task.FromResult(new List<MenuSectionDto>());
        public Task<List<MenuItemDto>> GetItemsBySectionAsync(Guid sectionId) =>
            Task.FromResult(new List<MenuItemDto>());
        public Task<MenuItemDto?> GetItemByIdAsync(Guid id) => Task.FromResult<MenuItemDto?>(null);
        public Task<Guid> CreateItemAsync(Guid sectionId, MenuItemUpsertRequest request) =>
            throw new NotSupportedException();
        public Task<bool> UpdateItemAsync(Guid id, MenuItemUpsertRequest request) =>
            throw new NotSupportedException();
        public Task<bool> DeleteItemAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost:7296/", "https://localhost:7296/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}

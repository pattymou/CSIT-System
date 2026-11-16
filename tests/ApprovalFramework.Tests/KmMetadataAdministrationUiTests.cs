using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class KmMetadataAdministrationUiTests
{
    [Fact]
    public void DedicatedController_IsAdminOnlyAndReadOnly()
    {
        var authorize = typeof(KmMetadataAdministrationController)
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(SystemAuthorization.Policies.AdminOnly, authorize?.Policy);
        Assert.DoesNotContain(typeof(KmMetadataAdministrationController).GetMethods(), method =>
            method.GetCustomAttributes<HttpPostAttribute>().Any() ||
            method.GetCustomAttributes<HttpPutAttribute>().Any() ||
            method.GetCustomAttributes<HttpDeleteAttribute>().Any());
    }

    [Fact]
    public void PageUsesAdminPolicyPagedApiAllSectionsAndCopy()
    {
        var page = Read("Components", "Pages", "System", "KmMetadata.razor");
        var program = Read("Program.cs");
        var sidebar = Read("Components", "Layout", "DynamicSidebar.razor");

        Assert.Contains("@page \"/administration/km-metadata\"", page);
        Assert.Contains("SystemAuthorization.Policies.AdminOnly", page);
        Assert.Contains("/api/administration/km-metadata/results?", page);
        Assert.Contains("pageSize={PageSize}", page);
        foreach (var section in new[] { "Document", "Technical", "Extraction", "Indexing", "SourceSpecific", "Warnings" })
            Assert.Contains($"(\"{section}\",", page);
        Assert.Contains("navigator.clipboard.writeText", page);
        Assert.Contains("RequireRole(\"Admin\")", program);
        Assert.Contains("RoutePath = \"/administration/km-metadata\"", sidebar);
        Assert.Contains("AdminOnly = true", sidebar);
    }

    [Fact]
    public void GeneralPagesHideTechnicalMetadataButEquipmentKeepsSuggestions()
    {
        var documents = Read("Components", "Pages", "DocumentLibrary.razor");
        var equipment = Read("Components", "Shared", "EquipmentMetadataSummary.razor");

        Assert.DoesNotContain("查看 Metadata", documents);
        Assert.DoesNotContain("ShowMetadataAsync", documents);
        Assert.DoesNotContain("查看完整 Metadata", equipment);
        Assert.DoesNotContain("原始 JSON", equipment);
        Assert.Contains("文件摘要", equipment);
        Assert.Contains("設備規格建議", equipment);
        Assert.Contains("設備能力與限制", equipment);
        Assert.Contains("EquipmentKmMetadataProjection.Project", equipment);
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}

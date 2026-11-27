using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    public void PageUsesAdminPolicyPagedApiReadableSectionsAndCollapsedRawCopy()
    {
        var page = Read("Components", "Pages", "System", "KmMetadata.razor");
        var program = Read("Program.cs");
        var sidebar = Read("Components", "Layout", "DynamicSidebar.razor");
        var catalog = Read("Services", "Manuals", "SidebarCatalog.cs");

        Assert.Contains("@page \"/administration/km-metadata\"", page);
        Assert.Contains("SystemAuthorization.Policies.AdminOnly", page);
        Assert.Contains("/api/administration/km-metadata/results?", page);
        Assert.Contains("pageSize={PageSize}", page);
        foreach (var section in new[] { "文件摘要", "技術資訊", "規格與量測", "測試能力與限制", "分析資訊", "分析警告" })
            Assert.Contains(section, page);
        Assert.Contains("EquipmentKmMetadataProjection.Project", page);
        Assert.Contains("JavaScriptEncoder.UnsafeRelaxedJsonEscaping", page);
        Assert.Contains("<details class=\"kmm-raw\">", page);
        Assert.DoesNotContain("<details class=\"kmm-raw\" open>", page);
        Assert.Contains("navigator.clipboard.writeText", page);
        Assert.Contains("RequireRole(\"Admin\")", program);
        Assert.Contains("SidebarCatalog.LoadAsync", sidebar);
        Assert.Contains("RoutePath = \"/administration/km-metadata\"", catalog);
        Assert.Contains("AdminOnly = true", catalog);
    }

    [Fact]
    public void RawMetadataJsonDisplaysEscapedChineseAsReadableText()
    {
        using var source = JsonDocument.Parse("{\"summary\":\"\\u4e2d\\u6587\"}");
        var displayed = JsonSerializer.Serialize(source.RootElement,
            new JsonSerializerOptions { WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        Assert.Contains("中文", displayed);
        Assert.DoesNotContain("\\u4e2d", displayed);
    }

    [Fact]
    public void GeneralPagesHideTechnicalMetadataButEquipmentShowsAggregateAnalysis()
    {
        var documents = Read("Components", "Pages", "DocumentLibrary.razor");
        var equipment = Read("Components", "Shared", "EquipmentMetadataSummary.razor");

        Assert.DoesNotContain("查看 Metadata", documents);
        Assert.DoesNotContain("ShowMetadataAsync", documents);
        Assert.DoesNotContain("查看完整 Metadata", equipment);
        Assert.DoesNotContain("原始 JSON", equipment);
        Assert.Contains("設備綜合分析", equipment);
        Assert.Contains("設備概述", equipment);
        Assert.Contains("技術規格", equipment);
        Assert.Contains("測試能力", equipment);
        Assert.Contains("來源證據", equipment);
        Assert.Contains("/api/equipment-aggregate-analysis?", equipment);
        Assert.DoesNotContain("EquipmentKmMetadataProjection.Project", equipment);
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

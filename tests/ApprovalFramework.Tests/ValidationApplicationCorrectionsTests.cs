using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

public sealed class ValidationApplicationCorrectionsTests
{
    [Fact]
    public void SensitiveTextIsVisibleToAuthorizedViewsAndDateFieldsUseThePickerControl()
    {
        var field = Read("Components", "Shared", "ValidationRequestFieldPreview.razor");
        Assert.Contains("case \"SensitiveText\":", field);
        Assert.Contains("<input type=\"text\" value=\"@TextValue\"", field);
        Assert.DoesNotContain("<input type=\"password\"", field);

        var picker = Read("Components", "Shared", "DatePickerInput.razor");
        Assert.Contains("type=\"date\"", picker);
        Assert.Contains("app-date-picker-button", picker);
        Assert.Contains("ShowDateTimePickerAsync", picker);

        Assert.Contains("<DatePickerInput", Read("Components", "Pages", "ValidationRequests.razor"));
        Assert.Contains("<DatePickerInput", Read("Components", "Pages", "VerificationApplicationDetail.razor"));
        Assert.Contains("<DatePickerInput", Read("Components", "Pages", "VerificationReviewDetail.razor"));
    }

    [Fact]
    public void DepartmentAndLocationUiUseExternalOptionsAndDefaultNewRequestsToTaipei()
    {
        var form = Read("Components", "Pages", "ValidationRequests.razor");
        var field = Read("Components", "Shared", "ValidationRequestFieldPreview.razor");

        Assert.Contains("GetEnabledByCategoryAsync(SystemOptionCategories.Department)).ToList()", form);
        Assert.DoesNotContain("DepartmentFamilyMatcher.IsDa40Family", form);
        Assert.Contains("_values[\"LOCATION\"] = [\"TAIPEI\"]", form);
        Assert.Contains("string.Equals(value, \"WUJIANG\"", field);
        Assert.Contains("disabled=\"@IsUnavailableLocation(option.ValueCode)\"", field);
    }

    [Fact]
    public async Task DynamicRoutingSeparatesExternalDepartmentFromTaipeiInternalRoutingAndProductTeam()
    {
        await using var db = NewDb();
        var taipeiId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var productTeamId = Guid.NewGuid();
        db.SystemOptions.AddRange(
            Option(taipeiId, SystemOptionCategories.Department, "DA40"),
            Option(externalId, SystemOptionCategories.Department, "EXT-DEPARTMENT"),
            Option(Guid.NewGuid(), SystemOptionCategories.Department, "DA0400-WJ"),
            Option(productTeamId, SystemOptionCategories.Team, "WJ Team"));
        db.ValidationProductTypes.Add(new ValidationProductType
        {
            Id = Guid.NewGuid(), Code = "PRODUCT-A", DisplayLabel = "Product A", IsEnabled = true,
            TeamOptionId = productTeamId
        });
        await db.SaveChangesAsync();

        var result = await ResolveDynamicRoutingAsync(db, Submission("TAIPEI", "EXT-DEPARTMENT"));

        Assert.Equal(taipeiId, Property<Guid?>(result, "DepartmentOptionId"));
        Assert.Equal(productTeamId, Property<Guid>(result, "TeamOptionId"));
    }

    [Fact]
    public async Task DynamicRoutingRejectsWujiangAndAmbiguousTaipeiRouting()
    {
        await using var db = NewDb();
        var productTeamId = Guid.NewGuid();
        db.SystemOptions.AddRange(
            Option(Guid.NewGuid(), SystemOptionCategories.Department, "DA40"),
            Option(Guid.NewGuid(), SystemOptionCategories.Department, "DA0400"),
            Option(Guid.NewGuid(), SystemOptionCategories.Department, "EXT-DEPARTMENT"),
            Option(productTeamId, SystemOptionCategories.Team, "Team A"));
        db.ValidationProductTypes.Add(new ValidationProductType
        {
            Id = Guid.NewGuid(), Code = "PRODUCT-A", DisplayLabel = "Product A", IsEnabled = true,
            TeamOptionId = productTeamId
        });
        await db.SaveChangesAsync();

        var wujiang = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveDynamicRoutingAsync(db, Submission("WUJIANG", "EXT-DEPARTMENT")));
        Assert.Contains("TAIPEI", wujiang.Message, StringComparison.OrdinalIgnoreCase);

        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveDynamicRoutingAsync(db, Submission("TAIPEI", "EXT-DEPARTMENT")));
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewAuditRedactsSensitiveFieldAndParameterValues()
    {
        var versionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var sensitiveField = new ValidationFieldDto(
            Guid.NewGuid(), sectionId, null, "DEFAULT_PASSWORD", "Default Password", null, null,
            "SensitiveText", true, true, 10, true, null, null, [], [], []);
        var sensitiveParameter = new ValidationParameterDto(
            Guid.NewGuid(), null, "LOGIN_PASSWORD", "Login Password", null, null,
            "SensitiveText", true, true, 10, null, [], []);
        var testItem = new ValidationTestItemDto(
            Guid.NewGuid(), categoryId, "LOGIN_TEST", "Login", null, null, false, true, false, 10,
            [sensitiveParameter]);
        var request = new ValidationRequestSubmissionDto
        {
            TemplateVersionId = versionId,
            TemplateVersion = 1,
            ProductTypeCode = "PRODUCT-A",
            FieldValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DEFAULT_PASSWORD"] = ["device-secret"],
                ["MODEL_NAME"] = ["MODEL-A"]
            },
            SelectedTestItemCodes = new(StringComparer.OrdinalIgnoreCase) { "LOGIN_TEST" },
            TestParameterValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["LOGIN_TEST"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["LOGIN_PASSWORD"] = ["parameter-secret"]
                }
            },
            FormSnapshot = new ValidationRequestPreviewDto(
                "VALIDATION_TEST_REQUEST", versionId, 1, "PRODUCT-A", "Product A",
                [new ValidationSectionDto(sectionId, "COMMON", "Common", null, true, true, 10, [sensitiveField])],
                [new ValidationTestCategoryDto(categoryId, "TESTS", "Tests", true, true, 10, [testItem])])
        };
        var method = typeof(VerificationApplicationService).GetMethod(
            "AuditContent", BindingFlags.NonPublic | BindingFlags.Static)!;

        var json = JsonSerializer.Serialize(method.Invoke(null, [Guid.NewGuid(), Guid.NewGuid(), request]));

        Assert.DoesNotContain("device-secret", json);
        Assert.DoesNotContain("parameter-secret", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("MODEL-A", json);
    }

    private static ValidationRequestSubmissionDto Submission(string location, string department) => new()
    {
        ProductTypeCode = "PRODUCT-A",
        FieldValues = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCATION"] = [location],
            ["DEPARTMENT_CODE"] = [department]
        }
    };

    private static async Task<object> ResolveDynamicRoutingAsync(
        AppDbContext db,
        ValidationRequestSubmissionDto submission)
    {
        var service = new VerificationApplicationService(db, null!, null!, null!, null!,
            Options.Create(new DocumentStagingOptions()), null!, null!, null!);
        var method = typeof(VerificationApplicationService).GetMethod(
            "ResolveDynamicRoutingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            [typeof(ValidationRequestSubmissionDto), typeof(bool), typeof(CancellationToken)],
            null)!;
        var task = (Task)method.Invoke(service, [submission, true, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static T Property<T>(object value, string propertyName) =>
        (T)value.GetType().GetProperty(propertyName)!.GetValue(value)!;

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static SystemOption Option(Guid id, string category, string value) => new()
    {
        Id = id,
        Category = category,
        Name = value,
        Value = value,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

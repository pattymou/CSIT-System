using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Implementations;
using System.Reflection;

namespace ApprovalFramework.Tests;

public sealed class DynamicValidationWorkflowAdjustmentTests
{
    [Fact]
    public void DynamicApplication_SubmitsWithoutLegacyRequestedDateOrRequirement()
    {
        var now = DateTime.UtcNow;
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var entity = VerificationApplication.CreateDraft(
            Guid.NewGuid(), "VA-DYNAMIC", departmentId, teamId,
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "MODEL-001",
                ProductModel = "MODEL-001",
                Customer = "CUSTOMER-001"
            }, now);
        entity.SetValidationRequest(Guid.NewGuid(), 2, "WIFI_AP", "{}", now);

        entity.Submit(new VerificationApplicationRouting(
            departmentId, "DA40", "DA40", teamId, "WIFI", "Wi-Fi Team",
            "VERIFICATION_APPLICATION", "leader", "Leader"), now.AddMinutes(1));

        Assert.Equal(VerificationApplicationStatus.Submitted, entity.Status);
        Assert.Null(entity.RequestedFinishDate);
        Assert.Null(entity.ValidationRequirement);
    }

    [Fact]
    public void Accept_SavesSupervisorExpectedEndDateOnSameApplication()
    {
        var now = DateTime.UtcNow;
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var entity = VerificationApplication.CreateDraft(
            Guid.NewGuid(), "VA-EXPECTED", departmentId, teamId,
            "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "MODEL-002", ProductModel = "MODEL-002", Customer = "CUSTOMER-002",
                RequestedFinishDate = new DateOnly(2026, 11, 1), ValidationRequirement = "Legacy requirement"
            }, now);
        entity.Submit(new VerificationApplicationRouting(
            departmentId, "DA40", "DA40", teamId, "WIFI", "Wi-Fi Team",
            "VERIFICATION_APPLICATION", "leader", "Leader"), now.AddMinutes(1));

        var expectedEndDate = new DateOnly(2026, 12, 31);
        entity.Accept(Guid.NewGuid(), expectedEndDate, "leader", now.AddMinutes(2));

        Assert.Equal(VerificationApplicationStatus.Accepted, entity.Status);
        Assert.Equal(expectedEndDate, entity.ExpectedEndDate);
    }

    [Fact]
    public void ServerMapping_UsesDynamicModelCustomerAndSelectedTestsWithoutLegacyDefaults()
    {
        var versionId = Guid.NewGuid();
        var test = new ValidationTestItemDto(Guid.NewGuid(), Guid.NewGuid(), "WIFI_RVR", "RVR", null, null,
            false, true, false, 10,
            [new ValidationParameterDto(Guid.NewGuid(), null, "BAND", "Band", null, null, "Text", true, true, 10, null, [], [])]);
        var submission = new ValidationRequestSubmissionDto
        {
            TemplateVersionId = versionId,
            TemplateVersion = 2,
            ProductTypeCode = "WIFI_AP",
            FieldValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["MODEL_NAME"] = ["MODEL-DYNAMIC"],
                ["CUSTOMER"] = ["CUSTOMER-DYNAMIC"],
                ["PU_CODE"] = ["1A"]
            },
            SelectedTestItemCodes = new(StringComparer.OrdinalIgnoreCase) { "WIFI_RVR" },
            TestParameterValues = new(StringComparer.OrdinalIgnoreCase)
            {
                ["WIFI_RVR"] = new(StringComparer.OrdinalIgnoreCase) { ["BAND"] = ["5G"] }
            },
            FormSnapshot = new("VALIDATION_TEST_REQUEST", versionId, 2, "WIFI_AP", "Wi-Fi AP", [],
                [new ValidationTestCategoryDto(Guid.NewGuid(), "B", "Wi-Fi", true, true, 10, [test])])
        };
        var request = new CreateVerificationApplicationRequest
        {
            ProjectName = "legacy-project",
            Customer = "legacy-customer",
            RequestedFinishDate = new DateOnly(2099, 1, 1),
            ValidationRequirement = "legacy-requirement",
            ValidationRequest = submission
        };
        var method = typeof(VerificationApplicationService).GetMethod("MapContent", BindingFlags.NonPublic | BindingFlags.Static)!;

        var content = Assert.IsType<VerificationApplicationContent>(method.Invoke(null, [request]));

        Assert.Equal("MODEL-DYNAMIC", content.ProjectName);
        Assert.Equal("MODEL-DYNAMIC", content.ProductModel);
        Assert.Equal("CUSTOMER-DYNAMIC", content.Customer);
        Assert.Equal("1A", content.SubPu);
        Assert.Null(content.RequestedFinishDate);
        Assert.Equal("RVR (Band=5G)", content.ValidationRequirement);
    }

    [Fact]
    public void NewFormAndReviewUi_UseDynamicFieldsAndSupervisorDate()
    {
        var form = Read("Components", "Pages", "ValidationRequests.razor");
        Assert.DoesNotContain("workflow-fields", form);
        Assert.DoesNotContain("_form.TeamOptionId", form);
        Assert.DoesNotContain("_form.RequestedFinishDate", form);

        var field = Read("Components", "Shared", "ValidationRequestFieldPreview.razor");
        Assert.Contains("DEPARTMENT_CODE", field);
        Assert.Contains("DepartmentOptions", field);

        var settings = Read("Components", "Pages", "System", "ValidationRequestSettings.razor");
        Assert.Contains("對應 Team", settings);
        Assert.Contains("_productTeamOptionId", settings);

        var review = Read("Components", "Pages", "VerificationReviewDetail.razor");
        Assert.Contains("預計結束日期（同意必填）", review);
        Assert.Contains("_expectedEndDate.Value", review);
    }

    [Fact]
    public void CsitDynamicDetail_RemovesLegacySummaryButKeepsLegacyFallback()
    {
        var view = Read("Components", "Pages", "ValidationRequestApplicationView.razor");
        var dynamicPosition = view.IndexOf("_item.ValidationRequest is not null", StringComparison.Ordinal);
        var summaryPosition = view.IndexOf("class=\"detail-grid\"", StringComparison.Ordinal);
        Assert.True(dynamicPosition >= 0 && summaryPosition > dynamicPosition);
        Assert.Contains("此為舊版申請", view);
    }

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

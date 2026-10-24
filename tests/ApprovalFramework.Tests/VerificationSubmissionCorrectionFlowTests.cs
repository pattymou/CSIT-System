using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class VerificationSubmissionCorrectionFlowTests
{
    [Fact]
    public void IncompleteSubmissionCarriesExactAreaAndFieldMetadata()
    {
        var issue = new VerificationApplicationValidationIssue("TYPE_WIFI", "Wi-Fi", "MAX_BW_24", "2.4 GHz");

        var error = new VerificationApplicationValidationException([issue]);

        Assert.Equal("新版申請單尚未完整填寫：2.4 GHz", error.Message);
        Assert.Equal(issue, Assert.Single(error.Issues));
    }

    [Fact]
    public void DetailRedirectsSameDraftToEditorAndEditorKeepsFormVisibleWithGuidance()
    {
        var detail = Read("Components", "Pages", "VerificationApplicationDetail.razor");
        var editor = Read("Components", "Pages", "ValidationRequests.razor");
        var preview = Read("Components", "Shared", "ValidationRequestPreview.razor");
        var field = Read("Components", "Shared", "ValidationRequestFieldPreview.razor");

        Assert.Contains("catch (VerificationApplicationValidationException ex)", detail);
        Assert.Contains("$\"/validation-requests/{Id}?validationArea=", detail);
        Assert.Contains("validationField=", detail);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(_validationGuidance))", editor);
        Assert.Contains("RequestedActiveTab=\"ValidationArea\" ActiveTabRequestVersion=\"_validationFocusRevision\"", editor);
        Assert.Contains("正式送出未完成，已返回原草稿", editor);
        Assert.Contains("csitScrollIntoView", editor);
        Assert.Contains("_activeTab = RequestedActiveTab", preview);
        Assert.Contains("validation-field-focus", field);
    }

    [Fact]
    public void ServerValidationMapsEveryIssueToItsExistingSnapshotAreaWithoutSkippingRules()
    {
        var service = Read("Services", "Implementations", "VerificationApplicationService.cs");

        Assert.Contains("section.Code, section.DisplayLabel, field.Code, field.DisplayLabel", service);
        Assert.Contains("VerificationApplicationValidationIssue.TestItemsAreaCode", service);
        Assert.Contains("if (issues.Count > 0) throw new VerificationApplicationValidationException(issues);", service);
        Assert.Contains("IsVisible(field, request.FieldValues) && IsRequired(field, request.FieldValues)", service);
    }

    private static string Read(params string[] parts)
    {
        var root = FindRoot();
        return File.ReadAllText(Path.Combine([root, .. parts]));
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

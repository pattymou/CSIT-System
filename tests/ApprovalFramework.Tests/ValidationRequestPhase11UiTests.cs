using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.ValidationRequests;

namespace ApprovalFramework.Tests;

public sealed class ValidationRequestPhase11UiTests
{
    [Fact]
    public void AdministrationUsesFourUserFacingPrimaryTabs()
    {
        var source = Read("Components", "Pages", "System", "ValidationRequestSettings.razor");
        Assert.Contains("private static readonly string[] Tabs = [\"產品類型\", \"表單欄位\", \"測試項目\", \"表單預覽\"]", source);
        Assert.DoesNotContain("private static readonly string[] Tabs = [\"Template Version\"", source);
        Assert.DoesNotContain("Product Type → Section Mapping", source);
        Assert.DoesNotContain("Fields / Options / Conditions", source);
    }

    [Theory]
    [InlineData("Text", "文字")]
    [InlineData("TextArea", "多行文字")]
    [InlineData("SensitiveText", "密碼/敏感文字")]
    [InlineData("Number", "數字")]
    [InlineData("Dropdown", "下拉選單")]
    [InlineData("SingleSelect", "單選")]
    [InlineData("Checkbox", "勾選")]
    [InlineData("MultiSelect", "複選")]
    [InlineData("Attachment", "附件")]
    [InlineData("Group", "欄位群組")]
    [InlineData("RepeatableText", "可新增多筆文字")]
    [InlineData("RepeatableGroup", "可新增多組資料")]
    [InlineData("Lookup", "系統資料選擇")]
    public void FieldTypesHaveUserFacingChineseLabels(string fieldType, string expected)
        => Assert.Equal(expected, ValidationRequestPresentation.FieldType(fieldType));

    [Fact]
    public void CommonFieldsArePresentedInFourSemanticGroups()
    {
        var fields = new[]
        {
            Field("PROJECT", 10), Field("VERSION", 120), Field("OTHER", 160), Field("LOGIN", 170)
        };
        var section = new ValidationSectionDto(Guid.NewGuid(), "COMMON_PRODUCT_INFO", "共通產品資訊", null, true, true, 10, fields);
        var groups = ValidationRequestPresentation.Groups(section);
        Assert.Equal(["A. 產品專案資訊", "B. 產品軟硬體版本資訊", "C. 其他資訊", "D. 產品登入資訊"], groups.Select(x => x.Label));
        Assert.All(groups, x => Assert.Single(x.Fields));
    }

    [Fact]
    public void DepartmentPageUsesProductFieldWithoutDuplicateTopSelectorOrTemplateDebugText()
    {
        var source = Read("Components", "Pages", "ValidationRequests.razor");
        Assert.DoesNotContain("id=\"validation-product\"", source);
        Assert.DoesNotContain("Template V", source);
        Assert.DoesNotContain("Phase 1 表單預覽", source);
        Assert.DoesNotContain("DEV 預覽模式", source);
        Assert.DoesNotContain("尚未開放儲存與送出", source);
        var fieldSource = Read("Components", "Shared", "ValidationRequestFieldPreview.razor");
        Assert.Contains("Field.DataSource == \"ValidationProductType\"", fieldSource);
        Assert.Contains("ValidationApplicationAttachments", fieldSource);
        Assert.DoesNotContain("附件上傳功能將於下一階段啟用", fieldSource);
    }

    [Fact]
    public void PreviewNavigationIsBuiltFromMappedSectionsAndCategories()
    {
        var source = Read("Components", "Shared", "ValidationRequestPreview.razor");
        Assert.Contains("@foreach (var tabSection in Preview.Sections)", source);
        Assert.Contains("@foreach (var category in Preview.TestCategories)", source);
        Assert.DoesNotContain("Wi-Fi AP", source);
        Assert.DoesNotContain("5G FWA", source);
        Assert.DoesNotContain("SmallCell", source);
    }

    [Fact]
    public void ResponsiveLayoutsCollapseToOneColumn()
    {
        var previewCss = Read("Components", "Shared", "ValidationRequestPreview.razor.css");
        var departmentCss = Read("Components", "Pages", "ValidationRequests.razor.css");
        Assert.Contains("@media(max-width:760px)", previewCss);
        Assert.Contains(".validation-fields,.validation-group-fields,.validation-test-grid{grid-template-columns:1fr}", previewCss);
        Assert.Contains("@media(max-width:700px)", departmentCss);
    }

    [Fact]
    public void AdministrationUsesReadableMappingsAndActionableEditors()
    {
        var source = Read("Components", "Pages", "System", "ValidationRequestSettings.razor");
        Assert.Contains("class=\"tag-list\"", source);
        Assert.Contains("class=\"mapping-tag category\"", source);
        Assert.Contains("欄位代碼", source);
        Assert.Contains("測試類別", source);
        Assert.Contains("data-testid=\"edit-product-@p.Code\"", source);
        Assert.Contains("role=\"dialog\"", source);
        Assert.Contains("_productEditor", source);
        Assert.Contains("_fieldEditor", source);
        Assert.Contains("_itemEditor", source);
    }

    [Fact]
    public void PreviewUsesHumanFacingCopyAndNoEngineeringSectionLabel()
    {
        var preview = Read("Components", "Shared", "ValidationRequestPreview.razor");
        Assert.Contains("預覽模式", preview);
        Assert.Contains("申請內容", preview);
        Assert.DoesNotContain("表單區塊</span>", preview);
        Assert.DoesNotContain("Preview only", preview);
    }

    [Fact]
    public void PreviewHasEnterpriseLayoutWithoutHorizontalOverflow()
    {
        var previewCss = Read("Components", "Shared", "ValidationRequestPreview.razor.css");
        var settingsCss = Read("Components", "Pages", "System", "ValidationRequestSettings.razor.css");
        Assert.Contains("min-width:0", previewCss);
        Assert.Contains("overflow:hidden", previewCss);
        Assert.Contains("max-width:270px", settingsCss);
        Assert.Contains("overflow-x:auto", settingsCss);
    }

    private static ValidationFieldDto Field(string code, int order) => new(Guid.NewGuid(), Guid.NewGuid(), null, code, code, null, null, "Text", false, true, order, false, null, null, [], [], []);
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));
    private static string FindRepositoryRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SIT.DepartmentSystem.Web.csproj"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}

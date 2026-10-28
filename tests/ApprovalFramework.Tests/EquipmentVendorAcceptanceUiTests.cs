namespace ApprovalFramework.Tests;

public sealed class EquipmentVendorAcceptanceUiTests
{
    [Fact]
    public void VendorRows_AreKeyboardClickable_AndActionsStopPropagation()
    {
        var source = Read("Components", "Pages", "VendorList.razor");
        var css = Read("Components", "Pages", "VendorList.razor.css");

        Assert.Contains("class=\"vendor-clickable-row\"", source);
        Assert.Contains("role=\"link\" tabindex=\"0\"", source);
        Assert.Contains("OpenFromKeyboard", source);
        Assert.DoesNotContain(">檢視</button>", source);
        Assert.Equal(2, Count(source, "@onclick:stopPropagation=\"true\""));
        Assert.Contains(".vendor-clickable-row:hover", css);
        Assert.Contains("cursor: pointer", css);
    }

    [Fact]
    public void EquipmentView_HasSystemMetadataAndRealPhotoSections_WithoutOwnershipPanel()
    {
        var view = Read("Components", "Pages", "ApparatusView.razor");
        var files = Read("Components", "Shared", "EquipmentFileTable.razor");
        var fileCss = Read("Components", "Shared", "EquipmentFileTable.razor.css");
        var metadata = Read("Components", "Shared", "EquipmentMetadataSummary.razor");

        Assert.DoesNotContain("歸屬／管理設定", view);
        Assert.Contains("B. 系統自動新增／維護", view);
        Assert.Contains("Equipment ID", view);
        Assert.Contains("LifecycleRecords", view);
        Assert.Contains("equipment-photo-gallery", view);
        Assert.Contains("src=\"@photo.ImageUrl\"", view);
        Assert.Contains("href=\"@photo.ImageUrl\"", view);
        Assert.Contains("api/assets/files/{fileId:guid}/image",
            Read("Controllers", "ApparatusController.cs"));
        Assert.Contains("C. AI／KM 根據設備文件擷取建議", metadata);
        Assert.Contains("尚無 KM 分析結果", metadata);
        Assert.Contains("TechnicalSpecifications", metadata);
        Assert.Contains("CapabilitySummary", metadata);
        Assert.Contains("EvidenceText", metadata);
        Assert.Contains(">下載</a>", files);
        Assert.DoesNotContain(">檢視</a>", files);
        Assert.DoesNotContain("既有檔案", files);
        Assert.Contains("歷史附件，入庫狀態待確認", files);
        Assert.Contains("attachment-status", files);
        Assert.Contains(".attachment-status.completed", fileCss);
        Assert.Contains("background:rgba(16,185,129,.16)", fileCss);
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SIT.DepartmentSystem.Web.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

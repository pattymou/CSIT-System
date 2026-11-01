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
    public void VendorAttachments_UseInlineResponsiveMultiFileUploadAction()
    {
        var edit = Read("Components", "Pages", "VendorEdit.razor");
        var css = Read("Components", "Pages", "VendorEdit.razor.css");

        Assert.Contains("<InputFile OnChange=\"OnFilesSelected\" multiple", edit);
        Assert.Contains("AccumulateUploadFilesAsync", edit);
        Assert.Contains("RemoveAccumulatedUploadFileAsync", edit);
        Assert.Contains("UploadAccumulatedFilesAsync", edit);
        Assert.DoesNotContain("GetMultipleFiles(20)", edit);
        Assert.DoesNotContain("Api.UploadFilesAsync", edit);
        Assert.Contains("class=\"upload-selection-actions\"", edit);
        Assert.Contains("@onclick=\"UploadOnlyAsync\"", edit);
        Assert.Contains("@(_saving ? \"\u4e0a\u50b3\u4e2d...\" : \"\u4e0a\u50b3\")", edit);
        Assert.DoesNotContain("\u63d0\u4ea4\u6240\u9078\u9644\u4ef6", edit);
        Assert.Contains("class=\"upload-file-item pending-upload-file\" @key=\"file.Id\"", edit);
        Assert.Contains("RemoveSelectedFileAsync(file.Id)", edit);
        Assert.Contains("class=\"table-chip-btn danger\"", edit);
        Assert.Contains("_selectedFiles.Clear()", edit);
        Assert.Contains("justify-content: flex-end", css);
        Assert.Contains("flex-wrap: wrap", css);
        Assert.Contains(".pending-upload-actions", css);
        Assert.Contains("@media (max-width: 600px)", css);
    }

    [Fact]
    public void EquipmentView_HasCompactTimestampsKmMetadataAndRealPhotoSections_WithoutDuplicateSystemPanel()
    {
        var view = Read("Components", "Pages", "ApparatusView.razor");
        var files = Read("Components", "Shared", "EquipmentFileTable.razor");
        var fileCss = Read("Components", "Shared", "EquipmentFileTable.razor.css");
        var metadata = Read("Components", "Shared", "EquipmentMetadataSummary.razor");

        Assert.DoesNotContain("歸屬／管理設定", view);
        Assert.DoesNotContain("B. 系統自動新增／維護", view);
        Assert.DoesNotContain("Equipment ID", view);
        Assert.DoesNotContain("LifecycleRecords", view);
        Assert.Contains("class=\"equipment-record-timestamps\"", view);
        Assert.Contains("建立：@DisplayDateTime(_item.CreatedAt)", view);
        Assert.Contains("更新：@DisplayDateTime(_item.UpdatedAt)", view);
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
        Assert.Contains("<th>KM 分析</th><th>操作</th>", files);
        Assert.Equal(5, Count(files, "<th>"));
        Assert.DoesNotContain("LocalDeletionText(file)", files);
        Assert.Contains("disabled=\"@(!file.CanDelete)\"", files);
        Assert.DoesNotContain("file.DeletionError ?? file.KmError", files);
        Assert.Contains(".attachment-status.completed", fileCss);
        Assert.Contains("background:rgba(16,185,129,.16)", fileCss);
    }

    [Fact]
    public void EquipmentEdit_UsesSingleVendorSelector_AutomaticOwnership_AndScopedViewSpacing()
    {
        var edit = Read("Components", "Pages", "ApparatusEdit.razor");
        var editCss = Read("Components", "Pages", "ApparatusEdit.razor.css");
        var view = Read("Components", "Pages", "ApparatusView.razor");
        var viewCss = Read("Components", "Pages", "ApparatusView.razor.css");
        var service = Read("Services", "Implementations", "ApparatusService.cs");

        Assert.DoesNotContain("既有廠牌文字（Brand）", edit);
        Assert.DoesNotContain("正式廠商主檔 *", edit);
        Assert.Contains("value=\"@VendorSelectionValue\" @onchange=\"OnVendorChanged\"", edit);
        Assert.Contains("原始廠牌：@_form.Brand（尚未連結）", edit);
        Assert.Contains("@bind:after=\"OnCustodianChanged\"", edit);
        Assert.Contains("@if (ShowOwnershipCorrection)", edit);
        Assert.Contains("Shared Identity 正式資料驗證", edit);
        Assert.Contains("@bind=\"_form.ConfirmOwnershipChange\"", edit);
        Assert.Contains(".equipment-ownership-confirm input { width:auto", editCss);
        Assert.Contains("Brand = moduleCode == \"equipment\" ? selectedVendor?.Name : request.Brand", service);
        Assert.Contains("設備部門或 Team 歸屬變更必須明確確認", service);
        Assert.Contains("form-actions apparatus-view-actions", view);
        Assert.Contains("margin-bottom: 20px", viewCss);
        Assert.Contains("@Display(_item.VendorName ?? _item.Brand)", view);
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

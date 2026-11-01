namespace ApprovalFramework.Tests;

public sealed class EquipmentAttachmentSelectionUiTests
{
    [Fact]
    public void EquipmentEdit_UsesSynchronizedAccumulatedPendingFilesAndInlineUploadAction()
    {
        var edit = Read("Components", "Pages", "ApparatusEdit.razor");
        var css = Read("Components", "Pages", "ApparatusEdit.razor.css");

        Assert.Contains("<InputFile OnChange=\"OnFilesSelected\" multiple", edit);
        Assert.Contains("AccumulateUploadFilesAsync", edit);
        Assert.Contains("RemoveAccumulatedUploadFileAsync", edit);
        Assert.Contains("UploadAccumulatedFilesAsync", edit);
        Assert.Contains("\u5f85\u4e0a\u50b3\u9644\u4ef6", edit);
        Assert.Contains("\u5df2\u4e0a\u50b3\u9644\u4ef6", edit);
        Assert.Contains("\u5df2\u9078\u64c7 @_selectedFiles.Count \u500b\u6a94\u6848", edit);
        Assert.Contains("@(_uploading ? \"\u4e0a\u50b3\u4e2d...\" : \"\u4e0a\u50b3\")", edit);
        Assert.Contains("\u5df2\u7565\u904e\u91cd\u8907\u6a94\u540d", edit);
        Assert.Contains("\u5f85\u4e0a\u50b3\u9644\u4ef6\u6700\u591a", edit);
        Assert.Contains("RemoveSelectedFileAsync(f.Id)", edit);
        Assert.DoesNotContain("\u63d0\u4ea4\u6240\u9078\u9644\u4ef6", edit);
        Assert.Contains(".upload-selection-actions", css);
        Assert.Contains("flex-wrap: wrap", css);
    }

    [Fact]
    public void BrowserUploadAccumulator_IsAdditiveAndKeepsLegacyUploadBehavior()
    {
        var api = Read("wwwroot", "js", "browser-api.js");
        var client = Read("Services", "BrowserApiClient.cs");

        Assert.Contains("export async function uploadFiles(url, inputContainer, fileNames = null, clearInput = true)", api);
        Assert.Contains("const accumulatedUploadFiles = new WeakMap()", api);
        Assert.Contains("export function accumulateUploadFiles", api);
        Assert.Contains("state.files.push({ id: createUploadFileId(), file })", api);
        Assert.Contains("state.files = state.files.filter(item => item.id !== fileId)", api);
        Assert.Contains("for (const item of state.files)", api);
        Assert.Contains("if (response.ok && clearOnSuccess) clearAccumulatedUploadFiles(inputContainer)", api);
        Assert.Contains("UploadAccumulatedFilesAsync", client);
        Assert.Contains("RemoveAccumulatedUploadFileAsync", client);
    }

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

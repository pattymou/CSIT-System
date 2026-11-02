namespace ApprovalFramework.Tests;

public sealed class AttachmentTableUiConsistencyTests
{
    [Theory]
    [InlineData("Components/Shared/EquipmentFileTable.razor")]
    [InlineData("Components/Shared/VendorAttachmentTable.razor")]
    [InlineData("Components/Shared/ValidationApplicationAttachments.razor")]
    [InlineData("Components/Shared/OwnApprovalUploadList.razor")]
    [InlineData("Components/Shared/VerificationWorkTaskCard.razor")]
    [InlineData("Components/Pages/DocumentLibrary.razor")]
    [InlineData("Components/Pages/SupervisorReviewCenter.razor")]
    public void AttachmentTables_ShowUploaderAndHideReviewStatus(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("<th>上傳者</th>", source);
        Assert.DoesNotContain("<th>審核狀態</th>", source);
        Assert.DoesNotContain("<th>審核</th>", source);
    }

    [Fact]
    public void UploaderDtos_UseOriginalUploadFields()
    {
        var apparatus = Read("Services", "Implementations", "ApparatusService.cs");
        var verificationWork = Read("Services", "VerificationWorkManagementService.cs");
        var pending = Read("Services", "Approval", "ApprovalContextUploads.cs");

        Assert.Contains("UploadedBy = x.UploadEmp", apparatus);
        Assert.Contains("UploadedBy = file.UploadEmp", verificationWork);
        Assert.Contains("x.SubmittedByAccount, x.SubmittedAt", pending);
    }

    [Fact]
    public void DocumentAndSupervisorTablesUseDesktopWidthAndMobileOverflow()
    {
        var documentCss = Read("Components", "Pages", "DocumentLibrary.razor.css");
        var supervisorCss = Read("Components", "Pages", "SupervisorReviewCenter.razor.css");

        Assert.Contains(".custom-document-table { min-width:0; table-layout:fixed; }", documentCss);
        Assert.Contains(".custom-document-table { min-width:980px; }", documentCss);
        Assert.Contains(".approval-review-layout", supervisorCss);
        Assert.Contains("width: 100%;", supervisorCss);
        Assert.Contains(".approval-review-table { min-width: 920px; }", supervisorCss);
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

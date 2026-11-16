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
        Assert.Contains(".custom-document-table { min-width:880px; }", documentCss);
        Assert.Contains(".approval-review-layout", supervisorCss);
        Assert.Contains("width: 100%;", supervisorCss);
        Assert.Contains(".approval-review-table { min-width: 920px; }", supervisorCss);
    }

    [Fact]
    public void DocumentLibraryCustomFileList_ShowsOnlyOriginalNameAndHidesHash()
    {
        var source = Read("Components", "Pages", "DocumentLibrary.razor");
        var css = Read("Components", "Pages", "DocumentLibrary.razor.css");
        var customTable = source[source.IndexOf("<table class=\"document-table custom-document-table\"", StringComparison.Ordinal)..];

        Assert.Contains("@file.OriginalFileName", customTable);
        Assert.DoesNotContain("@file.StandardFileName", customTable);
        Assert.DoesNotContain("<th>SHA256</th>", customTable);
        Assert.DoesNotContain("@file.Sha256", customTable);
        Assert.DoesNotContain("custom-file-sha-column", customTable);
        Assert.Contains("colspan=\"7\"", customTable);
        Assert.Contains(".custom-document-table .document-file-details strong { white-space:normal; overflow-wrap:anywhere; word-break:break-word; }", css);
    }

    [Fact]
    public void EquipmentActionsKeepNativeTableCellAndUseInnerFlexContainer()
    {
        var table = Read("Components", "Shared", "EquipmentFileTable.razor");
        var css = Read("Components", "Shared", "EquipmentFileTable.razor.css");

        Assert.Contains("<td class=\"action-cell\"><div class=\"action-cell-content\">", table);
        Assert.Contains(".equipment-file-table td.action-cell { display: table-cell; }", css);
        Assert.Contains(".action-cell-content { display: flex;", css);
    }

    [Fact]
    public void DocumentLibraryArchiveRefresh_UsesOneBoundedBatchAndPreservesExistingRows()
    {
        var source = Read("Components", "Pages", "DocumentLibrary.razor");

        Assert.Contains("TimeSpan.FromSeconds(5)", source);
        Assert.Contains("PeriodicTimer(StatusPollingInterval)", source);
        Assert.Contains("/api/document-library/file-statuses", source);
        Assert.Contains("new DocumentArchiveStatusBatchRequest { FileIds = ids.ToList() }", source);
        Assert.Contains("_trackedFileIds.Take(100).ToArray()", source);
        Assert.Contains("ApplyArchiveStatus(status)", source);
        Assert.Contains("custom.KmJobId = status.KmJobId", source);
        Assert.Contains("custom.MetadataAvailable = status.MetadataAvailable", source);
        Assert.Contains("ShouldTrack(x.ArchiveStatus, x.ProcessingStatus)", source);
        Assert.Contains("await CancelStatusTrackingAsync();", source);
        Assert.Contains("@implements IAsyncDisposable", source);
    }

    [Fact]
    public void DocumentLibraryUploadNotices_HaveIndependentCancelableLifetimes()
    {
        var source = Read("Components", "Pages", "DocumentLibrary.razor");

        Assert.Contains("SuccessMessageLifetime = TimeSpan.FromSeconds(6)", source);
        Assert.Contains("SuccessResultLifetime = TimeSpan.FromSeconds(8)", source);
        Assert.Contains("FailureNoticeLifetime = TimeSpan.FromSeconds(15)", source);
        Assert.Contains("FinalProgressLifetime = TimeSpan.FromSeconds(8)", source);
        Assert.Contains("version != _messageVersion", source);
        Assert.Contains("version != _uploadResultsVersion", source);
        Assert.Contains("version != _progressVersion", source);
        Assert.Contains("Task.Delay(delay, cancellationToken)", source);
        Assert.Contains("CancelAndDispose(ref _messageCts)", source);
        Assert.Contains("CancelAndDispose(ref _uploadResultsCts)", source);
        Assert.Contains("CancelAndDispose(ref _progressHideCts)", source);
        Assert.Contains("DismissUploadResult(notice.Id)", source);
        Assert.Contains("UploadTerminalFailedCount > 0", source);
        Assert.Contains("_uploadTrackedIds.Any(_trackedFileIds.Contains)", source);
        Assert.Contains("自動追蹤已暫停。", source);
        Assert.Contains("RefreshTrackedStatusesAsync", source);
    }

    [Fact]
    public void DocumentLibraryMetadata_IsHiddenFromGeneralFileLists()
    {
        var source = Read("Components", "Pages", "DocumentLibrary.razor");

        Assert.DoesNotContain("ShowMetadataAsync", source);
        Assert.DoesNotContain("/api/administration/km-analysis/results", source);
        Assert.DoesNotContain("查看 Metadata", source);
        Assert.DoesNotContain("原始 JSON", source);
        Assert.Contains("AttachmentStatusPill", source);
        Assert.Contains("download", source);
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

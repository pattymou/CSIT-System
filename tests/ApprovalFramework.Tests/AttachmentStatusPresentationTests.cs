using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class AttachmentStatusPresentationTests
{
    [Theory]
    [InlineData(ApprovalStatus.Pending, "待主管審核", "pending")]
    [InlineData(ApprovalStatus.ApprovedPendingCommit, "已核准", "success")]
    [InlineData(ApprovalStatus.CommitFailed, "已核准", "success")]
    [InlineData(ApprovalStatus.Rejected, "已退回", "danger")]
    public void ReviewStatus_UsesUnifiedTextAndTone(ApprovalStatus status, string text, string tone)
    {
        Assert.Equal(new AttachmentStatusValue(text, tone), AttachmentStatusPresentation.Review(status));
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending, "尚未入庫", "neutral")]
    [InlineData(ApprovalStatus.ApprovedPendingCommit, "正式入庫中", "pending")]
    [InlineData(ApprovalStatus.CommitFailed, "寫入失敗", "danger")]
    [InlineData(ApprovalStatus.Committed, "正式入庫完成", "success")]
    public void ArchiveStatus_RemainsSeparateFromReview(ApprovalStatus status, string text, string tone)
    {
        Assert.Equal(new AttachmentStatusValue(text, tone), AttachmentStatusPresentation.Archive(status));
    }

    [Theory]
    [InlineData(false, true, null, "尚未派送", "neutral")]
    [InlineData(true, false, "Pending", "KM 尚未啟用", "neutral")]
    [InlineData(true, false, "Completed", "KM 完成", "success")]
    [InlineData(true, true, "Pending", "待 KM 處理", "pending")]
    [InlineData(true, true, "Processing", "KM 分析中", "pending")]
    [InlineData(true, true, "Completed", "KM 完成", "success")]
    [InlineData(true, true, "Failed", "KM 失敗", "danger")]
    public void KmStatus_UsesUnifiedTextAndTone(bool scheduled, bool enabled, string? status,
        string text, string tone)
    {
        Assert.Equal(new AttachmentStatusValue(text, tone),
            AttachmentStatusPresentation.Km(scheduled, enabled, status));
    }

    [Fact]
    public void SupervisorCenter_ShowsWideUploaderDetailsWithoutReviewColumnOrChangingActions()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Components", "Pages", "SupervisorReviewCenter.razor"));

        Assert.Contains("<th>所屬區域</th><th>主資料名稱</th><th>附件名稱</th><th>上傳者</th><th>上傳時間</th><th>類型</th>", source);
        Assert.DoesNotContain("<th>審核狀態</th>", source);
        Assert.Contains("@foreach (var file in item.Files)", source);
        Assert.Contains("Submissions.ApproveAsync", source);
        Assert.Contains("Submissions.RejectAsync", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}

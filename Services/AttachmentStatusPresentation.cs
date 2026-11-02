using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services;

public sealed record AttachmentStatusValue(string Text, string Tone);

public static class AttachmentStatusPresentation
{
    public static AttachmentStatusValue Review(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => Pending("待主管審核"),
        ApprovalStatus.Rejected => Danger("已退回"),
        ApprovalStatus.ApprovedPendingCommit or ApprovalStatus.CommitFailed or ApprovalStatus.Committed =>
            Success("已核准"),
        _ => Neutral("—")
    };

    public static AttachmentStatusValue Review(VerificationApplicationStatus status) => status switch
    {
        VerificationApplicationStatus.Submitted => Pending("待主管審核"),
        VerificationApplicationStatus.Accepted => Success("已核准"),
        VerificationApplicationStatus.Returned or VerificationApplicationStatus.Rejected => Danger("已退回"),
        _ => Neutral("—")
    };

    public static AttachmentStatusValue Approved() => Success("已核准");
    public static AttachmentStatusValue Exempt() => Success("主管免審");
    public static AttachmentStatusValue UnknownReview() => Neutral("—");

    public static AttachmentStatusValue Archive(ApprovalStatus status) => status switch
    {
        ApprovalStatus.ApprovedPendingCommit => Pending("正式入庫中"),
        ApprovalStatus.CommitFailed => Danger("寫入失敗"),
        ApprovalStatus.Committed => Success("正式入庫完成"),
        _ => Neutral("尚未入庫")
    };

    public static AttachmentStatusValue Archive(string? status) => status switch
    {
        "Archived" or "Completed" => Success("正式入庫完成"),
        "Pending" or "Processing" or "Archiving" => Pending("正式入庫中"),
        "Failed" => Danger("寫入失敗"),
        _ => Neutral("尚未入庫")
    };

    public static AttachmentStatusValue Km(bool hasSchedule, bool enabled, string? status,
        bool excludedByPolicy = false)
    {
        if (excludedByPolicy) return Neutral("KM 尚未啟用");
        if (!hasSchedule || string.IsNullOrWhiteSpace(status))
            return enabled ? Neutral("尚未派送") : Neutral("KM 尚未啟用");
        if (!enabled && status == "Pending") return Neutral("KM 尚未啟用");
        return status switch
        {
            "Pending" or "Dispatching" or "Sent" or "WaitingNotification" or "WaitingAnalysis" or
                "Accepted" => Pending("待 KM 處理"),
            "Processing" => Pending("KM 分析中"),
            "Completed" => Success("KM 完成"),
            "Failed" => Danger("KM 失敗"),
            "Disabled" => Neutral("KM 尚未啟用"),
            _ => Neutral("尚未派送")
        };
    }

    private static AttachmentStatusValue Success(string text) => new(text, "success");
    private static AttachmentStatusValue Pending(string text) => new(text, "pending");
    private static AttachmentStatusValue Danger(string text) => new(text, "danger");
    private static AttachmentStatusValue Neutral(string text) => new(text, "neutral");
}

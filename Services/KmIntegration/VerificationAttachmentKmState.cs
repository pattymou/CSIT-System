using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class VerificationAttachmentKmState
{
    public static void Apply(
        VerificationApplicationFileDto target,
        bool formalArchiveCompleted,
        KmIntegrationOutbox? outbox,
        bool kmEnabled)
    {
        target.KmAnalysisError = outbox?.LastErrorMessage;
        if (!formalArchiveCompleted)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.NotApplicable;
            target.KmAnalysisStatusDisplay = "尚未正式入庫";
            return;
        }

        if (outbox?.Status == KmIntegrationStatus.Completed)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.Completed;
            target.KmAnalysisStatusDisplay = "KM 分析完成";
            return;
        }
        if (outbox?.Status == KmIntegrationStatus.Processing)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.Processing;
            target.KmAnalysisStatusDisplay = "KM 分析處理中";
            return;
        }
        if (outbox?.Status == KmIntegrationStatus.Sent)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.WaitingAnalysis;
            target.KmAnalysisStatusDisplay = "KM 已接收，等待分析";
            return;
        }
        if (outbox?.Status == KmIntegrationStatus.Failed)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.Failed;
            target.KmAnalysisStatusDisplay = "KM 通知／分析失敗";
            return;
        }
        if (!kmEnabled)
        {
            target.KmAnalysisStatus = VerificationAttachmentKmStatuses.Disabled;
            target.KmAnalysisStatusDisplay = "KM 尚未啟用";
            return;
        }

        target.KmAnalysisStatus = VerificationAttachmentKmStatuses.WaitingNotification;
        target.KmAnalysisStatusDisplay = "等待通知 KM";
    }
}

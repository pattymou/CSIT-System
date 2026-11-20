using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Components.Shared;

public static class EquipmentFileDeletionPresentation
{
    public static bool IsActive(string? status) => status is
        EquipmentFileDeletionStatuses.Pending or EquipmentFileDeletionStatuses.Dispatching or
        EquipmentFileDeletionStatuses.Accepted or EquipmentFileDeletionStatuses.Processing or
        EquipmentFileDeletionStatuses.KmVerified or EquipmentFileDeletionStatuses.LocalCleanup;

    public static bool IsFailure(string? status) => status is
        EquipmentFileDeletionStatuses.Failed or EquipmentFileDeletionStatuses.CleanupFailed;

    public static string? Message(ApparatusFileDto file, bool submitting, bool accepted, bool longRunning)
    {
        if (submitting) return "送出刪除要求中…";
        if (file.DeletionStatus == EquipmentFileDeletionStatuses.Failed)
            return "刪除失敗，請稍後重試或聯絡管理員。";
        if (file.DeletionStatus == EquipmentFileDeletionStatuses.CleanupFailed)
            return "本地檔案清理失敗，請聯絡管理員。";
        if (longRunning && (accepted || IsActive(file.DeletionStatus)))
            return "仍在處理，可稍後回來查看。";
        return file.DeletionStatus switch
        {
            EquipmentFileDeletionStatuses.Pending or EquipmentFileDeletionStatuses.Dispatching or
                EquipmentFileDeletionStatuses.Accepted => "刪除要求已送出，正在等待 KM 清理。",
            EquipmentFileDeletionStatuses.Processing => "KM 刪除處理中。",
            EquipmentFileDeletionStatuses.KmVerified or EquipmentFileDeletionStatuses.LocalCleanup =>
                "KM 已確認，正在清理本地檔案。",
            _ => accepted ? "刪除要求已送出，正在等待 KM 清理。" : null
        };
    }

    public static bool DisableDelete(ApparatusFileDto file, bool submitting, bool accepted) =>
        submitting || accepted || !file.CanDelete || IsActive(file.DeletionStatus) ||
        IsFailure(file.DeletionStatus) || file.DeletionStatus == EquipmentFileDeletionStatuses.Completed;
}

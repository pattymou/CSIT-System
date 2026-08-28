using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Components;

public static class ReservationUi
{
    public static string StatusText(ReservationStatus status) => status switch
    {
        ReservationStatus.Draft => "草稿",
        ReservationStatus.Pending => "待審核",
        ReservationStatus.Approved => "已核准",
        ReservationStatus.Borrowed => "使用中 / 已借出",
        ReservationStatus.Returned => "已歸還",
        ReservationStatus.Rejected => "已拒絕",
        ReservationStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    public static string EquipmentSummary(IReadOnlyList<string> names, int itemCount)
    {
        if (names.Count == 0) return itemCount == 0 ? "—" : $"{itemCount} 台設備";
        var visible = names.Take(3).ToArray();
        var remaining = Math.Max(0, itemCount - visible.Length);
        return remaining == 0 ? string.Join("、", visible) : $"{string.Join("、", visible)}，另 {remaining} 台";
    }

    public static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}

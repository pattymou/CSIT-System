using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class EnvironmentReadinessService(AppDbContext db) : IEnvironmentReadinessService
{
    public async Task<EnvironmentReadinessDto> GetReadinessAsync(
        Guid equipmentGroupId,
        CancellationToken cancellationToken = default)
    {
        var results = await GetReadinessForGroupsAsync([equipmentGroupId], cancellationToken);
        return results.TryGetValue(equipmentGroupId, out var result)
            ? result
            : throw new KeyNotFoundException($"Equipment group {equipmentGroupId} was not found.");
    }

    public async Task<IReadOnlyDictionary<Guid, EnvironmentReadinessDto>> GetReadinessForGroupsAsync(
        IReadOnlyCollection<Guid> equipmentGroupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(equipmentGroupIds);
        var groupIds = equipmentGroupIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (groupIds.Length == 0)
            return new Dictionary<Guid, EnvironmentReadinessDto>();

        // A single, batched query intentionally reads only group composition and apparatus master data.
        // Reservation rows and time-overlap availability do not participate in readiness.
        var rows = await (
            from environmentGroup in db.EquipmentGroups.AsNoTracking()
            where groupIds.Contains(environmentGroup.Id)
            join membership in db.EquipmentGroupDevices.AsNoTracking()
                on environmentGroup.Id equals membership.EquipmentGroupId into memberships
            from membership in memberships.DefaultIfEmpty()
            join apparatus in db.Apparatuses.AsNoTracking()
                on membership.ApparatusId equals apparatus.Id into apparatuses
            from apparatus in apparatuses.DefaultIfEmpty()
            select new ReadinessRow(
                environmentGroup.Id,
                environmentGroup.Code,
                environmentGroup.Name,
                environmentGroup.Status,
                membership == null ? null : membership.Id,
                membership == null ? null : membership.ApparatusId,
                membership == null ? null : membership.IsInEnvironment,
                apparatus != null,
                apparatus == null ? null : apparatus.Name,
                apparatus == null ? null : apparatus.ProductsId,
                apparatus == null ? null : apparatus.ModuleCode,
                apparatus == null ? null : apparatus.ReservationStatus))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.EquipmentGroupId)
            .ToDictionary(x => x.Key, BuildReadiness);
    }

    private static EnvironmentReadinessDto BuildReadiness(IGrouping<Guid, ReadinessRow> groupRows)
    {
        var first = groupRows.First();
        var devices = groupRows
            .Where(x => x.MembershipId.HasValue)
            .OrderBy(x => x.ApparatusProductsId)
            .ThenBy(x => x.ApparatusName)
            .ThenBy(x => x.ApparatusId)
            .ToList();
        var total = devices.Count;
        var present = devices.Count(x => x.IsInEnvironment == true);
        var completeness = EnvironmentGroupDeviceRules.GetCompletenessStatus(total, present);
        var issues = new List<EnvironmentReadinessIssueDto>();

        if (first.GroupStatus != EquipmentGroupStatus.Active)
        {
            issues.Add(new EnvironmentReadinessIssueDto
            {
                Type = EnvironmentReadinessIssueType.GroupInactive,
                Message = $"群組目前為{GroupStatusName(first.GroupStatus)}狀態"
            });
        }

        if (completeness == EquipmentGroupCompletenessStatus.Unconfigured)
        {
            issues.Add(new EnvironmentReadinessIssueDto
            {
                Type = EnvironmentReadinessIssueType.Unconfigured,
                Message = "尚未設定設備"
            });
        }

        foreach (var device in devices)
        {
            var label = DeviceLabel(device);
            if (device.IsInEnvironment != true)
                issues.Add(DeviceIssue(device, EnvironmentReadinessIssueType.DeviceNotPresent, $"{label} 已移出測試環境"));

            if (!device.ApparatusExists)
            {
                issues.Add(DeviceIssue(device, EnvironmentReadinessIssueType.DeviceMissing, $"設備 {device.ApparatusId} 的主檔不存在"));
                continue;
            }

            if (!string.Equals(device.ModuleCode, ApparatusReservationRules.EquipmentModuleCode, StringComparison.OrdinalIgnoreCase))
                issues.Add(DeviceIssue(device, EnvironmentReadinessIssueType.DeviceNotEquipment, $"{label} 不是 equipment 模組設備"));

            if (!string.Equals(device.ReservationStatus, ApparatusReservationRules.BookableStatus, StringComparison.Ordinal))
            {
                var issueType = ApparatusReservationRules.GetReadinessIssueType(device.ReservationStatus);
                issues.Add(DeviceIssue(device, issueType, $"{label}：{DisplayStatus(device.ReservationStatus)}"));
            }
        }

        return new EnvironmentReadinessDto
        {
            EquipmentGroupId = first.EquipmentGroupId,
            GroupCode = first.GroupCode,
            GroupName = first.GroupName,
            Status = issues.Count == 0 ? EnvironmentReadinessStatus.Ready : EnvironmentReadinessStatus.NotReady,
            TotalDeviceCount = total,
            PresentDeviceCount = present,
            Issues = issues
        };
    }

    private static EnvironmentReadinessIssueDto DeviceIssue(
        ReadinessRow device,
        EnvironmentReadinessIssueType type,
        string message) => new()
        {
            Type = type,
            ApparatusId = device.ApparatusId,
            ApparatusName = device.ApparatusName,
            ApparatusProductsId = device.ApparatusProductsId,
            ApparatusReservationStatus = device.ReservationStatus,
            Message = message
        };

    private static string DeviceLabel(ReadinessRow device)
    {
        var identity = string.IsNullOrWhiteSpace(device.ApparatusProductsId)
            ? device.ApparatusId
            : device.ApparatusProductsId;
        return string.IsNullOrWhiteSpace(device.ApparatusName)
            ? identity ?? "未知設備"
            : $"{identity} {device.ApparatusName}";
    }

    private static string DisplayStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "未設定設備狀態" : status;

    private static string GroupStatusName(EquipmentGroupStatus status) => status switch
    {
        EquipmentGroupStatus.Disabled => "停用",
        _ => status.ToString()
    };

    private sealed record ReadinessRow(
        Guid EquipmentGroupId,
        string GroupCode,
        string GroupName,
        EquipmentGroupStatus GroupStatus,
        Guid? MembershipId,
        string? ApparatusId,
        bool? IsInEnvironment,
        bool ApparatusExists,
        string? ApparatusName,
        string? ApparatusProductsId,
        string? ModuleCode,
        string? ReservationStatus);
}

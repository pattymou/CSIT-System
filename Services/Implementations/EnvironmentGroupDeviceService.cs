using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class EnvironmentGroupDeviceService(AppDbContext db) : IEnvironmentGroupDeviceService
{
    public async Task<List<EquipmentGroupDeviceDto>> ListDevicesAsync(
        Guid groupId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveManagerAccessAsync(user, cancellationToken);
        var group = await LoadGroupAsync(groupId, cancellationToken);
        EnsureGroupOwnership(group.OwnerTeamOptionId, access);

        var devices = await db.EquipmentGroupDevices.AsNoTracking()
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.Apparatus)
            .Where(x => x.EquipmentGroupId == groupId)
            .OrderByDescending(x => x.IsInEnvironment)
            .ThenBy(x => x.Apparatus.ProductsId)
            .ThenBy(x => x.Apparatus.Name)
            .ToListAsync(cancellationToken);

        var account = GetAccount(user);
        var canManagePresence = access.IsAdmin || access.TeamOptionIds.Contains(group.OwnerTeamOptionId);
        var custodianNames = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            db,
            devices.Select(x => x.Apparatus.CustodianAccount),
            cancellationToken);
        var result = new List<EquipmentGroupDeviceDto>(devices.Count);
        foreach (var device in devices)
        {
            var canUpdate = canManagePresence
                || string.Equals(device.Apparatus.CustodianAccount, account, StringComparison.OrdinalIgnoreCase);
            result.Add(await MapDeviceAsync(
                device,
                ApparatusCustodianResolver.GetDisplayName(custodianNames, device.Apparatus.CustodianAccount),
                canUpdate,
                cancellationToken));
        }
        return result;
    }

    public async Task<List<EquipmentGroupDeviceCandidateDto>> ListCandidatesAsync(
        Guid groupId,
        string? keyword,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveManagerAccessAsync(user, cancellationToken);
        var group = await LoadGroupAsync(groupId, cancellationToken);
        EnsureGroupOwnership(group.OwnerTeamOptionId, access);

        var query = db.Apparatuses.AsNoTracking()
            .Include(x => x.OwnerTeamOption)
            .Include(x => x.EnvironmentGroupDevices).ThenInclude(x => x.EquipmentGroup)
            .Where(x => x.ModuleCode == ApparatusReservationRules.EquipmentModuleCode);
        var term = Clean(keyword);
        if (term is not null)
        {
            query = query.Where(x =>
                x.Id.Contains(term)
                || (x.ProductsId ?? string.Empty).Contains(term)
                || x.Name.Contains(term)
                || x.Kind.Contains(term)
                || (x.Brand ?? string.Empty).Contains(term)
                || (x.Model ?? string.Empty).Contains(term)
                || (x.Number ?? string.Empty).Contains(term)
                || db.Users.Any(user => x.CustodianAccount != null
                    && user.Account.ToLower() == x.CustodianAccount.ToLower()
                    && user.DisplayName.Contains(term))
                || (x.CustodianAccount ?? string.Empty).Contains(term)
                || (x.Place ?? string.Empty).Contains(term));
        }

        var apparatuses = await query.OrderBy(x => x.ProductsId).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        var custodianNames = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            db,
            apparatuses.Select(x => x.CustodianAccount),
            cancellationToken);
        return apparatuses.Select(x => MapCandidate(
            x,
            ApparatusCustodianResolver.GetDisplayName(custodianNames, x.CustodianAccount),
            group,
            access)).ToList();
    }

    public async Task<List<EquipmentGroupDeviceDto>> AddDevicesAsync(
        Guid groupId,
        AddEquipmentGroupDevicesRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = await ResolveManagerAccessAsync(user, cancellationToken);
        var group = await LoadGroupAsync(groupId, cancellationToken);
        EnsureGroupOwnership(group.OwnerTeamOptionId, access);
        var ids = request.ApparatusIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) throw new ArgumentException("請至少選擇一台設備。", nameof(request.ApparatusIds));

        var apparatuses = await db.Apparatuses
            .Include(x => x.EnvironmentGroupDevices).ThenInclude(x => x.EquipmentGroup)
            .Where(x => ids.Contains(x.Id) && x.ModuleCode == ApparatusReservationRules.EquipmentModuleCode)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (apparatuses.Count != ids.Length)
        {
            var found = apparatuses.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            throw new InvalidOperationException($"設備不存在或不是 equipment：{string.Join("、", ids.Where(x => !found.Contains(x)))}。");
        }

        foreach (var apparatus in apparatuses)
        {
            if (apparatus.EnvironmentGroupDevices.Any(x => x.EquipmentGroupId == groupId))
                throw new InvalidOperationException($"設備 {DeviceLabel(apparatus)} 已屬於此測試環境群組；若目前已移出，請使用「放回測試環境」。");
            if (apparatus.EnvironmentGroupDevices.FirstOrDefault(x => x.IsInEnvironment) is { } active)
                throw new InvalidOperationException($"設備 {DeviceLabel(apparatus)} 已固定於測試環境 {active.EquipmentGroup.Name}，請先從原環境移出或移除。");
            if (!access.IsAdmin && apparatus.OwnerTeamOptionId != group.OwnerTeamOptionId)
                throw new UnauthorizedAccessException($"設備 {DeviceLabel(apparatus)} 不屬於此測試環境群組的管理 Team。");
        }

        var now = DateTime.UtcNow;
        var account = GetAccount(user);
        var note = Clean(request.Note);
        var entities = apparatuses.Select(x => new EquipmentGroupDevice
        {
            Id = Guid.NewGuid(),
            EquipmentGroupId = groupId,
            ApparatusId = x.Id,
            IsInEnvironment = true,
            AddedAt = now,
            AddedBy = account,
            Note = note
        }).ToList();
        db.EquipmentGroupDevices.AddRange(entities);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("選取的設備已同時存在於另一個測試環境，請重新整理後再試。", ex);
        }

        return await ListDevicesAsync(groupId, user, cancellationToken);
    }

    public async Task<bool> DeleteDeviceAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var access = await ResolveManagerAccessAsync(user, cancellationToken);
        var entity = await db.EquipmentGroupDevices
            .Include(x => x.EquipmentGroup)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        EnsureGroupOwnership(entity.EquipmentGroup.OwnerTeamOptionId, access);
        db.EquipmentGroupDevices.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<EquipmentGroupDeviceDto> UpdatePresenceAsync(
        Guid id,
        UpdateEquipmentGroupDevicePresenceRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = await db.EquipmentGroupDevices
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.Apparatus)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Environment group device {id} was not found.");
        var account = GetAccount(user);
        if (!await CanUpdatePresenceAsync(entity, user, account, cancellationToken))
            throw new UnauthorizedAccessException("只有系統管理員、設備保管人或該測試環境群組 Team Leader 可以更新設備環境狀態。");

        if (request.IsInEnvironment && !entity.IsInEnvironment)
        {
            var active = await db.EquipmentGroupDevices.AsNoTracking()
                .Include(x => x.EquipmentGroup)
                .FirstOrDefaultAsync(x => x.ApparatusId == entity.ApparatusId && x.IsInEnvironment && x.Id != entity.Id, cancellationToken);
            if (active is not null)
                throw new InvalidOperationException($"設備 {DeviceLabel(entity.Apparatus)} 目前已固定於測試環境 {active.EquipmentGroup.Name}，無法放回。");
        }

        entity.IsInEnvironment = request.IsInEnvironment;
        entity.PresenceUpdatedAt = DateTime.UtcNow;
        entity.PresenceUpdatedBy = account;
        if (request.Note is not null) entity.Note = Clean(request.Note);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("此設備目前已存在於另一個測試環境，無法放回。", ex);
        }

        var custodianNames = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            db,
            [entity.Apparatus.CustodianAccount],
            cancellationToken);
        return await MapDeviceAsync(
            entity,
            ApparatusCustodianResolver.GetDisplayName(custodianNames, entity.Apparatus.CustodianAccount),
            true,
            cancellationToken);
    }

    public async Task<ApparatusEnvironmentAssignmentDto?> GetApparatusAssignmentAsync(
        string apparatusId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(user);
        var entity = await db.EquipmentGroupDevices.AsNoTracking()
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.Apparatus)
            .Where(x => x.ApparatusId == apparatusId)
            .OrderByDescending(x => x.IsInEnvironment)
            .ThenByDescending(x => x.PresenceUpdatedAt ?? x.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is null) return null;
        var account = GetAccount(user);
        var canUpdate = await CanUpdatePresenceAsync(entity, user, account, cancellationToken);
        return new ApparatusEnvironmentAssignmentDto
        {
            MembershipId = entity.Id,
            EquipmentGroupId = entity.EquipmentGroupId,
            EquipmentGroupCode = entity.EquipmentGroup.Code,
            EquipmentGroupName = entity.EquipmentGroup.Name,
            OwnerTeamOptionId = entity.EquipmentGroup.OwnerTeamOptionId,
            OwnerTeamName = entity.EquipmentGroup.OwnerTeamOption.Name,
            IsInEnvironment = entity.IsInEnvironment,
            CanUpdatePresence = canUpdate,
            Warning = entity.IsInEnvironment ? null : await GetFutureReservationWarningAsync(entity.ApparatusId, cancellationToken)
        };
    }

    public async Task EnsureDirectReservationAllowedAsync(
        IReadOnlyCollection<string> apparatusIds,
        CancellationToken cancellationToken = default)
    {
        if (apparatusIds.Count == 0) return;
        var locked = await db.EquipmentGroupDevices.AsNoTracking()
            .Include(x => x.EquipmentGroup)
            .Include(x => x.Apparatus)
            .Where(x => apparatusIds.Contains(x.ApparatusId) && x.IsInEnvironment)
            .OrderBy(x => x.ApparatusId)
            .FirstOrDefaultAsync(cancellationToken);
        if (locked is not null)
            throw new InvalidOperationException($"設備 {DeviceLabel(locked.Apparatus)} 固定於測試環境 {locked.EquipmentGroup.Name}，目前不可單獨預約。");
    }

    private async Task<EquipmentGroupDeviceDto> MapDeviceAsync(
        EquipmentGroupDevice x,
        string? custodianDisplayName,
        bool canUpdatePresence,
        CancellationToken cancellationToken)
    {
        return new EquipmentGroupDeviceDto
        {
            Id = x.Id,
            EquipmentGroupId = x.EquipmentGroupId,
            EquipmentGroupCode = x.EquipmentGroup.Code,
            EquipmentGroupName = x.EquipmentGroup.Name,
            OwnerTeamOptionId = x.EquipmentGroup.OwnerTeamOptionId,
            OwnerTeamName = x.EquipmentGroup.OwnerTeamOption.Name,
            ApparatusId = x.ApparatusId,
            ProductsId = x.Apparatus.ProductsId,
            ApparatusName = x.Apparatus.Name,
            Kind = x.Apparatus.Kind,
            Brand = x.Apparatus.Brand,
            Model = x.Apparatus.Model,
            Number = x.Apparatus.Number,
            Custodian = custodianDisplayName,
            CustodianAccount = x.Apparatus.CustodianAccount,
            ReservationStatus = x.Apparatus.ReservationStatus,
            Place = x.Apparatus.Place,
            IsInEnvironment = x.IsInEnvironment,
            AddedAt = x.AddedAt,
            AddedBy = x.AddedBy,
            PresenceUpdatedAt = x.PresenceUpdatedAt,
            PresenceUpdatedBy = x.PresenceUpdatedBy,
            Note = x.Note,
            CanUpdatePresence = canUpdatePresence,
            Warning = x.IsInEnvironment ? null : await GetFutureReservationWarningAsync(x.ApparatusId, cancellationToken)
        };
    }

    private static EquipmentGroupDeviceCandidateDto MapCandidate(
        Apparatus x,
        string? custodianDisplayName,
        EquipmentGroup group,
        GroupAccess access)
    {
        var sameMembership = x.EnvironmentGroupDevices.FirstOrDefault(d => d.EquipmentGroupId == group.Id);
        var active = x.EnvironmentGroupDevices.FirstOrDefault(d => d.IsInEnvironment);
        string? reason = null;
        if (sameMembership is not null)
            reason = sameMembership.IsInEnvironment ? "已在此測試環境中" : "已屬於此群組，請使用放回測試環境";
        else if (active is not null)
            reason = $"固定於：{active.EquipmentGroup.Name}，不可加入";
        else if (!access.IsAdmin && x.OwnerTeamOptionId != group.OwnerTeamOptionId)
            reason = "此設備不屬於本 Team";

        return new EquipmentGroupDeviceCandidateDto
        {
            ApparatusId = x.Id,
            ProductsId = x.ProductsId,
            ApparatusName = x.Name,
            Kind = x.Kind,
            Brand = x.Brand,
            Model = x.Model,
            Number = x.Number,
            Custodian = custodianDisplayName,
            CustodianAccount = x.CustodianAccount,
            Place = x.Place,
            ReservationStatus = x.ReservationStatus,
            OwnerTeamOptionId = x.OwnerTeamOptionId,
            OwnerTeamName = x.OwnerTeamOption?.Name,
            ActiveEquipmentGroupId = active?.EquipmentGroupId,
            ActiveEquipmentGroupName = active?.EquipmentGroup.Name,
            CanAdd = reason is null,
            UnavailableReason = reason
        };
    }

    private async Task<EquipmentGroup> LoadGroupAsync(Guid id, CancellationToken cancellationToken) =>
        await db.EquipmentGroups.AsNoTracking()
            .Include(x => x.OwnerTeamOption)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException($"Equipment group {id} was not found.");

    private async Task<bool> CanUpdatePresenceAsync(
        EquipmentGroupDevice entity,
        ClaimsPrincipal user,
        string account,
        CancellationToken cancellationToken)
    {
        if (user.IsInRole("Admin")) return true;
        if (string.Equals(entity.Apparatus.CustodianAccount, account, StringComparison.OrdinalIgnoreCase)) return true;
        return await db.TeamRoutings.AsNoTracking().AnyAsync(x =>
            x.IsEnabled
            && x.TeamOptionId == entity.EquipmentGroup.OwnerTeamOptionId
            && x.LeaderAccount == account, cancellationToken);
    }

    private async Task<string?> GetFutureReservationWarningAsync(string apparatusId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var hasFutureReservation = await db.ReservationItems.AsNoTracking().AnyAsync(x =>
            x.ApparatusId == apparatusId
            && x.Reservation.EndTime > now
            && (x.Reservation.Status == ReservationStatus.Pending
                || x.Reservation.Status == ReservationStatus.Approved
                || x.Reservation.Status == ReservationStatus.Borrowed), cancellationToken);
        return hasFutureReservation ? "此設備仍有進行中或未來的一般預約；放回環境不會取消預約。" : null;
    }

    private async Task<GroupAccess> ResolveManagerAccessAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        EnsureAuthenticated(user);
        if (user.IsInRole("Admin")) return new GroupAccess(true, []);
        var account = GetAccount(user);
        var teamIds = await (
            from routing in db.TeamRoutings.AsNoTracking()
            join team in db.SystemOptions.AsNoTracking() on routing.TeamOptionId equals team.Id
            where routing.IsEnabled
                && routing.LeaderAccount == account
                && team.Category == SystemOptionCategories.Team
                && team.IsEnabled
            select team.Id).Distinct().ToListAsync(cancellationToken);
        if (teamIds.Count == 0)
            throw new UnauthorizedAccessException("只有系統管理員或啟用中的 Team Leader 可以管理測試環境設備。");
        return new GroupAccess(false, teamIds);
    }

    private static void EnsureGroupOwnership(Guid ownerTeamOptionId, GroupAccess access)
    {
        if (!access.IsAdmin && !access.TeamOptionIds.Contains(ownerTeamOptionId))
            throw new UnauthorizedAccessException("此測試環境群組屬於其他 Team。");
    }

    private static void EnsureAuthenticated(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");
    }

    private static string GetAccount(ClaimsPrincipal user)
    {
        EnsureAuthenticated(user);
        var account = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(account))
            throw new UnauthorizedAccessException("Authenticated account claim is missing.");
        return account.Trim().ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static string DeviceLabel(Apparatus apparatus) =>
        string.IsNullOrWhiteSpace(apparatus.ProductsId) ? apparatus.Id : apparatus.ProductsId;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record GroupAccess(bool IsAdmin, List<Guid> TeamOptionIds);
}

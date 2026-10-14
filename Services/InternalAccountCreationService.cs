using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;

namespace SIT.DepartmentSystem.Web.Services;

public sealed class InternalAccountCreationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ProvisioningActorResolver actorResolver,
    SharedIdentityProvisioningClient provisioningClient,
    IBusinessWriteAuthorizationGuard businessWrite)
{
    public async Task<AccountProvisioningOptions> GetOptionsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var departments = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Department && x.IsEnabled)
            .OrderBy(x => x.Sort).ThenBy(x => x.Name)
            .Select(x => x.Value)
            .Where(x => x != string.Empty)
            .ToListAsync(cancellationToken);
        return new AccountProvisioningOptions(actor, departments);
    }

    public async Task<InternalAccountProvisioningResult> CreateAsync(
        ClaimsPrincipal principal,
        CreateInternalAccountForm form,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        var role = form.CsitRole ?? throw new ArgumentException("CSIT Role is required.");
        var team = ProvisioningActorResolver.AuthorizeTarget(actor, form.TeamCode, role);
        var access = role == CsitProvisioningRole.Admin
            ? ProvisionedDataAccessLevel.Editor
            : form.DataAccessLevel ?? throw new ArgumentException("DataAccessLevel is required.");
        var request = new InternalAccountProvisioningRequest(
            form.EmployeeNo, form.Account, form.DisplayName, form.Department, team.Code,
            form.Position, form.Email, form.Extension, role, access, actor.IdentityUserId,
            Guid.NewGuid(), idempotencyKey);
        return await provisioningClient.CreateAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedAccountListItem>> GetAccountsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        var accounts = await provisioningClient.ListAccountsAsync(cancellationToken);

        var allowedTeams = actor.Teams
            .Select(x => x.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopedAccounts = actor.IsAdmin
            ? accounts
            : accounts.Where(x => x.TeamCode is not null && allowedTeams.Contains(x.TeamCode));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activeLeaderAccounts = await (from routing in db.TeamRoutings.AsNoTracking()
                                          join team in db.SystemOptions.AsNoTracking()
                                              on routing.TeamOptionId equals team.Id
                                          where routing.IsEnabled && team.IsEnabled &&
                                                team.Category == SystemOptionCategories.Team
                                          select routing.LeaderAccount)
            .ToListAsync(cancellationToken);
        var leaderAccounts = activeLeaderAccounts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return scopedAccounts
            .Select(x => new ManagedAccountListItem(
                x.IdentityUserId,
                x.EmployeeNo,
                x.Account,
                x.DisplayName,
                x.Department,
                x.TeamCode,
                x.Position,
                x.Extension,
                x.Email,
                x.CsitRole,
                x.DataAccessLevel,
                x.AccountStatus,
                x.SystemAccessStatus,
                x.CreatedAt,
                x.CreatedByIdentityUserId,
                x.CreatedByAccount,
                leaderAccounts.Contains(x.Account)))
            .ToList();
    }

    public async Task<InternalAccountManagementResult> UpdateAsync(
        ClaimsPrincipal principal,
        EditInternalAccountForm form,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        var accounts = await provisioningClient.ListAccountsAsync(cancellationToken);
        var target = accounts.SingleOrDefault(x => x.IdentityUserId == form.IdentityUserId)
            ?? throw new ArgumentException("找不到要編輯的帳號。");
        if (!string.Equals(target.Account, form.Account, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Account 是不可修改的登入識別碼。");
        if (!Enum.TryParse<CsitProvisioningRole>(target.CsitRole, out var currentRole))
            throw new ArgumentException("目前帳號角色不受支援。");
        var requestedRole = form.CsitRole ?? throw new ArgumentException("CSIT Role is required.");
        var requestedStatus = form.AccountStatus ?? throw new ArgumentException("AccountStatus is required.");
        var allowedTeams = actor.Teams.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!actor.IsAdmin)
        {
            if (target.TeamCode is null || !allowedTeams.Contains(target.TeamCode))
                throw new UnauthorizedAccessException("不可編輯其他 Team 的帳號。");
            if (currentRole == CsitProvisioningRole.Admin)
                throw new UnauthorizedAccessException("Team Leader 不可編輯 Admin 帳號。");
            if (requestedRole == CsitProvisioningRole.Admin)
                throw new UnauthorizedAccessException("Team Leader 不可指定 Admin role。");
            if (!string.Equals(target.EmployeeNo, form.EmployeeNo, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Team Leader 不可修改 EmployeeNo。");
        }

        var team = ProvisioningActorResolver.AuthorizeTarget(actor, form.TeamCode, requestedRole);
        if (form.IdentityUserId == actor.IdentityUserId && requestedStatus == ManagedAccountStatus.Disabled)
            throw new UnauthorizedAccessException("不可停用自己的帳號。");
        if (form.IdentityUserId == actor.IdentityUserId && actor.IsAdmin &&
            requestedRole != CsitProvisioningRole.Admin)
            throw new UnauthorizedAccessException("不可移除自己的 Admin role。");

        var access = requestedRole == CsitProvisioningRole.Admin
            ? ProvisionedDataAccessLevel.Editor
            : form.DataAccessLevel ?? throw new ArgumentException("DataAccessLevel is required.");
        var request = new InternalAccountManagementRequest(
            form.EmployeeNo, form.DisplayName, form.Department, team.Code, form.Position,
            form.Email, form.Extension, requestedRole, access, requestedStatus,
            actor.IdentityUserId, Guid.NewGuid());
        return await provisioningClient.UpdateAccountAsync(form.IdentityUserId, request, cancellationToken);
    }

    public async Task<InternalAccountProvisioningResult> ReissueTemporaryPasswordAsync(
        ClaimsPrincipal principal,
        Guid identityUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        var target = await GetSensitiveOperationTargetAsync(actor, identityUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Password reset operation key is required.");
        var request = new TemporaryPasswordReissueRequest(actor.IdentityUserId, Guid.NewGuid(),
            idempotencyKey.Trim());
        return await provisioningClient.ReissueTemporaryPasswordAsync(target.IdentityUserId, request,
            cancellationToken);
    }

    public async Task<InternalAccountManagementResult> DeprovisionAsync(
        ClaimsPrincipal principal,
        Guid identityUserId,
        CancellationToken cancellationToken = default)
    {
        await businessWrite.DemandAsync(cancellationToken);
        var actor = await actorResolver.ResolveAsync(principal, cancellationToken);
        var target = await GetSensitiveOperationTargetAsync(actor, identityUserId, cancellationToken);
        var request = new AccountDeprovisionRequest(actor.IdentityUserId, Guid.NewGuid());
        return await provisioningClient.DeprovisionAccountAsync(target.IdentityUserId, request,
            cancellationToken);
    }

    private async Task<SharedIdentityAccountListItem> GetSensitiveOperationTargetAsync(
        ProvisioningActorContext actor,
        Guid identityUserId,
        CancellationToken cancellationToken)
    {
        var accounts = await provisioningClient.ListAccountsAsync(cancellationToken);
        var target = accounts.SingleOrDefault(x => x.IdentityUserId == identityUserId)
            ?? throw new ArgumentException("找不到要管理的帳號。");
        if (target.IdentityUserId == actor.IdentityUserId)
            throw new UnauthorizedAccessException("不可透過帳號管理操作自己的密碼或刪除自己的帳號。");
        if (!Enum.TryParse<CsitProvisioningRole>(target.CsitRole, out var currentRole))
            throw new ArgumentException("目前帳號角色不受支援。");

        if (!actor.IsAdmin)
        {
            var allowedTeams = actor.Teams.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (target.TeamCode is null || !allowedTeams.Contains(target.TeamCode))
                throw new UnauthorizedAccessException("不可管理其他 Team 的帳號。");
            if (currentRole == CsitProvisioningRole.Admin)
                throw new UnauthorizedAccessException("Team Leader 不可管理 Admin 帳號。");
        }

        return target;
    }
}

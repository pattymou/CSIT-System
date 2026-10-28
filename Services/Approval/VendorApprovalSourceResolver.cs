using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

/// <summary>Resolves a Vendor approval Team only from the authenticated Shared Identity account.</summary>
public sealed class VendorApprovalSourceResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    SharedIdentityProvisioningClient accounts)
{
    public const string ModuleCode = "vendor";
    public const string MenuCode = "vendors";
    public const string ContextType = "Vendor";

    public async Task<ApprovalSourceIdentity> ResolveAsync(Guid vendorId,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        var matches = (await accounts.ListAccountsAsync(cancellationToken))
            .Where(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase) &&
                x.AccountStatus == "Active" && x.SystemAccessStatus == "Active")
            .Take(2).ToList();
        if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].TeamCode) ||
            string.IsNullOrWhiteSpace(matches[0].Department))
            throw new InvalidOperationException(
                "無法唯一判定目前使用者的 Team，廠商附件未送審；請由系統管理員確認帳號 Team 設定。");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var vendor = await db.Vendors.AsNoTracking().Where(x => x.Id == vendorId)
            .Select(x => new { x.Id, x.Name }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("找不到廠商。");
        var teamCode = matches[0].TeamCode!.Trim();
        var teams = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled &&
                x.Value.ToLower() == teamCode.ToLower())
            .Take(2).ToListAsync(cancellationToken);
        if (teams.Count != 1)
            throw new InvalidOperationException(
                "使用者 Team 無法唯一對應 CSIT Team 選項，廠商附件未送審。");
        var routingCount = await db.TeamRoutings.AsNoTracking().CountAsync(x =>
            x.TeamOptionId == teams[0].Id && x.IsEnabled && x.LeaderAccount != "", cancellationToken);
        if (routingCount != 1)
            throw new InvalidOperationException(
                "使用者 Team 沒有唯一且啟用中的主管 routing，廠商附件未送審。");

        return new ApprovalSourceIdentity(ModuleCode, ContextType, vendor.Id.ToString("N"),
            vendor.Name, matches[0].Department.Trim(), teams[0].Id, teams[0].Value);
    }
}

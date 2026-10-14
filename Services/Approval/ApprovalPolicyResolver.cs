using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SIT.DepartmentSystem.Web.Data;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed class ApprovalPolicyResolver(
    ApprovalMenuResolver menus,
    IApprovalCommitHandlerRegistry handlers,
    IApprovalStagingRootResolver stagingRoot,
    IAuthorizationService authorization)
{
    public async Task<ApprovalUploadDecision> ResolveAsync(ApprovalSourceIdentity source,
        ClaimsPrincipal uploader, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(uploader, SystemAuthorization.Policies.BusinessWrite)).Succeeded)
            throw new UnauthorizedAccessException("This account has read-only data access.");
        var menu = await menus.ResolveAsync(source.ModuleCode, cancellationToken);
        if (!menu.RequiresSupervisorApproval)
            return new ApprovalUploadDecision(ApprovalUploadDecisionKind.DirectCommit, menu.Code, source.ModuleCode);
        handlers.Resolve(source.ModuleCode);
        var supervisor = (await authorization.AuthorizeAsync(uploader,
            SystemAuthorization.Policies.Administration)).Succeeded;
        if (!supervisor) await stagingRoot.GetRootAsync(cancellationToken);
        return new ApprovalUploadDecision(supervisor
            ? ApprovalUploadDecisionKind.ApprovalBypassed
            : ApprovalUploadDecisionKind.Pending, menu.Code, source.ModuleCode);
    }
}

public sealed class ApprovalEnableGuard(
    IApprovalCommitHandlerRegistry handlers,
    IApprovalStagingRootResolver stagingRoot,
    IDbContextFactory<AppDbContext>? dbFactory = null,
    IOptions<RawDataSettings>? rawData = null)
{
    public async Task DemandReadyAsync(string? moduleCode, CancellationToken cancellationToken = default)
    {
        if (!handlers.HasHandler(moduleCode))
            throw new InvalidOperationException("無法啟用上傳主管審核：此 Module 尚無可用的正式 Approval Handler。");
        try { await stagingRoot.GetRootAsync(cancellationToken); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("無法啟用上傳主管審核：ApprovalStagingRoot 尚未完成設定或無法存取。");
        }
        if (string.Equals(moduleCode, "equipment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(moduleCode, "goods", StringComparison.OrdinalIgnoreCase))
        {
            if (dbFactory is null || rawData is null)
                throw new InvalidOperationException("無法啟用資產上傳主管審核：正式儲存設定無法驗證。");
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var roots = await db.SystemOptions.AsNoTracking()
                .Where(x => x.Category == "AssetRootPath" && x.IsEnabled
                    && x.Name.ToLower() == moduleCode!.ToLower())
                .Select(x => x.Value).Take(2).ToListAsync(cancellationToken);
            if (roots.Count != 1 || string.IsNullOrWhiteSpace(roots[0])
                || !Path.IsPathFullyQualified(roots[0]) || !Directory.Exists(roots[0]))
                throw new InvalidOperationException("無法啟用資產上傳主管審核：AssetRootPath 未設定或無法存取。");
            if (rawData.Value.Enabled && (string.IsNullOrWhiteSpace(rawData.Value.NasRootPath)
                || !Path.IsPathFullyQualified(rawData.Value.NasRootPath)
                || !Directory.Exists(rawData.Value.NasRootPath)))
                throw new InvalidOperationException("無法啟用資產上傳主管審核：RawData root 未設定或無法存取。");
        }
    }
}

public sealed class ApprovalReviewerScopeResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    IAuthorizationService authorization)
{
    public async Task<ApprovalReviewScope> ResolveForViewAsync(ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (!(await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.Administration)).Succeeded)
            throw new UnauthorizedAccessException("Administration access is required.");
        if (user.IsInRole("Admin")) return new ApprovalReviewScope(true, new HashSet<Guid>());
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var teams = await (from routing in db.TeamRoutings.AsNoTracking()
            join team in db.SystemOptions.AsNoTracking() on routing.TeamOptionId equals team.Id
            where routing.IsEnabled && team.IsEnabled && team.Category == SystemOptionCategories.Team
                && routing.LeaderAccount.ToLower() == account
            select routing.TeamOptionId).ToListAsync(cancellationToken);
        return new ApprovalReviewScope(false, teams.ToHashSet());
    }

    public async Task DemandReviewAsync(ClaimsPrincipal user, PendingSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveForViewAsync(user, cancellationToken);
        if (!(await authorization.AuthorizeAsync(user, SystemAuthorization.Policies.BusinessWrite)).Succeeded)
            throw new UnauthorizedAccessException("BusinessWrite access is required.");
        if (ApprovalIdentity.Account(user) == submission.SubmittedByAccount)
            throw new UnauthorizedAccessException("Self approval is not permitted.");
        if (!scope.Includes(submission.SubmittedTeamOptionId))
            throw new UnauthorizedAccessException("This submission belongs to another Team.");
    }
}

public static class ApprovalIdentity
{
    public static string Account(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");
        var account = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(account)
            ? throw new UnauthorizedAccessException("Account claim is missing.")
            : account.Trim().ToLowerInvariant();
    }
}

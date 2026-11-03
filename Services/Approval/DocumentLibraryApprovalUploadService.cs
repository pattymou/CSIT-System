using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record DocumentLibraryUploadOutcome(
    ApprovalUploadDecisionKind Kind,
    string Message,
    Guid? SubmissionId,
    DocumentLibraryFileDto? File);

public sealed class DocumentLibraryApprovalUploadService(
    IDbContextFactory<AppDbContext> dbFactory,
    ApprovalMenuResolver menus,
    IApprovalCommitHandlerRegistry handlers,
    IApprovalStagingStorage staging,
    ApprovalSubmissionService submissions,
    IDocumentLibraryFormalUploadService formalUploads,
    SharedIdentityProvisioningClient identityAccounts,
    IAuthorizationService authorization,
    IOptions<DocumentStorageOptions> storageOptions)
{
    public async Task<DocumentLibraryUploadOutcome> UploadAsync(
        Guid folderId,
        IFormFile file,
        ClaimsPrincipal uploader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!(await authorization.AuthorizeAsync(uploader,
                SystemAuthorization.Policies.BusinessWrite)).Succeeded)
            throw new UnauthorizedAccessException("This account has read-only data access.");
        if (file.Length <= 0) throw new InvalidOperationException("不可上傳空白檔案。");
        if (file.Length > storageOptions.Value.MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"檔案大小不可超過 {storageOptions.Value.MaxFileSizeBytes / 1_000_000} MB。");

        var context = await ResolveFolderContextAsync(folderId, cancellationToken);
        var menu = await menus.ResolveAsync(ApprovalSourceMenuBinding.DocumentLibraryModule,
            ApprovalSourceMenuBinding.DocumentLibraryMenu, cancellationToken);
        handlers.Resolve(ApprovalSourceMenuBinding.DocumentLibraryModule);
        var account = ApprovalIdentity.Account(uploader);
        var bypassBasis = await ResolveBypassBasisAsync(uploader, account, cancellationToken);
        var operationId = Guid.NewGuid();

        if (bypassBasis is not null)
        {
            var archived = await formalUploads.StageApprovedAsync(operationId, folderId, file, account,
                new DocumentLibraryBypassAudit(account, bypassBasis),
                cancellationToken: cancellationToken);
            return new DocumentLibraryUploadOutcome(ApprovalUploadDecisionKind.ApprovalBypassed,
                "已建立合法免審紀錄，等待正式 NAS 入庫。", null, archived);
        }

        var source = await ResolvePendingSourceAsync(uploader, account, context, cancellationToken);
        var decision = new ApprovalUploadDecision(ApprovalUploadDecisionKind.Pending,
            menu.Code, ApprovalSourceMenuBinding.DocumentLibraryModule);
        StagedFileMetadata? staged = null;
        try
        {
            await using var content = file.OpenReadStream();
            staged = await staging.StoreAsync(operationId, content, file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.Length, cancellationToken);
            await submissions.CreatePendingAsync(operationId, decision, source,
                $"document-library:{folderId:N}:{operationId:N}", uploader, staged, cancellationToken,
                forceSupervisorApproval: true);
        }
        catch
        {
            if (staged is not null)
            {
                try { await staging.DeleteAsync(operationId, staged.StorageKey, cancellationToken); }
                catch { /* The cleanup worker can safely remove an orphaned staging copy. */ }
            }
            throw;
        }

        return new DocumentLibraryUploadOutcome(ApprovalUploadDecisionKind.Pending,
            "已送主管待審；核准前不會正式入庫或派送 KM。", operationId, null);
    }

    private async Task<FolderContext> ResolveFolderContextAsync(Guid folderId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = await db.DocumentFolders.AsNoTracking()
            .Include(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleOrDefaultAsync(x => x.Id == folderId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到資料夾。");
        if (folder.Level != DocumentLibraryRules.MaximumLevel || folder.Parent?.Level != 2 ||
            folder.Parent.Parent?.Level != 1)
            throw new InvalidOperationException("文件只能上傳至完整的第三層資料夾。");
        return new FolderContext(folder.Id,
            $"{folder.Parent.Parent.Name} / {folder.Parent.Name} / {folder.Name}");
    }

    private async Task<string?> ResolveBypassBasisAsync(ClaimsPrincipal uploader, string account,
        CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(uploader,
                SystemAuthorization.Policies.Administration)).Succeeded)
            return null;
        if (uploader.IsInRole("Admin")) return "Administration";

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var isActiveLeader = await (from routing in db.TeamRoutings.AsNoTracking()
            join team in db.SystemOptions.AsNoTracking() on routing.TeamOptionId equals team.Id
            where routing.IsEnabled && team.IsEnabled &&
                team.Category == SystemOptionCategories.Team &&
                routing.LeaderAccount.ToLower() == account
            select routing.Id).AnyAsync(cancellationToken);
        return isActiveLeader ? "ActiveTeamLeader" : null;
    }

    private async Task<ApprovalSourceIdentity> ResolvePendingSourceAsync(
        ClaimsPrincipal uploader,
        string account,
        FolderContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(uploader.FindFirstValue("identity_sub"), out var identityUserId))
            throw new UnauthorizedAccessException("A verified Shared Identity session is required.");

        IReadOnlyList<SharedIdentityAccountListItem> identityDirectory;
        try
        {
            identityDirectory = await identityAccounts.ListAccountsAsync(cancellationToken);
        }
        catch (SharedIdentityProvisioningException exception)
        {
            throw new InvalidOperationException(
                "無法讀取 Shared Identity 帳號資料，文件未送審。", exception);
        }

        var identityMatches = identityDirectory.Where(x => x.IdentityUserId == identityUserId)
            .Take(2).ToList();
        if (identityMatches.Count != 1)
            throw new InvalidOperationException(
                "無法由 Shared Identity 唯一確認目前登入帳號，文件未送審。");
        var identityAccount = identityMatches[0];
        if (!string.Equals(identityAccount.Account?.Trim(), account, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identityAccount.AccountStatus?.Trim(), "Active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identityAccount.SystemAccessStatus?.Trim(), "Active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identityAccount.CsitRole?.Trim(), "CsitStaff", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identityAccount.DataAccessLevel?.Trim(), SystemAuthorization.DataAccessLevels.Editor,
                StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(identityAccount.Department))
            throw new InvalidOperationException(
                "Shared Identity 帳號無效或不具文件上傳資格，文件未送審。");
        if (string.IsNullOrWhiteSpace(identityAccount.TeamCode))
            throw new InvalidOperationException(
                "Shared Identity 帳號缺少 TeamCode，文件未送審；請由系統管理員確認帳號 Team 設定。");

        var teamCode = identityAccount.TeamCode.Trim();
        var normalizedTeamCode = teamCode.ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var teams = await db.SystemOptions.AsNoTracking()
            .Where(x => x.IsEnabled && x.Category == SystemOptionCategories.Team &&
                x.Value.ToLower() == normalizedTeamCode)
            .Take(2).Select(x => new { x.Id, x.Value }).ToListAsync(cancellationToken);
        if (teams.Count != 1)
            throw new InvalidOperationException(
                "Shared Identity TeamCode 無法唯一對應已啟用的 CSIT Team，文件未送審。");

        var routings = await db.TeamRoutings.AsNoTracking()
            .Where(x => x.TeamOptionId == teams[0].Id && x.IsEnabled)
            .Take(2).Select(x => new { x.LeaderAccount }).ToListAsync(cancellationToken);
        if (routings.Count != 1 || string.IsNullOrWhiteSpace(routings[0].LeaderAccount))
            throw new InvalidOperationException(
                "使用者 Team 沒有唯一且啟用中的主管 routing，文件未送審；請先修正 Team Routing。");

        var department = identityAccount.Department?.Trim() ?? string.Empty;
        return new ApprovalSourceIdentity(ApprovalSourceMenuBinding.DocumentLibraryModule,
            DocumentLibraryUploadApprovalCommitHandler.ContextType, context.Id.ToString("N"),
            context.Display, department, teams[0].Id, teams[0].Value);
    }

    private sealed record FolderContext(Guid Id, string Display);
}

public sealed class DocumentLibraryUploadApprovalCommitHandler(
    IDbContextFactory<AppDbContext> dbFactory,
    IApprovalStagingStorage staging,
    IDocumentLibraryFormalUploadService formalUploads) : IApprovalCommitHandler
{
    public const string ContextType = "DocumentLibraryFolder";
    public string ModuleCode => ApprovalSourceMenuBinding.DocumentLibraryModule;

    public async Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken)
    {
        if (submission.SourceModuleCode != ModuleCode || submission.ContextType != ContextType ||
            !Guid.TryParseExact(submission.ContextId, "N", out var folderId) ||
            submission.ReviewerAccount is null || submission.ReviewedAt is null ||
            string.IsNullOrWhiteSpace(submission.StagingStorageKey) ||
            string.IsNullOrWhiteSpace(submission.OriginalFileName) ||
            submission.FileSize is null or <= 0 || string.IsNullOrWhiteSpace(submission.FileHash))
            throw new InvalidOperationException("Document Library approval evidence is incomplete.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DocumentFolders.AsNoTracking().AnyAsync(
                x => x.Id == folderId && x.Level == DocumentLibraryRules.MaximumLevel, cancellationToken))
            throw new InvalidOperationException("The approved Document Library folder is unavailable.");
        await using var content = await staging.OpenReadAsync(
            submission.Id, submission.StagingStorageKey, cancellationToken);
        if (content.Length != submission.FileSize ||
            !string.Equals(Convert.ToHexString(
                    await SHA256.HashDataAsync(content, cancellationToken)),
                submission.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved Document Library staging file no longer matches.");
    }

    public async Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission,
        CancellationToken cancellationToken)
    {
        await ValidateAsync(submission, cancellationToken);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var existing = await db.DocumentLibraryFiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == submission.Id, cancellationToken);
            if (existing is not null) return new ApprovalCommitResult(existing.Id.ToString("N"));
        }

        var folderId = Guid.ParseExact(submission.ContextId, "N");
        await using var stream = await staging.OpenReadAsync(
            submission.Id, submission.StagingStorageKey!, cancellationToken);
        var formFile = new FormFile(stream, 0, submission.FileSize!.Value, "files",
            submission.OriginalFileName!)
        {
            Headers = new HeaderDictionary(),
            ContentType = submission.ContentType ?? "application/octet-stream"
        };
        var archived = await formalUploads.StageApprovedAsync(submission.Id, folderId, formFile,
            submission.SubmittedByAccount, receivedAt: submission.SubmittedAt,
            cancellationToken: cancellationToken);
        return new ApprovalCommitResult(archived.Id.ToString("N"));
    }
}

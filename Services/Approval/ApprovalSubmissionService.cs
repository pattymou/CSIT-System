using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed record ApprovalSourceDto(string Code, string Title, int SortOrder);

public sealed record ApprovalSubmissionDto(
    Guid Id, string SourceMenuCode, string SourceTitle, string ContextType, string ContextId,
    string? ContextDisplay, string? OriginalFileName, string SubmittedByAccount, DateTime SubmittedAt,
    string SubmittedTeamCode, ApprovalStatus Status, string? ReviewerAccount, DateTime? ReviewedAt,
    string? RejectReason, bool HasStagingFile, int CommitAttemptCount, string? LastCommitError)
{
    public string? ContextRoute { get; init; }
    public string SourceModuleCode { get; init; } = string.Empty;
}

public sealed record MySubmissionCounts(int Pending, int Rejected);

public sealed class ApprovalSubmissionService(
    IDbContextFactory<AppDbContext> dbFactory,
    ApprovalReviewerScopeResolver reviewerScope,
    IApprovalCommitHandlerRegistry handlers,
    IApprovalStagingRootResolver stagingRoot,
    IApprovalStagingStorage stagingStorage,
    ILogger<ApprovalSubmissionService>? logger = null)
{
    public async Task<IReadOnlyList<ApprovalSourceDto>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var menus = await db.MenuItems.AsNoTracking()
            .Where(x => x.IsEnabled && x.RequiresSupervisorApproval && x.ModuleCode != null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ThenBy(x => x.Code)
            .Select(x => new { x.Code, x.Title, x.SortOrder, x.ModuleCode })
            .ToListAsync(cancellationToken);
        return menus.Where(x => handlers.HasHandler(x.ModuleCode))
            .Select(x => new ApprovalSourceDto(x.Code, x.Title, x.SortOrder)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountForReviewBySourceAsync(
        ClaimsPrincipal reviewer, CancellationToken cancellationToken = default)
    {
        var scope = await reviewerScope.ResolveForViewAsync(reviewer, cancellationToken);
        var sourceCodes = (await ListActiveSourcesAsync(cancellationToken)).Select(x => x.Code).ToArray();
        if (sourceCodes.Length == 0) return new Dictionary<string, int>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PendingSubmissions.AsNoTracking().Where(x => sourceCodes.Contains(x.SourceMenuCode) &&
            (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.ApprovedPendingCommit ||
                x.Status == ApprovalStatus.CommitFailed));
        if (!scope.IsAdmin)
        {
            var teamIds = scope.TeamOptionIds.ToArray();
            query = query.Where(x => teamIds.Contains(x.SubmittedTeamOptionId));
        }
        return await query.GroupBy(x => x.SourceMenuCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count, cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalSubmissionDto>> ListForReviewAsync(
        ClaimsPrincipal reviewer, string sourceMenuCode, CancellationToken cancellationToken = default)
    {
        var scope = await reviewerScope.ResolveForViewAsync(reviewer, cancellationToken);
        var sources = await ListActiveSourcesAsync(cancellationToken);
        if (!sources.Any(x => x.Code == sourceMenuCode)) return [];
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PendingSubmissions.AsNoTracking().Where(x => x.SourceMenuCode == sourceMenuCode &&
            (x.Status == ApprovalStatus.Pending || x.Status == ApprovalStatus.ApprovedPendingCommit ||
                x.Status == ApprovalStatus.CommitFailed));
        if (!scope.IsAdmin)
        {
            var teamIds = scope.TeamOptionIds.ToArray();
            query = query.Where(x => teamIds.Contains(x.SubmittedTeamOptionId));
        }
        var items = await query.OrderBy(x => x.SubmittedAt).Take(200).ToListAsync(cancellationToken);
        var title = sources.Single(x => x.Code == sourceMenuCode).Title;
        var result = new List<ApprovalSubmissionDto>(items.Count);
        foreach (var item in items)
        {
            var context = await ApprovalContextNavigation.ResolveAsync(db, item, cancellationToken);
            result.Add(Map(item, title) with
            {
                ContextDisplay = context?.Display ?? item.ContextDisplay,
                ContextRoute = context?.Route
            });
        }
        return result;
    }

    public async Task<ApprovalStatus> GetStatusForReviewAsync(Guid id, ClaimsPrincipal reviewer,
        CancellationToken cancellationToken = default)
    {
        var scope = await reviewerScope.ResolveForViewAsync(reviewer, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.PendingSubmissions.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        if (!scope.Includes(item.SubmittedTeamOptionId))
            throw new UnauthorizedAccessException("This submission belongs to another Team.");
        return item.Status;
    }

    public async Task<MySubmissionCounts> CountMineAsync(ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return new MySubmissionCounts(
            await db.PendingSubmissions.CountAsync(x => x.SubmittedByAccount == account
                && x.Status == ApprovalStatus.Pending, cancellationToken),
            await db.PendingSubmissions.CountAsync(x => x.SubmittedByAccount == account
                && x.Status == ApprovalStatus.Rejected && x.ResubmittedAsSubmissionId == null
                && x.SubmitterDismissedAt == null, cancellationToken));
    }

    public Task<IReadOnlyList<ApprovalSubmissionDto>> ListRejectedMineAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ListMineAsync(user, ApprovalStatus.Rejected, cancellationToken);

    public async Task<IReadOnlyList<ApprovalSubmissionDto>> ListMineAsync(
        ClaimsPrincipal user, ApprovalStatus status, CancellationToken cancellationToken = default)
    {
        if (status is not (ApprovalStatus.Pending or ApprovalStatus.Rejected))
            throw new ArgumentException("Only active Pending or Rejected submissions can be listed.", nameof(status));
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PendingSubmissions.AsNoTracking()
            .Where(x => x.SubmittedByAccount == account && x.Status == status);
        if (status == ApprovalStatus.Rejected)
            query = query.Where(x => x.ResubmittedAsSubmissionId == null && x.SubmitterDismissedAt == null);
        var items = await query
            .OrderByDescending(x => x.SubmittedAt).Take(200).ToListAsync(cancellationToken);
        var codes = items.Select(x => x.SourceMenuCode).Distinct().ToList();
        var titles = await db.MenuItems.AsNoTracking().Where(x => codes.Contains(x.Code))
            .ToDictionaryAsync(x => x.Code, x => x.Title, cancellationToken);
        var result = new List<ApprovalSubmissionDto>(items.Count);
        foreach (var item in items)
        {
            var context = await ApprovalContextNavigation.ResolveAsync(db, item, cancellationToken);
            result.Add(Map(item, titles.GetValueOrDefault(item.SourceMenuCode, item.SourceMenuCode)) with
            {
                ContextDisplay = context?.Display ?? item.ContextDisplay,
                ContextRoute = context?.Route
            });
        }
        return result;
    }

    public async Task<ApprovalSubmissionDto> GetActiveRejectedMineAsync(Guid id, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.PendingSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.SubmittedByAccount == account && x.Status == ApprovalStatus.Rejected
            && x.ResubmittedAsSubmissionId == null && x.SubmitterDismissedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("此退回項目已處理，請重新整理頁面。");
        var title = await db.MenuItems.AsNoTracking().Where(x => x.Code == item.SourceMenuCode)
            .Select(x => x.Title).FirstOrDefaultAsync(cancellationToken) ?? item.SourceMenuCode;
        var context = await ApprovalContextNavigation.ResolveAsync(db, item, cancellationToken);
        return Map(item, title) with { ContextDisplay = context?.Display ?? item.ContextDisplay,
            ContextRoute = context?.Route };
    }

    public async Task<PendingSubmission> CreatePendingAsync(
        Guid submissionId, ApprovalUploadDecision decision, ApprovalSourceIdentity source, string submissionKey,
        ClaimsPrincipal submitter, StagedFileMetadata? file, CancellationToken cancellationToken = default,
        Guid? resubmissionOfId = null)
    {
        if (submissionId == Guid.Empty || decision.Kind != ApprovalUploadDecisionKind.Pending ||
            !string.Equals(decision.ModuleCode, source.ModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Submission is not eligible for Pending approval.");
        handlers.Resolve(source.ModuleCode);
        await stagingRoot.GetRootAsync(cancellationToken);
        if (file is not null)
        {
            await using var staged = await stagingStorage.OpenReadAsync(submissionId, file.StorageKey, cancellationToken);
            if (staged.Length != file.FileSize ||
                !string.Equals(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(staged, cancellationToken)),
                    file.FileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staged file metadata does not match its content.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var menu = await db.MenuItems.AsNoTracking().Where(x => x.Code == decision.MenuCode)
            .Take(2).ToListAsync(cancellationToken);
        if (menu.Count != 1 || !menu[0].IsEnabled || !menu[0].RequiresSupervisorApproval
            || !string.Equals(menu[0].ModuleCode, source.ModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Approval source configuration changed; submission was stopped.");
        var reportTarget = source.ContextType is nameof(ThreeLevelUploadKind.TestReport)
            or nameof(ThreeLevelUploadKind.TestReportByTaskNo)
            or nameof(ThreeLevelUploadKind.TestReportVersion)
            ? ThreeLevelUploadTarget.FromContext(source.ContextType, source.ContextId) : null;
        await using var transaction = (reportTarget is null && resubmissionOfId is null) || !db.Database.IsRelational() ? null
            : await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        PendingSubmission? previous = null;
        if (resubmissionOfId.HasValue)
        {
            previous = await LoadForUpdateAsync(db, resubmissionOfId.Value, cancellationToken);
            if (previous.SubmittedByAccount != ApprovalIdentity.Account(submitter)
                || previous.Status != ApprovalStatus.Rejected
                || previous.ResubmittedAsSubmissionId.HasValue || previous.SubmitterDismissedAt.HasValue
                || previous.SourceModuleCode != source.ModuleCode
                || previous.ContextType != source.ContextType || previous.ContextId != source.ContextId)
                throw new InvalidOperationException("此退回項目已處理或來源已變更，請重新整理頁面。");
        }
        if (reportTarget is not null)
            await TestReportBusinessRules.ValidatePendingAsync(db, reportTarget, submissionId, cancellationToken);
        var now = DateTime.UtcNow;
        var submission = PendingSubmission.Create(submissionId, submissionKey, menu[0].Code,
            source.ModuleCode, ApprovalIdentity.Account(submitter), source.Department ?? string.Empty,
            source.TeamOptionId ?? Guid.Empty, source.TeamCode ?? string.Empty,
            source.ContextType, source.ContextId, source.ContextDisplay, file, now);
        db.PendingSubmissions.Add(submission);
        db.ApprovalAuditEvents.Add(Event(submission, ApprovalAuditActions.Submitted,
            submission.SubmittedByAccount, now, null, ApprovalStatus.Pending));
        if (previous is not null)
        {
            previous.MarkResubmitted(submission.Id, now);
            db.ApprovalAuditEvents.Add(Event(previous, ApprovalAuditActions.Resubmitted,
                previous.SubmittedByAccount, now, ApprovalStatus.Rejected, ApprovalStatus.Rejected,
                details: submission.Id.ToString("N")));
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return submission;
    }

    public async Task RecordBypassAsync(Guid operationId, ApprovalUploadDecision decision,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (decision.Kind != ApprovalUploadDecisionKind.ApprovalBypassed || operationId == Guid.Empty)
            throw new InvalidOperationException("Only supervisor bypass operations can be audited here.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(operationId, decision.MenuCode,
            decision.ModuleCode, ApprovalAuditActions.ApprovalBypassed,
            ApprovalIdentity.Account(actor), DateTime.UtcNow, null, null));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task ApproveAsync(Guid id, ClaimsPrincipal reviewer, CancellationToken cancellationToken = default) =>
        ReviewAsync(id, reviewer, null, false, cancellationToken);

    public Task RejectAsync(Guid id, ClaimsPrincipal reviewer, string? reason,
        CancellationToken cancellationToken = default) =>
        ReviewAsync(id, reviewer, reason, true, cancellationToken);

    private async Task ReviewAsync(Guid id, ClaimsPrincipal reviewer, string? reason, bool reject,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var submission = await LoadForUpdateAsync(db, id, cancellationToken);
        await reviewerScope.DemandReviewAsync(reviewer, submission, cancellationToken);
        var actor = ApprovalIdentity.Account(reviewer);
        var now = DateTime.UtcNow;
        if (reject) submission.Reject(actor, reason, now);
        else submission.Approve(actor, now);
        db.ApprovalAuditEvents.Add(Event(submission,
            reject ? ApprovalAuditActions.Rejected : ApprovalAuditActions.Approved, actor, now,
            ApprovalStatus.Pending, submission.Status, reject ? submission.RejectReason : null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task WithdrawMineAsync(Guid id, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var item = await LoadForUpdateAsync(db, id, cancellationToken);
        if (item.SubmittedByAccount != account) throw new UnauthorizedAccessException();
        if (item.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("此提交已由主管處理，無法撤回，請重新整理頁面。");
        var now = DateTime.UtcNow;
        item.Withdraw(now);
        db.ApprovalAuditEvents.Add(Event(item, ApprovalAuditActions.Withdrawn, account, now,
            ApprovalStatus.Pending, ApprovalStatus.Withdrawn));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        await TryCleanStagingAsync(id, cancellationToken);
    }

    public async Task DismissMineAsync(Guid id, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var account = ApprovalIdentity.Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var item = await LoadForUpdateAsync(db, id, cancellationToken);
        if (item.SubmittedByAccount != account) throw new UnauthorizedAccessException();
        var now = DateTime.UtcNow;
        item.Dismiss(now);
        db.ApprovalAuditEvents.Add(Event(item, ApprovalAuditActions.SubmitterDismissed, account, now,
            ApprovalStatus.Rejected, ApprovalStatus.Rejected));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        await TryCleanStagingAsync(id, cancellationToken);
    }

    private async Task TryCleanStagingAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await CleanStagingAsync(id, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Approval staging cleanup will retry for submission {SubmissionId}", id);
        }
    }

    private async Task CleanStagingAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.PendingSubmissions.SingleAsync(x => x.Id == id, cancellationToken);
        if (item.StagingStorageKey is null) return;
        await stagingStorage.DeleteAsync(id, item.StagingStorageKey, cancellationToken);
        item.ClearStagingFile(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CleanInactiveStagingBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.StagingStorageKey != null &&
                (x.Status == ApprovalStatus.Withdrawn || x.SubmitterDismissedAt != null))
            .OrderBy(x => x.UpdatedAt).Select(x => x.Id).Take(20).ToListAsync(cancellationToken);
        var cleaned = 0;
        foreach (var id in ids)
        {
            try { await CleanStagingAsync(id, cancellationToken); cleaned++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Approval staging cleanup will retry for submission {SubmissionId}", id);
            }
        }
        return cleaned;
    }

    private static Task<PendingSubmission> LoadForUpdateAsync(AppDbContext db, Guid id,
        CancellationToken cancellationToken) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? db.PendingSubmissions.FromSqlInterpolated($"SELECT * FROM pending_submissions WHERE id = {id} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : db.PendingSubmissions.SingleAsync(x => x.Id == id, cancellationToken);

    public async Task RetryAsync(Guid id, ClaimsPrincipal reviewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var submission = await db.PendingSubmissions.SingleAsync(x => x.Id == id, cancellationToken);
        await reviewerScope.DemandReviewAsync(reviewer, submission, cancellationToken);
        var now = DateTime.UtcNow;
        submission.Retry(now);
        db.ApprovalAuditEvents.Add(Event(submission, ApprovalAuditActions.CommitRetryRequested,
            ApprovalIdentity.Account(reviewer), now, ApprovalStatus.CommitFailed, submission.Status));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)> OpenFileAsync(
        Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var submission = await db.PendingSubmissions.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        var account = ApprovalIdentity.Account(user);
        if (submission.Status == ApprovalStatus.Withdrawn || submission.SubmitterDismissedAt.HasValue)
            throw new FileNotFoundException("This submission has no downloadable file.");
        if (!((submission.Status is ApprovalStatus.Pending or ApprovalStatus.Rejected)
            && submission.SubmittedByAccount == account
            && submission.SubmitterDismissedAt is null))
        {
            var scope = await reviewerScope.ResolveForViewAsync(user, cancellationToken);
            if (!scope.Includes(submission.SubmittedTeamOptionId))
                throw new UnauthorizedAccessException("This submission belongs to another Team.");
        }
        if (string.IsNullOrWhiteSpace(submission.StagingStorageKey))
            throw new FileNotFoundException("This submission has no staged file.");
        return (await stagingStorage.OpenReadAsync(id, submission.StagingStorageKey, cancellationToken),
            submission.OriginalFileName ?? "upload", submission.ContentType ?? "application/octet-stream");
    }

    internal static ApprovalAuditEvent Event(PendingSubmission submission, string action, string actor,
        DateTime when, ApprovalStatus? from, ApprovalStatus? to, string? reason = null, string? details = null) =>
        ApprovalAuditEvent.Create(submission.Id, submission.SourceMenuCode, submission.SourceModuleCode,
            action, actor, when, from, to, reason, submission.CommitAttemptCount == 0
                ? null : submission.CommitAttemptCount, details);

    private static ApprovalSubmissionDto Map(PendingSubmission item, string title) => new(
        item.Id, item.SourceMenuCode, title, item.ContextType, item.ContextId, item.ContextDisplay,
        item.OriginalFileName, item.SubmittedByAccount, item.SubmittedAt, item.SubmittedTeamCode,
        item.Status, item.ReviewerAccount, item.ReviewedAt, item.RejectReason,
        !string.IsNullOrWhiteSpace(item.StagingStorageKey), item.CommitAttemptCount, item.LastCommitError)
        { SourceModuleCode = item.SourceModuleCode };

}

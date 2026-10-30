using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

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

public enum CommittedStagingDryRunCategory
{
    SafeToClean,
    FormalFileMissing,
    FileMismatch,
    CommitOrAuditIncomplete,
    StagingMissing,
    Other
}

public sealed record CommittedStagingDryRunSummary(
    int Total, IReadOnlyDictionary<CommittedStagingDryRunCategory, int> Counts);

public sealed class ApprovalSubmissionService(
    IDbContextFactory<AppDbContext> dbFactory,
    ApprovalReviewerScopeResolver reviewerScope,
    IApprovalCommitHandlerRegistry handlers,
    IApprovalStagingRootResolver stagingRoot,
    IApprovalStagingStorage stagingStorage,
    ILogger<ApprovalSubmissionService>? logger = null,
    IOptions<ApprovalStagingCleanupOptions>? cleanupOptions = null)
{
    private readonly ApprovalStagingCleanupOptions committedCleanup =
        cleanupOptions?.Value ?? new ApprovalStagingCleanupOptions();

    public async Task<IReadOnlyList<ApprovalSourceDto>> ListActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var menus = await db.MenuItems.AsNoTracking()
            .Where(x => x.IsEnabled && x.RequiresSupervisorApproval)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ThenBy(x => x.Code)
            .Select(x => new { x.Code, x.Title, x.SortOrder, x.ModuleCode })
            .ToListAsync(cancellationToken);
        return menus.Where(x => handlers.HasHandler(
                ApprovalSourceMenuBinding.EffectiveModuleCode(x.Code, x.ModuleCode)))
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
            || !ApprovalSourceMenuBinding.Matches(menu[0].Code, menu[0].ModuleCode, source.ModuleCode))
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
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
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
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
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

        cleaned += await CleanCommittedStagingBatchAsync(cancellationToken);

        var committedEquipmentIds = await (
            from submission in db.PendingSubmissions.AsNoTracking()
            join deletion in db.EquipmentDeletionJobs.AsNoTracking()
                on submission.ContextId equals deletion.EquipmentId
            where submission.StagingStorageKey != null &&
                submission.SourceModuleCode == "equipment" &&
                submission.ContextType == "Asset" &&
                submission.Status == ApprovalStatus.Committed &&
                deletion.Status == EquipmentDeletionStatuses.Completed
            orderby submission.UpdatedAt
            select submission.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in committedEquipmentIds)
        {
            try
            {
                if (await CleanCompletedEquipmentStagingAsync(id, cancellationToken)) cleaned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex,
                    "Completed equipment staging cleanup will retry for submission {SubmissionId}", id);
            }
        }
        return cleaned;
    }

    private async Task<int> CleanCommittedStagingBatchAsync(CancellationToken cancellationToken)
    {
        var cutoff = NormalizeCutoff(committedCleanup.NewSubmissionNotBeforeUtc);
        if (!committedCleanup.EnableNewSubmissionCleanup && !committedCleanup.EnableHistoricalCleanup)
            return 0;
        if (committedCleanup.EnableNewSubmissionCleanup && cutoff is null)
        {
            logger?.LogWarning(
                "Approval committed staging cleanup is disabled because NewSubmissionNotBeforeUtc is missing.");
            if (!committedCleanup.EnableHistoricalCleanup) return 0;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PendingSubmissions.AsNoTracking().Where(x =>
            x.Status == ApprovalStatus.Committed && x.StagingStorageKey != null);
        if (committedCleanup.EnableNewSubmissionCleanup && cutoff is not null &&
            !committedCleanup.EnableHistoricalCleanup)
            query = query.Where(x => x.SubmittedAt >= cutoff.Value);
        else if (!committedCleanup.EnableNewSubmissionCleanup &&
            committedCleanup.EnableHistoricalCleanup && cutoff is not null)
            query = query.Where(x => x.SubmittedAt < cutoff.Value);

        var batchSize = Math.Clamp(committedCleanup.BatchSize, 1, 100);
        var candidateIds = await query.OrderBy(x => x.UpdatedAt).Select(x => x.Id)
            .Take(batchSize).ToListAsync(cancellationToken);
        var cleaned = 0;
        foreach (var id in candidateIds)
        {
            try
            {
                if (await TryCleanCommittedStagingAsync(id, cancellationToken)) cleaned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "Committed staging cleanup will retry for submission {SubmissionId}", id);
            }
        }
        return cleaned;
    }

    public async Task<CommittedStagingDryRunSummary> DryRunCommittedStagingAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.Status == ApprovalStatus.Committed && x.StagingStorageKey != null)
            .OrderBy(x => x.SubmittedAt).ToListAsync(cancellationToken);
        var counts = Enum.GetValues<CommittedStagingDryRunCategory>()
            .ToDictionary(x => x, _ => 0);
        foreach (var item in items)
        {
            CommittedStagingInspection inspection;
            try
            {
                inspection = await InspectCommittedStagingAsync(db, item, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "Committed staging dry-run could not inspect submission {SubmissionId}", item.Id);
                inspection = Inspection(CommittedStagingDryRunCategory.Other,
                    "the submission could not be inspected safely");
            }
            counts[inspection.Category]++;
        }
        return new CommittedStagingDryRunSummary(items.Count, counts);
    }

    private async Task<bool> TryCleanCommittedStagingAsync(Guid id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var item = await LoadForUpdateAsync(db, id, cancellationToken);
        if (item.StagingStorageKey is null) return false;
        var inspection = await InspectCommittedStagingAsync(db, item, cancellationToken);
        if (!inspection.FormalFileSafe)
        {
            logger?.LogWarning(
                "Skipped committed staging cleanup for submission {SubmissionId}: {Reason}",
                id, inspection.Reason);
            return false;
        }

        await stagingStorage.DeleteAsync(id, item.StagingStorageKey, cancellationToken);
        item.ClearCommittedStagingFile(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<CommittedStagingInspection> InspectCommittedStagingAsync(AppDbContext db,
        PendingSubmission item, CancellationToken cancellationToken)
    {
        if (item.Status != ApprovalStatus.Committed ||
            !Guid.TryParseExact(item.CommitResultKey, "N", out var resultId) ||
            item.FileSize is null or <= 0 || !IsSha256(item.FileHash) ||
            item.ReviewerAccount is null || item.ReviewedAt is null ||
            item.CommitLeaseToken is not null || item.CommitLeaseExpiresAt is not null)
            return Inspection(CommittedStagingDryRunCategory.CommitOrAuditIncomplete,
                "commit metadata or lease is incomplete");

        var auditActions = await db.ApprovalAuditEvents.AsNoTracking()
            .Where(x => x.SubmissionId == item.Id).Select(x => x.Action)
            .ToListAsync(cancellationToken);
        if (!auditActions.Contains(ApprovalAuditActions.Submitted) ||
            !auditActions.Contains(ApprovalAuditActions.Approved) ||
            !auditActions.Contains(ApprovalAuditActions.Committed))
            return Inspection(CommittedStagingDryRunCategory.CommitOrAuditIncomplete,
                "required Submitted, Approved, or Committed audit is missing");

        if (await db.PendingSubmissions.AsNoTracking().AnyAsync(x => x.Id != item.Id &&
            x.StagingStorageKey != null && x.StagingStorageKey == item.StagingStorageKey,
            cancellationToken))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the staging key is referenced by another submission");

        var formal = item.SourceModuleCode.ToLowerInvariant() switch
        {
            "equipment" or "goods" => await InspectAssetFormalFileAsync(db, item, resultId,
                cancellationToken),
            "certification" or "verification" => await InspectThreeLevelFormalFileAsync(db,
                item, resultId, cancellationToken),
            _ => Inspection(CommittedStagingDryRunCategory.Other,
                "the approval module has no committed staging cleanup verifier")
        };
        if (!formal.FormalFileSafe) return formal;

        try
        {
            await using var staged = await stagingStorage.OpenReadAsync(item.Id,
                item.StagingStorageKey!, cancellationToken);
            return Inspection(CommittedStagingDryRunCategory.SafeToClean,
                "formal file and staging file are verified", true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Inspection(CommittedStagingDryRunCategory.StagingMissing,
                "staging file is already absent after formal file verification", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Inspection(CommittedStagingDryRunCategory.Other,
                "staging file cannot be read");
        }
    }

    private static async Task<CommittedStagingInspection> InspectAssetFormalFileAsync(
        AppDbContext db, PendingSubmission item, Guid resultId,
        CancellationToken cancellationToken)
    {
        var file = await db.ApparatusFiles.AsNoTracking().Include(x => x.Apparatus)
            .SingleOrDefaultAsync(x => x.Id == resultId, cancellationToken);
        if (file is null)
            return Inspection(CommittedStagingDryRunCategory.FormalFileMissing,
                "the formal asset record was removed or never committed");
        if (file.Apparatus is null || file.ApparatusId != item.ContextId ||
            !string.Equals(file.Apparatus.ModuleCode, item.SourceModuleCode,
                StringComparison.OrdinalIgnoreCase))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the formal asset record belongs to another context");
        if (file.FileSize != item.FileSize ||
            !string.Equals(file.Sha256, item.FileHash, StringComparison.OrdinalIgnoreCase))
            return Inspection(CommittedStagingDryRunCategory.FileMismatch,
                "the formal asset database size or SHA256 does not match the submission");

        var formal = await InspectPhysicalFileAsync(Path.Combine(file.FilePath, file.FileName),
            item.FileSize.Value, item.FileHash!, cancellationToken);
        if (!formal.FormalFileSafe) return formal;
        var managedEquipment = item.SourceModuleCode.Equals("equipment",
            StringComparison.OrdinalIgnoreCase) && file.ArchiveStatus is not null;
        if (managedEquipment)
            return file.ArchiveStatus == FileArchiveStatuses.Archived
                ? formal
                : Inspection(CommittedStagingDryRunCategory.Other,
                    "the managed equipment archive is not complete");
        if (!file.IsRawDataExported || string.IsNullOrWhiteSpace(file.NasFolderPath) ||
            string.IsNullOrWhiteSpace(file.RawJsonPath) || !File.Exists(file.RawJsonPath))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the required asset RAW mirror or manifest is incomplete");
        var mirrorPath = Path.Combine(file.NasFolderPath, file.Id.ToString("N"), file.FileName);
        return await InspectPhysicalFileAsync(mirrorPath, item.FileSize.Value, item.FileHash!,
            cancellationToken, "RAW mirror");
    }

    private static async Task<CommittedStagingInspection> InspectThreeLevelFormalFileAsync(
        AppDbContext db, PendingSubmission item, Guid resultId,
        CancellationToken cancellationToken)
    {
        var file = await db.ModuleCaseFiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == resultId, cancellationToken);
        if (file is null)
            return Inspection(CommittedStagingDryRunCategory.FormalFileMissing,
                "the formal three-level record was removed or never committed");
        var moduleCode = await db.ModuleRecords.AsNoTracking().Where(x => x.Id == file.RecordId)
            .Select(x => x.Module.Code).SingleOrDefaultAsync(cancellationToken);
        if (!string.Equals(moduleCode, item.SourceModuleCode, StringComparison.OrdinalIgnoreCase))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the formal three-level record belongs to another module");

        ThreeLevelUploadTarget target;
        try { target = ThreeLevelUploadTarget.FromContext(item.ContextType, item.ContextId); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Inspection(CommittedStagingDryRunCategory.CommitOrAuditIncomplete,
                "the committed three-level context is invalid");
        }
        if (!await ThreeLevelContextMatchesAsync(db, file, target, cancellationToken))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the formal three-level record belongs to another context");
        var isTestReport = target.Kind is ThreeLevelUploadKind.TestReport or
            ThreeLevelUploadKind.TestReportByTaskNo or ThreeLevelUploadKind.TestReportVersion;
        if (isTestReport && (file.FileKind != ModuleCaseFileKinds.TestReport ||
            file.ReviewStatus != TestReportReviewStatuses.Approved))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the formal test report is not approved");
        if (!isTestReport && file.FileKind != ModuleCaseFileKinds.Attachment)
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the formal attachment kind does not match the approval context");
        if (file.FileSize != item.FileSize ||
            !string.Equals(file.Sha256, item.FileHash, StringComparison.OrdinalIgnoreCase))
            return Inspection(CommittedStagingDryRunCategory.FileMismatch,
                "the formal three-level database size or SHA256 does not match the submission");

        var formal = await InspectPhysicalFileAsync(Path.Combine(file.FilePath, file.FileName),
            item.FileSize.Value, item.FileHash!, cancellationToken);
        if (!formal.FormalFileSafe) return formal;
        if (!file.IsRawDataExported || string.IsNullOrWhiteSpace(file.NasFilePath) ||
            string.IsNullOrWhiteSpace(file.RawJsonPath) || !File.Exists(file.RawJsonPath))
            return Inspection(CommittedStagingDryRunCategory.Other,
                "the required three-level RAW mirror or manifest is incomplete");
        return await InspectPhysicalFileAsync(file.NasFilePath, item.FileSize.Value,
            item.FileHash!, cancellationToken, "RAW mirror");
    }

    private static async Task<bool> ThreeLevelContextMatchesAsync(AppDbContext db,
        ModuleCaseFile file, ThreeLevelUploadTarget target, CancellationToken cancellationToken)
    {
        return target.Kind switch
        {
            ThreeLevelUploadKind.CaseAttachment => file.CaseId == target.Id,
            ThreeLevelUploadKind.CaseAttachmentByNo => file.RecordId == target.Id &&
                string.Equals(file.CaseNo, target.Number, StringComparison.OrdinalIgnoreCase),
            ThreeLevelUploadKind.TaskAttachment or ThreeLevelUploadKind.TestReport =>
                file.TaskId == target.Id,
            ThreeLevelUploadKind.TaskAttachmentByNo or ThreeLevelUploadKind.TestReportByTaskNo =>
                file.CaseId == target.Id &&
                string.Equals(file.TaskNo, target.Number, StringComparison.OrdinalIgnoreCase),
            ThreeLevelUploadKind.TestReportVersion => await db.ModuleCaseFiles.AsNoTracking()
                .AnyAsync(x => x.Id == target.Id && x.FileKind == ModuleCaseFileKinds.TestReport &&
                    x.DocumentId == file.DocumentId && x.RecordId == file.RecordId &&
                    x.CaseId == file.CaseId && x.TaskId == file.TaskId, cancellationToken),
            _ => false
        };
    }

    private static async Task<CommittedStagingInspection> InspectPhysicalFileAsync(string path,
        long expectedSize, string expectedHash, CancellationToken cancellationToken,
        string label = "formal file")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Inspection(CommittedStagingDryRunCategory.FormalFileMissing,
                $"the {label} does not exist");
        var info = new FileInfo(path);
        if (info.Length != expectedSize)
            return Inspection(CommittedStagingDryRunCategory.FileMismatch,
                $"the {label} size does not match the submission");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? Inspection(CommittedStagingDryRunCategory.SafeToClean,
                $"the {label} is verified", true)
            : Inspection(CommittedStagingDryRunCategory.FileMismatch,
                $"the {label} SHA256 does not match the submission");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(Uri.IsHexDigit);

    private static DateTime? NormalizeCutoff(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)
    };

    private static CommittedStagingInspection Inspection(
        CommittedStagingDryRunCategory category, string reason, bool formalFileSafe = false) =>
        new(category, reason, formalFileSafe);

    private sealed record CommittedStagingInspection(
        CommittedStagingDryRunCategory Category, string Reason, bool FormalFileSafe);

    private async Task<bool> CleanCompletedEquipmentStagingAsync(Guid id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var item = await LoadForUpdateAsync(db, id, cancellationToken);
        if (item.StagingStorageKey is null) return false;

        string? reason = null;
        if (item.SourceModuleCode != "equipment" || item.ContextType != "Asset" ||
            item.Status != ApprovalStatus.Committed)
            reason = "submission is not a committed equipment Asset submission";
        else if (!Guid.TryParseExact(item.CommitResultKey, "N", out var resultId) || resultId != item.Id)
            reason = "formal commit result is missing or does not match the submission";
        else if (item.ReviewerAccount is null || item.ReviewedAt is null ||
            item.CommitLeaseToken is not null || item.CommitLeaseExpiresAt is not null)
            reason = "approval or commit processing is incomplete";
        else if (!await db.EquipmentDeletionJobs.AsNoTracking().AnyAsync(x =>
            x.EquipmentId == item.ContextId && x.Status == EquipmentDeletionStatuses.Completed &&
            x.DbRecordDeletedAt != null && x.CompletedAt != null, cancellationToken))
            reason = "equipment deletion is not complete";
        else if (await db.Apparatuses.AsNoTracking().AnyAsync(x => x.Id == item.ContextId,
            cancellationToken) || await db.ApparatusFiles.AsNoTracking().AnyAsync(x =>
            x.ApparatusId == item.ContextId, cancellationToken))
            reason = "equipment or formal attachment records still exist";
        else if (!await db.EquipmentFileDeletionJobs.AsNoTracking().AnyAsync(x =>
            x.ApparatusFileId == item.Id && x.EquipmentId == item.ContextId &&
            x.Status == EquipmentFileDeletionStatuses.Completed && x.DbRecordDeletedAt != null &&
            x.PhysicalFileDeletedAt != null && x.CompletedAt != null, cancellationToken))
            reason = "the matching formal attachment deletion is not complete";
        else if (await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
            x.SourceEntityType == "ApparatusFile" && x.SourceEntityId == item.Id,
            cancellationToken))
            reason = "a KM outbox record still references the attachment";
        else
        {
            var auditActions = await db.ApprovalAuditEvents.AsNoTracking()
                .Where(x => x.SubmissionId == item.Id)
                .Select(x => x.Action).ToListAsync(cancellationToken);
            if (!auditActions.Contains(ApprovalAuditActions.Submitted) ||
                !auditActions.Contains(ApprovalAuditActions.Approved) ||
                !auditActions.Contains(ApprovalAuditActions.Committed))
                reason = "required approval audit history is incomplete";
        }

        if (reason is null && await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
            x.Id != item.Id && x.StagingStorageKey == item.StagingStorageKey, cancellationToken))
            reason = "the staging key is referenced by another submission";

        if (reason is not null)
        {
            logger?.LogWarning(
                "Skipped completed equipment staging cleanup for submission {SubmissionId}: {Reason}",
                id, reason);
            return false;
        }

        await stagingStorage.DeleteAsync(id, item.StagingStorageKey, cancellationToken);
        item.ClearCommittedEquipmentStagingFile(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
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

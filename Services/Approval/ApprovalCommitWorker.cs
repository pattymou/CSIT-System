using System.Data;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed class ApprovalCommitWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IApprovalCommitHandlerRegistry handlers,
    ILogger<ApprovalCommitWorker> logger,
    AppDbContext? activeDb = null)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task ProcessAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        var lease = Guid.NewGuid();
        PendingSubmission submission;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            submission = await LoadForUpdateAsync(db, submissionId, cancellationToken);
            if (submission.Status == ApprovalStatus.Committed) return;
            if (submission.Status != ApprovalStatus.ApprovedPendingCommit) return;
            // Resolve before claiming. Unknown modules fail closed and do not become formal data.
            handlers.Resolve(submission.SourceModuleCode);
            var now = DateTime.UtcNow;
            submission.StartCommit(lease, now, LeaseDuration);
            db.ApprovalAuditEvents.Add(ApprovalSubmissionService.Event(submission,
                ApprovalAuditActions.CommitStarted, "ApprovalWorker", now,
                ApprovalStatus.ApprovedPendingCommit, ApprovalStatus.ApprovedPendingCommit));
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            var handler = handlers.Resolve(submission.SourceModuleCode);
            await handler.ValidateAsync(submission, cancellationToken);
            var isTestReport = submission.ContextType is (nameof(ThreeLevelUploadKind.TestReport)
                    or nameof(ThreeLevelUploadKind.TestReportByTaskNo)
                    or nameof(ThreeLevelUploadKind.TestReportVersion));
            if (isTestReport)
            {
                if (activeDb is null || !activeDb.Database.IsRelational())
                    throw new InvalidOperationException("Test Report commit requires a shared relational transaction.");
                await CommitTestReportAtomicallyAsync(handler, submission, lease, cancellationToken);
            }
            else
            {
                var result = await handler.CommitAsync(submission, cancellationToken);
                await FinishAsync(submissionId, lease, result.ResultKey, null, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supervisor approval commit failed for submission {SubmissionId}", submissionId);
            // Persist and return only a fixed, credential-free message.
            await FinishAsync(submissionId, lease, null, "正式提交失敗，請由主管重試或聯絡系統管理員。", cancellationToken);
        }
    }

    private async Task CommitTestReportAtomicallyAsync(IApprovalCommitHandler handler,
        PendingSubmission claimed, Guid lease, CancellationToken cancellationToken)
    {
        var db = activeDb ?? throw new InvalidOperationException("A shared report context is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var result = await handler.CommitAsync(claimed, cancellationToken);
            if (!Guid.TryParse(result.ResultKey, out var fileId))
                throw new InvalidOperationException("Committed report result key is invalid.");
            var file = await db.ModuleCaseFiles.SingleAsync(x => x.Id == fileId, cancellationToken);
            if (file.FileKind != ModuleCaseFileKinds.TestReport ||
                file.ReviewStatus != TestReportReviewStatuses.Approved || !file.IsRawDataExported)
                throw new InvalidOperationException("Formal test report is incomplete.");
            var submission = await db.PendingSubmissions
                .FromSqlInterpolated($"SELECT * FROM pending_submissions WHERE id = {claimed.Id} FOR UPDATE")
                .SingleAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var existing = await db.KmIntegrationOutbox.SingleOrDefaultAsync(x =>
                x.SourceSystem == "CSIT" && x.DocumentId == file.DocumentId &&
                x.Version == file.VersionNo, cancellationToken);
            if (existing is not null && (existing.SourceEntityId != file.Id ||
                existing.EventType != "TestReportCommitted"))
                throw new InvalidOperationException("KM outbox business key belongs to another formal report.");
            submission.Commit(lease, result.ResultKey, now);
            if (existing is null)
                db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(file, now));
            db.ApprovalAuditEvents.Add(ApprovalSubmissionService.Event(submission,
                ApprovalAuditActions.Committed, "ApprovalWorker", now,
                ApprovalStatus.ApprovedPendingCommit, ApprovalStatus.Committed));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!handlers.HasAnyHandler) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var ids = await db.PendingSubmissions.AsNoTracking()
            .Where(x => x.Status == ApprovalStatus.ApprovedPendingCommit &&
                (x.CommitLeaseExpiresAt == null || x.CommitLeaseExpiresAt < now))
            .OrderBy(x => x.ReviewedAt).Select(x => x.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try { await ProcessAsync(id, cancellationToken); }
            catch (InvalidOperationException) { /* An expired/competing lease will be retried later. */ }
        }
        return ids.Count;
    }

    private async Task FinishAsync(Guid id, Guid lease, string? resultKey, string? safeError,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var submission = await LoadForUpdateAsync(db, id, cancellationToken);
        var now = DateTime.UtcNow;
        if (submission.Status == ApprovalStatus.Committed && safeError is not null) return;
        if (safeError is null) submission.Commit(lease, resultKey ?? string.Empty, now);
        else submission.FailCommit(lease, safeError, now);
        db.ApprovalAuditEvents.Add(ApprovalSubmissionService.Event(submission,
            safeError is null ? ApprovalAuditActions.Committed : ApprovalAuditActions.CommitFailed,
            "ApprovalWorker", now, ApprovalStatus.ApprovedPendingCommit, submission.Status,
            details: safeError));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static Task<PendingSubmission> LoadForUpdateAsync(AppDbContext db, Guid id,
        CancellationToken cancellationToken) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? db.PendingSubmissions
                .FromSqlInterpolated($"SELECT * FROM pending_submissions WHERE id = {id} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : db.PendingSubmissions.SingleAsync(x => x.Id == id, cancellationToken);
}

public sealed class ApprovalCommitBackgroundService(IServiceScopeFactory scopeFactory,
    ILogger<ApprovalCommitBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ApprovalCommitWorker>()
                    .ProcessBatchAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<ApprovalSubmissionService>()
                    .CleanInactiveStagingBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Supervisor approval commit worker failed."); }
        }
    }
}

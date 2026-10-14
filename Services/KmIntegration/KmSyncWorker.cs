using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmSyncWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmIntegrationClient client,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmSyncWorker> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        if (!options.Enabled) return 0;
        ValidateOptions(options);
        var count = 0;
        for (var i = 0; i < 20; i++)
        {
            var claim = await ClaimNextAsync(options, cancellationToken);
            if (claim is null) break;
            await DispatchAsync(claim.Value.Id, claim.Value.Lease, options, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<(Guid Id, Guid Lease)?> ClaimNextAsync(KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var now = DateTime.UtcNow;
        KmIntegrationOutbox? row;
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE km_integration_outbox SET status = 'Failed', lease_token = NULL,
                    lease_expires_at = NULL, next_retry_at = NULL,
                    last_error_code = 'RETRY_EXHAUSTED',
                    last_error_message = 'KM delivery retry limit reached.', updated_at = {now}
                WHERE status = 'Dispatching' AND lease_expires_at <= {now}
                    AND retry_count >= {options.MaxRetryCount}
                """, cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction!.GetDbTransaction();
            command.CommandText = """
                SELECT id FROM km_integration_outbox
                WHERE (status = 'Pending' OR retry_count < @max) AND
                  (status = 'Pending' OR
                   (status = 'Failed' AND next_retry_at <= @now) OR
                   (status = 'Dispatching' AND lease_expires_at <= @now))
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                """;
            var max = command.CreateParameter(); max.ParameterName = "max"; max.Value = options.MaxRetryCount;
            var at = command.CreateParameter(); at.ParameterName = "now"; at.Value = now;
            command.Parameters.Add(max); command.Parameters.Add(at);
            var id = await command.ExecuteScalarAsync(cancellationToken);
            row = id is Guid value
                ? await db.KmIntegrationOutbox.SingleAsync(x => x.Id == value, cancellationToken) : null;
        }
        else
        {
            row = await db.KmIntegrationOutbox.OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(x => (x.Status == KmIntegrationStatus.Pending ||
                    x.RetryCount < options.MaxRetryCount) &&
                    (x.Status == KmIntegrationStatus.Pending ||
                     (x.Status == KmIntegrationStatus.Failed && x.NextRetryAt <= now) ||
                     (x.Status == KmIntegrationStatus.Dispatching && x.LeaseExpiresAt <= now)), cancellationToken);
        }
        if (row is null) return null;
        var lease = Guid.NewGuid();
        row.Claim(lease, now, TimeSpan.FromSeconds(options.LeaseSeconds));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("KM outbox claimed EventId={EventId} DocumentId={DocumentId} Version={Version} Attempt={Attempt} Status={Status}",
            row.EventId, row.DocumentId, row.Version, row.RetryCount + 1, row.Status);
        return (row.Id, lease);
    }

    private async Task DispatchAsync(Guid id, Guid lease, KmIntegrationOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.LeaseSeconds - 5));
        KmDeliveryResult result;
        try
        {
            KmIngestionRequest? request;
            await using (var db = await dbFactory.CreateDbContextAsync(deadline.Token))
            {
                var row = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.Id == id, deadline.Token);
                request = await BuildRequestAsync(db, row, deadline.Token);
            }
            result = request is null
                ? KmDeliveryResult.Failure(false, "DOCUMENT_NOT_COMMITTED", "Formal test report is unavailable.")
                : await client.SubmitAsync(request, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = KmDeliveryResult.Failure(true, "LEASE_TIMEOUT", "KM delivery exceeded its lease deadline.");
        }

        await using var update = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = update.Database.IsRelational()
            ? await update.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var current = update.Database.IsRelational()
            ? await update.KmIntegrationOutbox
                .FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE id = {id} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : await update.KmIntegrationOutbox.SingleAsync(x => x.Id == id, cancellationToken);
        if (current.LeaseToken != lease || current.Status != KmIntegrationStatus.Dispatching) return;
        var now = DateTime.UtcNow;
        if (result.Accepted)
            current.Sent(lease, result.JobId!, now);
        else
        {
            var schedule = options.RetryScheduleSeconds;
            var delay = schedule[Math.Min(current.RetryCount, schedule.Length - 1)];
            var next = result.Retryable && current.RetryCount < options.MaxRetryCount
                ? now.AddSeconds(delay) : (DateTime?)null;
            current.DeliveryFailed(lease, result.ErrorCode ?? "UNKNOWN",
                result.ErrorMessage ?? "KM delivery failed.", next, now);
        }
        await update.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("KM outbox transition EventId={EventId} DocumentId={DocumentId} Version={Version} KmJobId={KmJobId} Attempt={Attempt} Status={Status} ErrorCode={ErrorCode}",
            current.EventId, current.DocumentId, current.Version, current.KmJobId,
            current.RetryCount + 1, current.Status, current.LastErrorCode);
    }

    private static async Task<KmIngestionRequest?> BuildRequestAsync(AppDbContext db,
        KmIntegrationOutbox row, CancellationToken cancellationToken)
    {
        var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == row.SourceEntityId && x.DocumentId == row.DocumentId && x.VersionNo == row.Version &&
            x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported,
            cancellationToken);
        if (file is null || await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
            x.Id == file.Id && x.Status != ApprovalStatus.Committed, cancellationToken)) return null;
        var project = await db.ModuleRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ProjectId, cancellationToken);
        var caseEntity = row.CaseId.HasValue
            ? await db.ModuleRecordCases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.CaseId.Value, cancellationToken)
            : null;
        var task = row.TaskId.HasValue
            ? await db.ModuleRecordTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.TaskId.Value, cancellationToken)
            : row.CaseId.HasValue && !string.IsNullOrWhiteSpace(file.TaskNo)
                ? await db.ModuleRecordTasks.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.CaseId == row.CaseId.Value && x.TaskNo == file.TaskNo, cancellationToken)
                : null;
        if (project is null || caseEntity is null || task is null || task.CaseId != caseEntity.Id ||
            caseEntity.RecordId != project.Id) return null;
        return new KmIngestionRequest(row.EventId, row.EventType, row.SourceSystem,
            row.DocumentId, row.Version,
            new KmReference(project.Id, project.RecordNo, project.Name),
            new KmReference(caseEntity.Id, caseEntity.CaseNo, caseEntity.Name),
            new KmReference(task.Id, task.TaskNo, task.Name),
            new KmDocumentMetadata(row.FileName, row.ContentType, row.SizeBytes), row.CommittedAt);
    }

    private static void ValidateOptions(KmIntegrationOptions options)
    {
        if (options.MaxRetryCount < 1 || options.LeaseSeconds <= options.TimeoutSeconds + 5 ||
            options.PollSeconds < 1 || options.TimeoutSeconds < 1 ||
            options.RetryScheduleSeconds.Length == 0 || options.RetryScheduleSeconds.Any(x => x < 1))
            throw new InvalidOperationException("KM integration retry or lease configuration is invalid.");
    }
}

public sealed class KmSyncBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(1, configured.Value.PollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configured.Value.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var processed = await scope.ServiceProvider.GetRequiredService<KmSyncWorker>()
                    .ProcessBatchAsync(stoppingToken);
                if (processed > 0)
                {
                    var snapshot = await scope.ServiceProvider.GetRequiredService<KmSyncDiagnostics>()
                        .ReadAsync(stoppingToken);
                    logger.LogInformation("KM outbox summary Pending={Pending} Failed={Failed} RetryCount={RetryCount} LastAttemptAt={LastAttemptAt} LastErrorCode={LastErrorCode}",
                        snapshot.PendingCount, snapshot.FailedCount, snapshot.RetryCount,
                        snapshot.LastAttemptAt, snapshot.LastErrorCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "KM synchronization worker failed."); }
        }
    }
}

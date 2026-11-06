using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmMetadataChangeWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmIntegrationClient client,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmMetadataChangeWorker> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        if (!options.Enabled) return 0;
        Validate(options);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.KmMetadataChanges.AsNoTracking().Where(x =>
                x.RetryCount < options.MaxRetryCount &&
                (x.Status == KmMetadataChangeStatuses.Pending ||
                 x.Status == KmMetadataChangeStatuses.Failed && x.LastErrorRetryable == true &&
                    x.NextRetryAt <= now ||
                 x.Status == KmMetadataChangeStatuses.Dispatching && x.LeaseExpiresAt <= now))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.ChangeId).Select(x => x.ChangeId)
            .Take(25).ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var id in ids)
            if (await DispatchAsync(id, cancellationToken)) processed++;
        return processed;
    }

    public async Task<bool> DispatchAsync(Guid changeId,
        CancellationToken cancellationToken = default)
    {
        var options = configured.Value;
        if (!options.Enabled) return false;
        Validate(options);
        Guid lease;
        KmMetadataChangeRequest request;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            var row = IsPostgres(db)
                ? await db.KmMetadataChanges.FromSqlInterpolated(
                    $"SELECT * FROM km_metadata_changes WHERE change_id = {changeId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.KmMetadataChanges.SingleOrDefaultAsync(x => x.ChangeId == changeId,
                    cancellationToken);
            if (row is null || row.RetryCount >= options.MaxRetryCount) return false;
            try { lease = row.Claim(DateTime.UtcNow, TimeSpan.FromSeconds(options.LeaseSeconds)); }
            catch (InvalidOperationException) { return false; }
            request = KmMetadataChangeQueueService.ToRequest(row);
            if (KmMetadataChangeQueueService.Sha(request) != row.PayloadSha256)
            {
                row.DeliveryFailed(lease, "PAYLOAD_INTEGRITY", "Stored metadata payload hash does not match.",
                    false, null, DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return false;
            }
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        var result = await client.SubmitMetadataChangeAsync(request, cancellationToken);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            var row = IsPostgres(db)
                ? await db.KmMetadataChanges.FromSqlInterpolated(
                    $"SELECT * FROM km_metadata_changes WHERE change_id = {changeId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.KmMetadataChanges.SingleOrDefaultAsync(x => x.ChangeId == changeId,
                    cancellationToken);
            if (row is null || row.Status != KmMetadataChangeStatuses.Dispatching ||
                row.LeaseToken != lease) return false;
            var now = DateTime.UtcNow;
            if (result.Accepted) row.DeliveryAccepted(lease, result.JobId!, now);
            else
            {
                var retryable = result.Retryable && row.RetryCount < options.MaxRetryCount;
                row.DeliveryFailed(lease, result.ErrorCode ?? "UNKNOWN",
                    result.ErrorMessage ?? "KM metadata change delivery failed.", retryable,
                    retryable ? NextRetry(options, row.RetryCount, now) : null, now);
            }
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        if (!result.Accepted)
            logger.LogWarning("KM metadata change {ChangeId} delivery failed: {Code}",
                changeId, result.ErrorCode);
        return true;
    }

    internal static DateTime NextRetry(KmIntegrationOptions options, int attempt, DateTime now)
    {
        var index = Math.Clamp(attempt - 1, 0, options.RetryScheduleSeconds.Length - 1);
        return now.AddSeconds(options.RetryScheduleSeconds[index]);
    }
    private static bool IsPostgres(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    private static void Validate(KmIntegrationOptions options)
    {
        if (options.MaxRetryCount < 1 || options.TimeoutSeconds < 1 ||
            options.LeaseSeconds <= options.TimeoutSeconds + 5 ||
            options.RetryScheduleSeconds.Length == 0 || options.RetryScheduleSeconds.Any(x => x < 1))
            throw new InvalidOperationException("KM metadata change retry configuration is invalid.");
    }
}

public sealed class KmMetadataChangeBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<KmIntegrationOptions> configured,
    ILogger<KmMetadataChangeBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Max(1, configured.Value.PollSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configured.Value.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<KmMetadataChangeWorker>()
                    .ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "KM metadata change worker failed."); }
        }
    }
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// Controlled integration seam pending Anderson KM v1.2 acceptance. Keep KmDeletion.Enabled=false
// until the external deletion API and end-to-end callback flow have passed integration acceptance.
public sealed class KmDeletionDispatchService(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmDeletionClient client,
    IOptions<KmDeletionOptions> deletion,
    IOptions<KmIntegrationOptions> integration)
{
    public async Task<int> ProcessBatchAsync(int maxCount = 20,
        CancellationToken cancellationToken = default)
        => await ProcessAsync(null, maxCount, cancellationToken);

    public async Task<int> ProcessJobAsync(Guid jobId, int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID is required.", nameof(jobId));
        return await ProcessAsync(jobId, maxCount, cancellationToken);
    }

    private async Task<int> ProcessAsync(Guid? jobId, int maxCount,
        CancellationToken cancellationToken)
    {
        if (!deletion.Value.Enabled) return 0;
        if (maxCount is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        ValidateConfiguration();

        var now = DateTime.UtcNow;
        List<Guid> candidateIds;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            candidateIds = await db.VerificationCaseDeletionTargets.AsNoTracking()
                .Where(target => (!jobId.HasValue || target.JobId == jobId.Value) &&
                    target.AttemptCount < integration.Value.MaxRetryCount &&
                    db.VerificationCaseDeletionJobs.Any(job => job.Id == target.JobId &&
                        job.Status == "WaitingForKm") &&
                    (target.Status == "Pending" ||
                     (target.Status == "DeliveryFailed" || target.Status == "Failed") &&
                        target.LastErrorRetryable == true ||
                     target.Status == "Dispatching" && target.LeaseExpiresAt <= now))
                .OrderBy(target => target.RequestedAt).ThenBy(target => target.Id)
                .Select(target => target.Id).Take(maxCount).ToListAsync(cancellationToken);
        }

        var accepted = 0;
        foreach (var targetId in candidateIds)
            if (await DispatchAsync(targetId, cancellationToken)) accepted++;
        return accepted;
    }

    public async Task<bool> DispatchAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        if (!deletion.Value.Enabled) return false;
        ValidateConfiguration();
        Guid lease;
        KmDeletionRequest request;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var target = db.Database.IsRelational()
                ? await db.VerificationCaseDeletionTargets.FromSqlInterpolated($"""
                    SELECT * FROM verification_case_deletion_targets WHERE id = {targetId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.VerificationCaseDeletionTargets.SingleOrDefaultAsync(x => x.Id == targetId,
                    cancellationToken);
            if (target is null || target.AttemptCount >= integration.Value.MaxRetryCount) return false;
            var job = await db.VerificationCaseDeletionJobs.AsNoTracking()
                .SingleAsync(x => x.Id == target.JobId, cancellationToken);
            if (job.Status != "WaitingForKm") return false;
            try { lease = target.ClaimDispatch(DateTime.UtcNow,
                TimeSpan.FromSeconds(integration.Value.LeaseSeconds)); }
            catch (InvalidOperationException) { return false; }
            request = target.ToRequest();
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        var result = await client.SubmitAsync(request, cancellationToken);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var target = db.Database.IsRelational()
                ? await db.VerificationCaseDeletionTargets.FromSqlInterpolated($"""
                    SELECT * FROM verification_case_deletion_targets WHERE id = {targetId} FOR UPDATE
                    """).SingleAsync(cancellationToken)
                : await db.VerificationCaseDeletionTargets.SingleAsync(x => x.Id == targetId,
                    cancellationToken);
            if (target.LeaseToken != lease || target.Status != "Dispatching") return false;
            if (result.Accepted) target.DeliveryAccepted(lease, result.JobId!, DateTime.UtcNow);
            else target.DeliveryFailed(lease, result.ErrorCode ?? "UNKNOWN", result.Retryable,
                DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        return result.Accepted;
    }

    private void ValidateConfiguration()
    {
        if (integration.Value.LeaseSeconds <= integration.Value.TimeoutSeconds + 5 ||
            integration.Value.MaxRetryCount < 1 || integration.Value.TimeoutSeconds < 1 ||
            string.IsNullOrWhiteSpace(deletion.Value.Scope) ||
            !Uri.TryCreate(integration.Value.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("KM deletion dispatch configuration is invalid.");
    }
}

public sealed class KmDeletionBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<KmDeletionOptions> deletion,
    IOptions<KmIntegrationOptions> integration,
    ILogger<KmDeletionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(1, integration.Value.PollSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!deletion.Value.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var accepted = await scope.ServiceProvider
                    .GetRequiredService<KmDeletionDispatchService>()
                    .ProcessBatchAsync(cancellationToken: stoppingToken);
                if (accepted > 0)
                    logger.LogInformation("KM deletion requests accepted Count={Count}.", accepted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "KM deletion dispatch worker failed.");
            }
        }
    }
}

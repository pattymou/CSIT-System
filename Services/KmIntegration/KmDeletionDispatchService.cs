using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// Controlled integration seam pending Anderson KM v1.2 acceptance. Keep KmDeletion.Enabled=false
// until the external deletion API and end-to-end callback flow have passed integration acceptance.
public sealed class KmDeletionDispatchService(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmDeletionClient client,
    IOptions<KmDeletionOptions> deletion,
    IOptions<KmIntegrationOptions> integration,
    IEquipmentDocumentDeletionAdapter? equipmentAdapter = null)
{
    public async Task<int> ProcessBatchAsync(int maxCount = 20,
        CancellationToken cancellationToken = default)
    {
        var caseCount = await ProcessAsync(null, maxCount, cancellationToken);
        var remaining = Math.Max(0, maxCount - caseCount);
        return caseCount + (remaining == 0 ? 0 :
            await ProcessEquipmentBatchAsync(remaining, cancellationToken));
    }

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

    private async Task<int> ProcessEquipmentBatchAsync(int maxCount,
        CancellationToken cancellationToken)
    {
        if (!deletion.Value.Enabled || !deletion.Value.EquipmentEnabled) return 0;
        var now = DateTime.UtcNow;
        List<Guid> candidateIds;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            candidateIds = await db.EquipmentFileDeletionJobs.AsNoTracking()
                .Where(job => job.AttemptCount < integration.Value.MaxRetryCount &&
                    (job.Status == EquipmentFileDeletionStatuses.Pending ||
                     job.Status == EquipmentFileDeletionStatuses.Failed &&
                        job.LastErrorRetryable == true ||
                     job.Status == EquipmentFileDeletionStatuses.Dispatching &&
                        job.LeaseExpiresAt <= now) &&
                    !db.KmIntegrationOutbox.Any(outbox =>
                        outbox.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                        outbox.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                        outbox.SourceEntityId == job.ApparatusFileId &&
                        outbox.Status == KmIntegrationStatus.Dispatching &&
                        outbox.LeaseExpiresAt > now))
                .OrderBy(job => job.RequestedAt).ThenBy(job => job.Id)
                .Select(job => job.Id).Take(maxCount).ToListAsync(cancellationToken);
        }
        var accepted = 0;
        foreach (var id in candidateIds)
            if (await DispatchEquipmentAsync(id, cancellationToken)) accepted++;
        return accepted;
    }

    public async Task<bool> DispatchEquipmentAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!deletion.Value.Enabled || !deletion.Value.EquipmentEnabled) return false;
        ValidateConfiguration();
        Guid lease;
        EquipmentDocumentDeletionRequest request;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var job = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM equipment_file_deletion_jobs WHERE id = {jobId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.EquipmentFileDeletionJobs.SingleOrDefaultAsync(x => x.Id == jobId,
                    cancellationToken);
            if (job is null || job.AttemptCount >= integration.Value.MaxRetryCount ||
                !await db.ApparatusFiles.AnyAsync(x => x.Id == job.ApparatusFileId,
                    cancellationToken)) return false;
            var now = DateTime.UtcNow;
            var ingestion = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.KmIntegrationOutbox.FromSqlInterpolated($"""
                    SELECT * FROM km_integration_outbox
                    WHERE event_type = 'EquipmentDocumentArchived'
                      AND source_entity_type = 'ApparatusFile'
                      AND source_entity_id = {job.ApparatusFileId}
                    FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.KmIntegrationOutbox.SingleOrDefaultAsync(outbox =>
                    outbox.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                    outbox.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                    outbox.SourceEntityId == job.ApparatusFileId, cancellationToken);
            if (ingestion?.Status == KmIntegrationStatus.Dispatching)
            {
                if (ingestion.LeaseExpiresAt > now) return false;
                if (ingestion.LeaseToken.HasValue)
                    ingestion.DeliveryFailed(ingestion.LeaseToken.Value,
                        "CANCELLED_BY_DELETION",
                        "Equipment deletion took ownership after the ingestion lease expired.",
                        null, now);
            }
            try { lease = job.ClaimDispatch(now,
                TimeSpan.FromSeconds(integration.Value.LeaseSeconds)); }
            catch (InvalidOperationException) { return false; }
            request = job.ToRequest();
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        var adapter = equipmentAdapter ?? new UnavailableEquipmentDocumentDeletionAdapter();
        var result = await adapter.SubmitAsync(request, cancellationToken);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var job = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM equipment_file_deletion_jobs WHERE id = {jobId} FOR UPDATE
                    """).SingleAsync(cancellationToken)
                : await db.EquipmentFileDeletionJobs.SingleAsync(x => x.Id == jobId,
                    cancellationToken);
            if (job.LeaseToken != lease || job.Status != EquipmentFileDeletionStatuses.Dispatching)
                return false;
            if (result.Accepted)
                job.DeliveryAccepted(lease, result.JobId!, DateTime.UtcNow);
            else
                job.DeliveryFailed(lease, result.ErrorCode ?? "UNKNOWN", null,
                    result.Retryable, DateTime.UtcNow);
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

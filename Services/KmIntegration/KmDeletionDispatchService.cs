using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

// Explicitly invoked only after the deletion gate is enabled. No hosted worker is registered.
public sealed class KmDeletionDispatchService(
    IDbContextFactory<AppDbContext> dbFactory,
    IKmDeletionClient client,
    IOptions<KmDeletionOptions> deletion,
    IOptions<KmIntegrationOptions> integration)
{
    public async Task<bool> DispatchAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        if (!deletion.Value.Enabled) return false;
        if (integration.Value.LeaseSeconds <= integration.Value.TimeoutSeconds + 5 ||
            integration.Value.MaxRetryCount < 1)
            throw new InvalidOperationException("KM deletion lease configuration is invalid.");
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
}

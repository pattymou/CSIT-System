using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public enum KmDeletionCallbackOutcome { Accepted, Invalid, NotFound, Conflict }

public sealed class KmDeletionCallbackService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<KmDeletionCallbackOutcome> ApplyAsync(KmDeletionCallback callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null || !callback.IsValid()) return KmDeletionCallbackOutcome.Invalid;
        var payloadHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(callback, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var target = db.Database.IsRelational()
            ? await db.VerificationCaseDeletionTargets.FromSqlInterpolated($"""
                SELECT * FROM verification_case_deletion_targets
                WHERE id = {callback.DeletionRequestId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.VerificationCaseDeletionTargets.SingleOrDefaultAsync(
                x => x.Id == callback.DeletionRequestId, cancellationToken);
        if (target is null) return KmDeletionCallbackOutcome.NotFound;
        if (target.Resource != callback.Resource ||
            !string.Equals(target.KmDeletionJobId, callback.KmDeletionJobId, StringComparison.Ordinal))
            return KmDeletionCallbackOutcome.Conflict;

        var receipt = await db.VerificationCaseDeletionCallbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (receipt is not null)
            return receipt.TargetId == target.Id && receipt.PayloadSha256 == payloadHash
                ? KmDeletionCallbackOutcome.Accepted : KmDeletionCallbackOutcome.Conflict;

        try { target.ApplyCallback(callback, DateTime.UtcNow); }
        catch (InvalidOperationException) { return KmDeletionCallbackOutcome.Conflict; }
        db.VerificationCaseDeletionCallbacks.Add(new VerificationCaseDeletionCallbackReceipt
        {
            CallbackId = callback.CallbackId, TargetId = target.Id,
            Status = callback.Status, PayloadSha256 = payloadHash, ReceivedAt = DateTime.UtcNow
        });
        if (target.Status == "Completed")
        {
            var job = await db.VerificationCaseDeletionJobs.Include(x => x.Targets)
                .SingleAsync(x => x.Id == target.JobId, cancellationToken);
            if (job.Status == "WaitingForKm" &&
                job.Targets.All(x => x.Status is "Completed" or "NotDispatched"))
                job.MarkKmVerified(DateTime.UtcNow);
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return KmDeletionCallbackOutcome.Conflict; }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return KmDeletionCallbackOutcome.Accepted;
    }
}

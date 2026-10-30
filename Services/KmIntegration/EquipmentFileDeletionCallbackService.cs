using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class EquipmentFileDeletionCallbackService(IDbContextFactory<AppDbContext> dbFactory,
    IRawDataExportService? rawDataExport = null)
{
    public async Task<KmDeletionCallbackOutcome> ApplyAsync(
        EquipmentDocumentDeletionCallback callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null || !callback.IsValid()) return KmDeletionCallbackOutcome.Invalid;
        var payloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            callback, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var shouldCleanup = false;
        string? equipmentId = null;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var job = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM equipment_file_deletion_jobs
                    WHERE id = {callback.DeletionRequestId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.EquipmentFileDeletionJobs.SingleOrDefaultAsync(
                    x => x.Id == callback.DeletionRequestId, cancellationToken);
            if (job is null) return KmDeletionCallbackOutcome.NotFound;
            equipmentId = job.EquipmentId;
            if (job.Resource != callback.Resource ||
                !string.Equals(job.KmDeletionJobId, callback.KmDeletionJobId,
                    StringComparison.Ordinal)) return KmDeletionCallbackOutcome.Conflict;

            var receipt = await db.EquipmentFileDeletionCallbacks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.DeletionJobId != job.Id || receipt.PayloadSha256 != payloadHash)
                    return KmDeletionCallbackOutcome.Conflict;
                shouldCleanup = job.Status is EquipmentFileDeletionStatuses.KmVerified or
                    EquipmentFileDeletionStatuses.LocalCleanup or
                    EquipmentFileDeletionStatuses.CleanupFailed;
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                try { job.ApplyCallback(callback, DateTime.UtcNow); }
                catch (InvalidOperationException) { return KmDeletionCallbackOutcome.Conflict; }
                db.EquipmentFileDeletionCallbacks.Add(new EquipmentFileDeletionCallbackReceipt
                {
                    CallbackId = callback.CallbackId, DeletionJobId = job.Id,
                    Status = callback.Status, PayloadSha256 = payloadHash,
                    ReceivedAt = DateTime.UtcNow
                });
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException) { return KmDeletionCallbackOutcome.Conflict; }
                shouldCleanup = job.Status == EquipmentFileDeletionStatuses.KmVerified;
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
        }

        if (shouldCleanup)
        {
            await using var cleanupDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            await new EquipmentFileDeletionService(cleanupDb)
                .TryCompleteLocalCleanupAsync(callback.DeletionRequestId, cancellationToken);
            if (equipmentId is not null)
                await new EquipmentDeletionService(cleanupDb,
                        new EquipmentFileDeletionService(cleanupDb), new ManagedAssetFileStorage(cleanupDb),
                        rawDataExport)
                    .TryAdvanceByEquipmentIdAsync(equipmentId, cancellationToken);
        }
        return KmDeletionCallbackOutcome.Accepted;
    }
}

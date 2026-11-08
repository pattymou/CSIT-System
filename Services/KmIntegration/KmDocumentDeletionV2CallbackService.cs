using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmDocumentDeletionV2CallbackService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRawDataExportService? rawDataExport = null)
{
    public async Task<KmDeletionCallbackOutcome> ApplyAsync(KmDocumentDeletionV2Callback callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null || !callback.IsValid()) return KmDeletionCallbackOutcome.Invalid;
        var payloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            callback, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        return callback.Resource.SourceType switch
        {
            KmDocumentDeletionV2SourceTypes.VendorFile =>
                await ApplyVendorAsync(callback, payloadHash, cancellationToken),
            KmDocumentDeletionV2SourceTypes.EquipmentAttachment =>
                await ApplyEquipmentAsync(callback, payloadHash, cancellationToken),
            _ => KmDeletionCallbackOutcome.Invalid
        };
    }

    private async Task<KmDeletionCallbackOutcome> ApplyVendorAsync(
        KmDocumentDeletionV2Callback callback, string payloadHash,
        CancellationToken cancellationToken)
    {
        var shouldCleanup = false;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var job = IsPostgres(db)
                ? await db.VendorFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM vendor_file_deletion_jobs
                    WHERE id = {callback.DeletionRequestId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.VendorFileDeletionJobs.SingleOrDefaultAsync(x =>
                    x.Id == callback.DeletionRequestId, cancellationToken);
            if (job is null) return KmDeletionCallbackOutcome.NotFound;
            if (job.Resource != callback.Resource || job.KmDeletionJobId != callback.KmDeletionJobId)
                return KmDeletionCallbackOutcome.Conflict;
            var duplicate = await CheckDuplicateAsync(db, callback, payloadHash, cancellationToken);
            if (duplicate == KmDeletionCallbackOutcome.Conflict) return duplicate.Value;
            if (duplicate == KmDeletionCallbackOutcome.Accepted)
                shouldCleanup = job.Status is VendorFileDeletionStatuses.KmVerified or
                    VendorFileDeletionStatuses.LocalCleanup or VendorFileDeletionStatuses.CleanupFailed;
            else
            {
                try { job.ApplyCallback(callback, DateTime.UtcNow); }
                catch (InvalidOperationException) { return KmDeletionCallbackOutcome.Conflict; }
                AddReceipt(db, callback, payloadHash);
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException) { return KmDeletionCallbackOutcome.Conflict; }
                shouldCleanup = job.Status == VendorFileDeletionStatuses.KmVerified;
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        if (shouldCleanup)
        {
            await using var cleanupDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            await new VendorFileDeletionService(cleanupDb)
                .TryCompleteLocalCleanupAsync(callback.DeletionRequestId, cancellationToken);
        }
        return KmDeletionCallbackOutcome.Accepted;
    }

    private async Task<KmDeletionCallbackOutcome> ApplyEquipmentAsync(
        KmDocumentDeletionV2Callback callback, string payloadHash,
        CancellationToken cancellationToken)
    {
        var shouldCleanup = false;
        string? equipmentId = null;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
            var job = IsPostgres(db)
                ? await db.EquipmentFileDeletionJobs.FromSqlInterpolated($"""
                    SELECT * FROM equipment_file_deletion_jobs
                    WHERE id = {callback.DeletionRequestId} FOR UPDATE
                    """).SingleOrDefaultAsync(cancellationToken)
                : await db.EquipmentFileDeletionJobs.SingleOrDefaultAsync(x =>
                    x.Id == callback.DeletionRequestId, cancellationToken);
            if (job is null) return KmDeletionCallbackOutcome.NotFound;
            equipmentId = job.EquipmentId;
            if (job.Resource != callback.Resource || job.KmDeletionJobId != callback.KmDeletionJobId)
                return KmDeletionCallbackOutcome.Conflict;
            var duplicate = await CheckDuplicateAsync(db, callback, payloadHash, cancellationToken);
            if (duplicate == KmDeletionCallbackOutcome.Conflict) return duplicate.Value;
            if (duplicate == KmDeletionCallbackOutcome.Accepted)
                shouldCleanup = job.Status is EquipmentFileDeletionStatuses.KmVerified or
                    EquipmentFileDeletionStatuses.LocalCleanup or EquipmentFileDeletionStatuses.CleanupFailed;
            else
            {
                try { job.ApplyCallback(callback, DateTime.UtcNow); }
                catch (InvalidOperationException) { return KmDeletionCallbackOutcome.Conflict; }
                AddReceipt(db, callback, payloadHash);
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException) { return KmDeletionCallbackOutcome.Conflict; }
                shouldCleanup = job.Status == EquipmentFileDeletionStatuses.KmVerified;
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        if (shouldCleanup)
        {
            await using var cleanupDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            await new EquipmentFileDeletionService(cleanupDb)
                .TryCompleteLocalCleanupAsync(callback.DeletionRequestId, cancellationToken);
            if (equipmentId is not null)
                await new EquipmentDeletionService(cleanupDb,
                        new EquipmentFileDeletionService(cleanupDb),
                        new ManagedAssetFileStorage(cleanupDb), rawDataExport)
                    .TryAdvanceByEquipmentIdAsync(equipmentId, cancellationToken);
        }
        return KmDeletionCallbackOutcome.Accepted;
    }

    private static async Task<KmDeletionCallbackOutcome?> CheckDuplicateAsync(AppDbContext db,
        KmDocumentDeletionV2Callback callback, string payloadHash,
        CancellationToken cancellationToken)
    {
        var receipt = await db.KmDocumentDeletionV2Callbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (receipt is null) return null;
        return receipt.DeletionRequestId == callback.DeletionRequestId &&
            receipt.SourceType == callback.Resource.SourceType &&
            receipt.PayloadSha256 == payloadHash
            ? KmDeletionCallbackOutcome.Accepted : KmDeletionCallbackOutcome.Conflict;
    }

    private static void AddReceipt(AppDbContext db, KmDocumentDeletionV2Callback callback,
        string payloadHash) => db.KmDocumentDeletionV2Callbacks.Add(new()
    {
        CallbackId = callback.CallbackId, DeletionRequestId = callback.DeletionRequestId,
        SourceType = callback.Resource.SourceType, Status = callback.Status,
        PayloadSha256 = payloadHash, ReceivedAt = DateTime.UtcNow
    });

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}

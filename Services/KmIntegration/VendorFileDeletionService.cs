using System.Data;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record VendorFileDeletionRequestResult(Guid RequestId, string Status,
    bool CompletedLocally);

public sealed class VendorFileDeletionService(AppDbContext db,
    ILogger<VendorFileDeletionService>? logger = null)
{
    public async Task<VendorFileDeletionRequestResult?> RequestAsync(Guid fileId, string actor,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty) return null;
        actor = string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

        var existing = IsPostgres(db)
            ? await db.VendorFileDeletionJobs.FromSqlInterpolated($"""
                SELECT * FROM vendor_file_deletion_jobs
                WHERE vendor_file_id = {fileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.VendorFileDeletionJobs.SingleOrDefaultAsync(x =>
                x.VendorFileId == fileId, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            if (existing.Status is VendorFileDeletionStatuses.LocalCleanup or
                VendorFileDeletionStatuses.CleanupFailed or VendorFileDeletionStatuses.KmVerified)
                await TryCompleteLocalCleanupAsync(existing.Id, cancellationToken);
            var current = await db.VendorFileDeletionJobs.AsNoTracking()
                .SingleAsync(x => x.Id == existing.Id, cancellationToken);
            return new(current.Id, current.Status,
                current.Status == VendorFileDeletionStatuses.Completed);
        }

        var file = IsPostgres(db)
            ? await db.VendorFiles.FromSqlInterpolated($"""
                SELECT * FROM vendor_files WHERE id = {fileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.VendorFiles.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null) return null;
        if (file.ApprovalOperationId is { } approvalId)
        {
            var approved = await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                    x.Id == approvalId && x.SourceModuleCode == "vendor" &&
                    x.Status == ApprovalStatus.Committed && x.ReviewerAccount != null &&
                    x.ReviewedAt != null, cancellationToken) ||
                await db.ApprovalAuditEvents.AsNoTracking().AnyAsync(x =>
                    x.SubmissionId == approvalId && x.SourceModuleCode == "vendor" &&
                    x.Action == ApprovalAuditActions.ApprovalBypassed, cancellationToken);
            if (!approved || file.ArchiveStatus != FileArchiveStatuses.Archived ||
                file.ApprovedAt is null || string.IsNullOrWhiteSpace(file.ApprovedBy))
                throw new InvalidOperationException(
                    "Vendor attachment has no valid formal approval evidence for deletion.");
        }

        var outbox = IsPostgres(db)
            ? await db.KmIntegrationOutbox.FromSqlInterpolated($"""
                SELECT * FROM km_integration_outbox
                WHERE event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile'
                  AND source_entity_id = {fileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile &&
                x.SourceEntityId == fileId, cancellationToken);
        var requiresKmDeletion = outbox is not null &&
            !EquipmentFileDeletionService.CanProveNeverDispatched(outbox);
        var now = DateTime.UtcNow;
        var job = VendorFileDeletionJob.Create(file, actor, requiresKmDeletion, now,
            requiresKmDeletion ? outbox!.EventId : null);
        db.VendorFileDeletionJobs.Add(job);
        if (!requiresKmDeletion && outbox is not null) db.KmIntegrationOutbox.Remove(outbox);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        if (!requiresKmDeletion) await TryCompleteLocalCleanupAsync(job.Id, cancellationToken);
        var result = await db.VendorFileDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id, cancellationToken);
        return new(result.Id, result.Status,
            result.Status == VendorFileDeletionStatuses.Completed);
    }

    public async Task<bool> TryCompleteLocalCleanupAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;
        var job = IsPostgres(db)
            ? await db.VendorFileDeletionJobs.FromSqlInterpolated($"""
                SELECT * FROM vendor_file_deletion_jobs WHERE id = {jobId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.VendorFileDeletionJobs.SingleOrDefaultAsync(x => x.Id == jobId,
                cancellationToken);
        if (job is null || job.Status == VendorFileDeletionStatuses.Completed) return job is not null;
        if (job.Status is not (VendorFileDeletionStatuses.LocalCleanup or
            VendorFileDeletionStatuses.KmVerified or VendorFileDeletionStatuses.CleanupFailed)) return false;

        var file = IsPostgres(db)
            ? await db.VendorFiles.FromSqlInterpolated($"""
                SELECT * FROM vendor_files WHERE id = {job.VendorFileId} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.VendorFiles.SingleOrDefaultAsync(x => x.Id == job.VendorFileId,
                cancellationToken);
        if (file is null)
        {
            job.BeginLocalCleanup(DateTime.UtcNow);
            job.MarkLocalCleanupCompleted(false, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var fullPath = ManagedAssetFileStorage.FullPath(file.FolderPath, file.StoredFileName);
        var quarantinePath = fullPath + ".csit-delete-" + job.Id.ToString("N");
        var moved = false;
        var transactionCompleted = false;
        try
        {
            job.BeginLocalCleanup(DateTime.UtcNow);
            if (File.Exists(fullPath))
            {
                if (File.Exists(quarantinePath)) File.Delete(quarantinePath);
                File.Move(fullPath, quarantinePath);
                moved = true;
            }
            else if (File.Exists(quarantinePath)) moved = true;

            var outboxes = await db.KmIntegrationOutbox.Where(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile &&
                x.SourceEntityId == file.Id).ToListAsync(cancellationToken);
            db.KmIntegrationOutbox.RemoveRange(outboxes);
            db.VendorFiles.Remove(file);
            job.MarkLocalCleanupCompleted(moved || !File.Exists(fullPath), DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            transactionCompleted = true;
            if (moved && File.Exists(quarantinePath)) File.Delete(quarantinePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
        {
            logger?.LogWarning(ex, "Vendor file local cleanup failed JobId={JobId}", jobId);
            if (!transactionCompleted && transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            if (!transactionCompleted && moved && !File.Exists(fullPath) && File.Exists(quarantinePath))
            {
                try { File.Move(quarantinePath, fullPath); } catch { }
            }
            if (transactionCompleted) return true;
            db.ChangeTracker.Clear();
            var failed = await db.VendorFileDeletionJobs.SingleAsync(x => x.Id == jobId,
                cancellationToken);
            if (failed.Status is VendorFileDeletionStatuses.KmVerified or
                VendorFileDeletionStatuses.LocalCleanup or VendorFileDeletionStatuses.CleanupFailed)
            {
                if (failed.Status != VendorFileDeletionStatuses.LocalCleanup)
                    failed.BeginLocalCleanup(DateTime.UtcNow);
                failed.MarkCleanupFailed(ex is UnauthorizedAccessException ? "FILE_ACCESS_DENIED" :
                    ex is IOException ? "FILE_IO_FAILED" : "LOCAL_DB_CLEANUP_FAILED",
                    "Vendor attachment local cleanup failed and can be retried safely.", DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
            }
            return false;
        }
    }

    private static bool IsPostgres(AppDbContext context) =>
        context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}

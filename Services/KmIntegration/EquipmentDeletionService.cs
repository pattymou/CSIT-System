using System.Data;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class EquipmentDeletionBlockedException(IReadOnlyList<string> reasons)
    : InvalidOperationException(string.Join(Environment.NewLine, reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

public sealed class EquipmentDeletionService(
    AppDbContext db,
    EquipmentFileDeletionService fileDeletion,
    ManagedAssetFileStorage storage)
{
    public const string ReservationHistoryMessage =
        "無法刪除此設備。此設備已有預約歷史，為保留預約紀錄及稽核資料，禁止刪除設備主檔。建議將設備狀態改為『不可借用』。";
    public const string EnvironmentGroupMessage =
        "無法刪除此設備。此設備目前仍隸屬測試環境群組，請先至『測試環境群組設定』移除設備關聯，再執行刪除。";

    public async Task<EquipmentDeletionPreviewDto?> GetPreviewAsync(string equipmentId,
        CancellationToken cancellationToken = default)
    {
        var equipment = await db.Apparatuses.AsNoTracking()
            .Where(x => x.Id == equipmentId && x.ModuleCode == "equipment")
            .Select(x => new
            {
                x.Id, x.Name, x.ProductsId,
                AttachmentCount = x.Files.Count
            }).SingleOrDefaultAsync(cancellationToken);
        var job = await db.EquipmentDeletionJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EquipmentId == equipmentId, cancellationToken);
        if (equipment is null && job is null) return null;

        var reasons = equipment is null
            ? []
            : await BlockingReasonsAsync(equipmentId, cancellationToken);
        return new EquipmentDeletionPreviewDto
        {
            Exists = equipment is not null,
            EquipmentId = equipment?.Id ?? job!.EquipmentId,
            EquipmentName = equipment?.Name ?? job!.EquipmentName,
            ProductsId = equipment?.ProductsId ?? job!.ProductsId,
            AttachmentCount = equipment?.AttachmentCount ?? job!.AttachmentCount,
            HasReservationHistory = reasons.Contains(ReservationHistoryMessage),
            HasEnvironmentGroupMembership = reasons.Contains(EnvironmentGroupMessage),
            BlockingReasons = reasons,
            DeletionJobId = job?.Id,
            DeletionStatus = job?.Status,
            DeletionError = job?.LastErrorMessage
        };
    }

    public async Task<EquipmentDeletionRequestResult> RequestAsync(string equipmentId, string actor,
        CancellationToken cancellationToken = default)
    {
        actor = string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        await EquipmentDeletionWriteGuard.LockAsync(db, equipmentId, cancellationToken);
        var equipment = await db.Apparatuses.Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == equipmentId && x.ModuleCode == "equipment",
                cancellationToken) ?? throw new KeyNotFoundException("找不到設備。");
        var reasons = await BlockingReasonsAsync(equipmentId, cancellationToken);
        if (reasons.Count > 0) throw new EquipmentDeletionBlockedException(reasons);

        var job = await db.EquipmentDeletionJobs
            .SingleOrDefaultAsync(x => x.EquipmentId == equipmentId, cancellationToken);
        if (job?.Status == EquipmentDeletionStatuses.Completed)
            throw new InvalidOperationException("此設備 ID 已完成刪除且保留永久稽核，不可建立新的同 ID 設備或刪除任務。");
        if (job is null)
        {
            job = EquipmentDeletionJob.Create(equipment, equipment.Files.Count, actor, DateTime.UtcNow);
            db.EquipmentDeletionJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        var fileIds = equipment.Files.Select(x => x.Id).ToList();
        db.ChangeTracker.Clear();
        foreach (var fileId in fileIds)
            await fileDeletion.RequestAsync(fileId, actor, cancellationToken);
        await TryAdvanceAsync(job.Id, cancellationToken);
        var result = await db.EquipmentDeletionJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id, cancellationToken);
        return new(result.Id, result.Status,
            result.Status == EquipmentDeletionStatuses.Completed, result.AttachmentCount);
    }

    public async Task<int> ProcessPendingAsync(int maxCount = 20,
        CancellationToken cancellationToken = default)
    {
        var ids = await db.EquipmentDeletionJobs.AsNoTracking()
            .Where(x => x.Status != EquipmentDeletionStatuses.Completed)
            .OrderBy(x => x.UpdatedAt).Select(x => x.Id).Take(maxCount)
            .ToListAsync(cancellationToken);
        var completed = 0;
        foreach (var id in ids)
            if (await TryAdvanceAsync(id, cancellationToken)) completed++;
        return completed;
    }

    public async Task<bool> TryAdvanceByEquipmentIdAsync(string equipmentId,
        CancellationToken cancellationToken = default)
    {
        var id = await db.EquipmentDeletionJobs.AsNoTracking()
            .Where(x => x.EquipmentId == equipmentId).Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return id.HasValue && await TryAdvanceAsync(id.Value, cancellationToken);
    }

    public async Task<bool> TryAdvanceAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var snapshot = await db.EquipmentDeletionJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (snapshot is null) return false;
        if (snapshot.Status == EquipmentDeletionStatuses.Completed) return true;

        var equipmentExists = await db.Apparatuses.AsNoTracking()
            .AnyAsync(x => x.Id == snapshot.EquipmentId, cancellationToken);
        if (equipmentExists)
        {
            var reasons = await BlockingReasonsAsync(snapshot.EquipmentId, cancellationToken);
            if (reasons.Count > 0)
            {
                var blocked = await db.EquipmentDeletionJobs.SingleAsync(x => x.Id == jobId,
                    cancellationToken);
                blocked.MarkBlocked(string.Join(Environment.NewLine, reasons), DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }

            var fileIds = await db.ApparatusFiles.AsNoTracking()
                .Where(x => x.ApparatusId == snapshot.EquipmentId).Select(x => x.Id)
                .ToListAsync(cancellationToken);
            if (fileIds.Count > 0)
            {
                var deletionJobs = await db.EquipmentFileDeletionJobs.AsNoTracking()
                    .Where(x => fileIds.Contains(x.ApparatusFileId))
                    .Select(x => new { x.Id, x.ApparatusFileId, x.Status })
                    .ToListAsync(cancellationToken);
                var requested = deletionJobs.Select(x => x.ApparatusFileId).ToHashSet();
                foreach (var fileId in fileIds.Except(requested))
                    await fileDeletion.RequestAsync(fileId, snapshot.RequestedBy, cancellationToken);
                foreach (var retry in deletionJobs.Where(x => x.Status is
                    EquipmentFileDeletionStatuses.LocalCleanup or
                    EquipmentFileDeletionStatuses.KmVerified or
                    EquipmentFileDeletionStatuses.CleanupFailed))
                    await fileDeletion.TryCompleteLocalCleanupAsync(retry.Id, cancellationToken);
                if (await db.ApparatusFiles.AsNoTracking().AnyAsync(x =>
                    x.ApparatusId == snapshot.EquipmentId, cancellationToken))
                    return false;
            }
        }

        return await FinalizeAsync(jobId, cancellationToken);
    }

    private async Task<bool> FinalizeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var job = await db.EquipmentDeletionJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        if (job.Status == EquipmentDeletionStatuses.Completed) return true;
        job.BeginFinalizing(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        string? originalFolder = null;
        string? quarantineFolder = null;
        var moved = false;
        var dbCommitted = job.DbRecordDeletedAt is not null;
        try
        {
            if (!string.IsNullOrWhiteSpace(job.StorageFolderName))
            {
                if (await db.Apparatuses.AsNoTracking().AnyAsync(x =>
                    x.Id != job.EquipmentId && x.StorageFolderName == job.StorageFolderName,
                    cancellationToken))
                    throw new InvalidOperationException("設備正式目錄可能與其他設備共用，已停止清理。");
                originalFolder = await storage.EquipmentFolderAsync(job.StorageFolderName, cancellationToken);
                quarantineFolder = originalFolder + ".csit-delete-" + job.Id.ToString("N");
            }

            if (job.DbRecordDeletedAt is null)
            {
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
                await EquipmentDeletionWriteGuard.LockAsync(db, job.EquipmentId, cancellationToken);
                var reasons = await BlockingReasonsAsync(job.EquipmentId, cancellationToken);
                if (reasons.Count > 0)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    var blocked = await db.EquipmentDeletionJobs.SingleAsync(x => x.Id == jobId,
                        cancellationToken);
                    blocked.MarkBlocked(string.Join(Environment.NewLine, reasons), DateTime.UtcNow);
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }
                if (await db.ApparatusFiles.AsNoTracking().AnyAsync(x =>
                    x.ApparatusId == job.EquipmentId, cancellationToken))
                    throw new InvalidOperationException("設備附件尚未全部完成安全刪除。");

                if (originalFolder is not null && quarantineFolder is not null &&
                    Directory.Exists(originalFolder))
                {
                    if (Directory.Exists(quarantineFolder))
                        throw new InvalidOperationException("設備刪除暫存目錄衝突。");
                    Directory.Move(originalFolder, quarantineFolder);
                    moved = true;
                }

                var equipment = await db.Apparatuses.SingleOrDefaultAsync(x =>
                    x.Id == job.EquipmentId && x.ModuleCode == "equipment", cancellationToken);
                if (equipment is not null)
                {
                    var capabilities = await db.ApparatusResourceCapabilities
                        .Where(x => x.ApparatusId == job.EquipmentId).ToListAsync(cancellationToken);
                    db.ApparatusResourceCapabilities.RemoveRange(capabilities);
                    db.Apparatuses.Remove(equipment);
                }
                db.EquipmentDeletionCleanupJobId = job.Id;
                job.MarkDbRecordDeleted(DateTime.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                db.EquipmentDeletionCleanupJobId = null;
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                dbCommitted = true;
            }

            if (quarantineFolder is not null && Directory.Exists(quarantineFolder))
                Directory.Delete(quarantineFolder, true);
            job.MarkCompleted(DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            DbUpdateException or InvalidOperationException)
        {
            db.EquipmentDeletionCleanupJobId = null;
            if (moved && originalFolder is not null && quarantineFolder is not null &&
                Directory.Exists(quarantineFolder) && !Directory.Exists(originalFolder) &&
                !dbCommitted)
                Directory.Move(quarantineFolder, originalFolder);
            db.ChangeTracker.Clear();
            var failed = await db.EquipmentDeletionJobs.SingleAsync(x => x.Id == jobId,
                CancellationToken.None);
            if (failed.Status == EquipmentDeletionStatuses.Finalizing)
            {
                failed.MarkCleanupFailed(ex is UnauthorizedAccessException ? "FILE_ACCESS_DENIED" :
                    ex is IOException ? "FILE_IO_FAILED" : "FINAL_CLEANUP_FAILED",
                    ex.Message, DateTime.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            return false;
        }
    }

    private async Task<List<string>> BlockingReasonsAsync(string equipmentId,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (await db.ReservationItems.AsNoTracking()
            .AnyAsync(x => x.ApparatusId == equipmentId, cancellationToken))
            result.Add(ReservationHistoryMessage);
        if (await db.EquipmentGroupDevices.AsNoTracking()
            .AnyAsync(x => x.ApparatusId == equipmentId, cancellationToken))
            result.Add(EnvironmentGroupMessage);
        return result;
    }
}

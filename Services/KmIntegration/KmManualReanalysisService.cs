using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmReanalysisRejectedException(string reason) : Exception(reason);

public sealed class KmManualReanalysisService(
    IDbContextFactory<AppDbContext> dbFactory, IOptions<KmIntegrationOptions> configured)
{
    public static string? FailureReason(KmIntegrationOutbox row)
    {
        if (row.SourceEntityType is not (KmIntegrationSourceEntityTypes.VendorFile or
            KmIntegrationSourceEntityTypes.ApparatusFile)) return "此來源尚未完成安全整合。";
        if (row.Status != KmIntegrationStatus.Failed || row.KmJobId is null ||
            row.NextRetryAt is not null) return "只有 KM 已接收並回報失敗的工作可重新分析。";
        if (row.LastErrorRetryable == false) return "KM 已判定此錯誤不可重試。";
        if (row.LastErrorCode?.StartsWith("MODEL_", StringComparison.OrdinalIgnoreCase) == true ||
            row.LastErrorCode?.StartsWith("EMBEDDING_", StringComparison.OrdinalIgnoreCase) == true ||
            row.LastErrorCode?.StartsWith("OCR_", StringComparison.OrdinalIgnoreCase) == true ||
            row.LastErrorCode is "UNSUPPORTED_FORMAT" or "UNSUPPORTED_FILE_TYPE" or
            "MODEL_UNAVAILABLE" or "MODEL_NOT_CONFIGURED" or "OCR_DISABLED" or
            "UNSUPPORTED_IMAGE") return "文件格式或必要模型服務尚不支援重新分析。";
        // Rows failed before this column existed have no retryable flag. Only the
        // deployed KM v2 worker's documented transient codes are safe to infer.
        if (row.LastErrorRetryable is null &&
            row.LastErrorCode is not ("STORAGE_UNAVAILABLE" or "PROCESSING_FAILED"))
            return "缺少可重試的 KM 錯誤證據。";
        return null;
    }

    public async Task<Guid> CreateAsync(Guid failedOutboxId, Guid requestId, string actor,
        CancellationToken cancellationToken)
    {
        actor = actor?.Trim() ?? string.Empty;
        if (failedOutboxId == Guid.Empty || requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 100 || actor.Any(char.IsControl))
            throw new KmReanalysisRejectedException("重新分析請求無效。");
        var options = configured.Value;
        if (!options.Enabled) throw new KmReanalysisRejectedException("KM 功能目前停用。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

        // Lock the source before the outbox. Deletion takes the same source lock first.
        var original = await db.KmIntegrationOutbox.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == failedOutboxId, cancellationToken)
            ?? throw new KmReanalysisRejectedException("找不到原始工作。");
        if (original.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile)
        {
            if (!options.VendorEnabled) throw new KmReanalysisRejectedException("廠商來源目前停用。");
            var file = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.VendorFiles.FromSqlInterpolated($"SELECT * FROM vendor_files WHERE id = {original.SourceEntityId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.VendorFiles.SingleOrDefaultAsync(x => x.Id == original.SourceEntityId, cancellationToken);
            if (file is null) throw new KmReanalysisRejectedException("正式來源檔案不存在。");
        }
        else if (original.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile)
        {
            if (!options.EquipmentEnabled) throw new KmReanalysisRejectedException("設備來源目前停用。");
            var file = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? await db.ApparatusFiles.FromSqlInterpolated($"SELECT * FROM apparatus_files WHERE \"Id\" = {original.SourceEntityId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.ApparatusFiles.SingleOrDefaultAsync(x => x.Id == original.SourceEntityId, cancellationToken);
            if (file is null) throw new KmReanalysisRejectedException("正式來源檔案不存在。");
        }
        else throw new KmReanalysisRejectedException("此來源尚未完成安全整合。");

        // A repeat request returns the already created attempt; it never creates a second job.
        var existing = await db.KmIntegrationManualRetryAudits.AsNoTracking()
            .SingleOrDefaultAsync(x => x.NewOutboxId == requestId, cancellationToken);
        if (existing is not null)
        {
            if (existing.OutboxId != failedOutboxId || existing.RequestedBy != actor)
                throw new KmReanalysisRejectedException("請求識別碼已被使用。");
            return requestId;
        }
        var old = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? await db.KmIntegrationOutbox.FromSqlInterpolated($"SELECT * FROM km_integration_outbox WHERE id = {failedOutboxId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.Id == failedOutboxId, cancellationToken);
        if (old is null) throw new KmReanalysisRejectedException("找不到原始工作。");
        if (FailureReason(old) is { } reason)
            throw new KmReanalysisRejectedException(reason);
        var latestId = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => x.SourceSystem == old.SourceSystem &&
                x.DocumentId == old.DocumentId && x.Version == old.Version)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => x.Id).FirstAsync(cancellationToken);
        if (latestId != old.Id)
            throw new KmReanalysisRejectedException("請從此文件最新的失敗工作重新分析。");
        if (!await db.KmAnalysisCallbackReceipts.AsNoTracking().AnyAsync(x =>
                x.OutboxId == old.Id && x.Endpoint == "document-jobs/status" && x.Status == "Failed",
                cancellationToken))
            throw new KmReanalysisRejectedException("原 KM 工作尚未確認失敗。");
        if (await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.Id != old.Id && x.SourceSystem == old.SourceSystem &&
                x.DocumentId == old.DocumentId && x.Version == old.Version &&
                (x.Status != KmIntegrationStatus.Failed || x.NextRetryAt != null), cancellationToken) ||
            await db.KmDocumentAnalysisResults.AsNoTracking().AnyAsync(x =>
                x.DocumentId == old.DocumentId && x.Version == old.Version, cancellationToken))
            throw new KmReanalysisRejectedException("同版本已有有效或進行中的分析工作。");
        if (old.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile
            ? await db.VendorFileDeletionJobs.AnyAsync(x => x.VendorFileId == old.SourceEntityId, cancellationToken)
            : await db.EquipmentFileDeletionJobs.AnyAsync(x => x.ApparatusFileId == old.SourceEntityId, cancellationToken))
            throw new KmReanalysisRejectedException("來源已進入刪除流程。");

        var now = DateTime.UtcNow;
        var attempt = KmIntegrationOutbox.ForManualReanalysis(old, requestId, now);
        var count = await db.KmIntegrationManualRetryAudits.CountAsync(x =>
            x.DocumentId == old.DocumentId && x.Version == old.Version &&
            x.Action == "ManualReanalysis", cancellationToken);
        db.KmIntegrationOutbox.Add(attempt);
        db.KmIntegrationManualRetryAudits.Add(
            KmIntegrationManualRetryAudit.CreateReanalysis(old, attempt, actor, count + 1, now));
        await db.SaveChangesAsync(cancellationToken);
        if (!await KmSyncWorker.ValidateManualReanalysisSourceAsync(db, attempt, options, cancellationToken))
            throw new KmReanalysisRejectedException("正式檔案、SHA256 或核准證據驗證失敗。");
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return attempt.Id;
    }
}

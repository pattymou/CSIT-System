using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmQueueQueryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<KmIntegrationOptions> integrationOptions,
    IOptions<KmDeletionOptions> deletionOptions)
{
    public const int PageSize = 50;

    public async Task<KmQueuePageDto> GetIngestionAsync(
        string filter, int page, CancellationToken cancellationToken = default)
    {
        filter = NormalizeFilter(filter);
        page = Math.Max(1, page);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var all = db.KmIntegrationOutbox.AsNoTracking();
        var stats = new KmQueueStatsDto(
            await all.CountAsync(x => x.Status != KmIntegrationStatus.Completed, cancellationToken),
            await all.CountAsync(x => x.Status == KmIntegrationStatus.Pending, cancellationToken),
            await all.CountAsync(x => x.Status == KmIntegrationStatus.Dispatching ||
                x.Status == KmIntegrationStatus.Sent ||
                x.Status == KmIntegrationStatus.Processing, cancellationToken),
            await all.CountAsync(x => x.Status == KmIntegrationStatus.Failed, cancellationToken),
            await all.CountAsync(x => x.Status == KmIntegrationStatus.Completed, cancellationToken));

        var filtered = ApplyIngestionFilter(all, filter);
        var total = await filtered.CountAsync(cancellationToken);
        filtered = filter == KmQueueFilters.Completed
            ? filtered.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
            : filtered.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);
        var rows = await filtered.Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync(cancellationToken);
        var sources = await LoadSourcesAsync(db,
            rows.Select(x => (x.SourceEntityType, x.SourceEntityId)), cancellationToken);
        var now = DateTime.UtcNow;
        var offset = (long)(page - 1) * PageSize;
        var items = rows.Select((row, index) =>
        {
            sources.TryGetValue((row.SourceEntityType, row.SourceEntityId), out var source);
            return new KmQueueItemDto(
                offset + index + 1, row.Id, source?.FileName ?? row.FileName,
                SourceModule(row.SourceEntityType), source?.Uploader ?? "未記錄",
                source?.UploadedAt, row.CreatedAt, row.Status.ToString(),
                IngestionStatusDisplay(row.Status), row.RetryCount, row.NextRetryAt,
                IngestionRetryState(row, now), row.LastErrorCode, Safe(row.LastErrorMessage));
        }).ToList();

        return new(items, page, PageSize, total, stats, integrationOptions.Value.Enabled,
            integrationOptions.Value.Enabled
                ? null
                : "KM 功能停用／等待派送；佇列仍維持唯讀可見。");
    }

    public async Task<KmQueuePageDto> GetDeletionAsync(
        string filter, int page, CancellationToken cancellationToken = default)
    {
        filter = NormalizeFilter(filter);
        page = Math.Max(1, page);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var verification = await LoadVerificationRowsAsync(db, filter, page, cancellationToken);
        var documents = await LoadDocumentRowsAsync(db, filter, page, cancellationToken);
        var equipment = await LoadEquipmentRowsAsync(db, filter, page, cancellationToken);
        var counts = verification.Counts + documents.Counts + equipment.Counts;
        var combined = verification.Rows.Concat(documents.Rows).Concat(equipment.Rows);
        combined = filter == KmQueueFilters.Completed
            ? combined.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
            : combined.OrderBy(x => x.Priority).ThenBy(x => x.QueuedAt).ThenBy(x => x.Id);
        var selected = combined.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        var sources = await LoadSourcesAsync(db,
            selected.Where(x => x.SourceId.HasValue)
                .Select(x => (x.SourceType, x.SourceId!.Value)), cancellationToken);
        var offset = (long)(page - 1) * PageSize;
        var items = selected.Select((row, index) =>
        {
            sources.TryGetValue((row.SourceType, row.SourceId ?? Guid.Empty), out var source);
            var originalDeleted = row.SourceId.HasValue && source is null;
            return new KmQueueItemDto(
                offset + index + 1, row.Id, source?.FileName ?? row.FileName,
                row.SourceModule,
                source?.Uploader ?? (originalDeleted ? "原始紀錄已刪除" : "未記錄"),
                source?.UploadedAt, row.QueuedAt, row.Status,
                DeletionStatusDisplay(row.Status), row.Attempts, null,
                DeletionRetryState(row), row.ErrorCode, Safe(row.ErrorMessage),
                row.Category, row.Priority, row.WaitsForKm);
        }).ToList();

        return new(items, page, PageSize, counts.For(filter), counts.ToDto(),
            deletionOptions.Value.Enabled,
            deletionOptions.Value.Enabled
                ? null
                : "KM 刪除功能目前停用；本地清理與歷史狀態仍維持唯讀可見。");
    }

    private static IQueryable<KmIntegrationOutbox> ApplyIngestionFilter(
        IQueryable<KmIntegrationOutbox> query, string filter) => filter switch
    {
        KmQueueFilters.Waiting => query.Where(x => x.Status == KmIntegrationStatus.Pending),
        KmQueueFilters.Processing => query.Where(x =>
            x.Status == KmIntegrationStatus.Dispatching ||
            x.Status == KmIntegrationStatus.Sent ||
            x.Status == KmIntegrationStatus.Processing),
        KmQueueFilters.Failed => query.Where(x => x.Status == KmIntegrationStatus.Failed),
        KmQueueFilters.Completed => query.Where(x => x.Status == KmIntegrationStatus.Completed),
        _ => query.Where(x => x.Status != KmIntegrationStatus.Completed)
    };

    private async Task<DeletionResult> LoadVerificationRowsAsync(
        AppDbContext db, string filter, int page, CancellationToken cancellationToken)
    {
        var all = from target in db.VerificationCaseDeletionTargets.AsNoTracking()
                  join job in db.VerificationCaseDeletionJobs.AsNoTracking()
                      on target.JobId equals job.Id
                  select new { Target = target, Job = job };
        var counts = new QueueCounts(
            await all.CountAsync(x => x.Job.Status != "Completed", cancellationToken),
            await all.CountAsync(x => x.Job.Status != "Completed" &&
                x.Target.Status == "Pending", cancellationToken),
            await all.CountAsync(x => x.Job.Status != "Completed" &&
                x.Job.LastCleanupErrorCode == null && x.Target.Status != "Pending" &&
                x.Target.Status != "Failed" && x.Target.Status != "DeliveryFailed",
                cancellationToken),
            await all.CountAsync(x => x.Job.Status != "Completed" &&
                (x.Job.LastCleanupErrorCode != null || x.Target.Status == "Failed" ||
                 x.Target.Status == "DeliveryFailed"), cancellationToken),
            await all.CountAsync(x => x.Job.Status == "Completed", cancellationToken));

        var filtered = filter switch
        {
            KmQueueFilters.Waiting => all.Where(x =>
                x.Job.Status != "Completed" && x.Target.Status == "Pending"),
            KmQueueFilters.Processing => all.Where(x =>
                x.Job.Status != "Completed" && x.Job.LastCleanupErrorCode == null &&
                x.Target.Status != "Pending" && x.Target.Status != "Failed" &&
                x.Target.Status != "DeliveryFailed"),
            KmQueueFilters.Failed => all.Where(x =>
                x.Job.Status != "Completed" &&
                (x.Job.LastCleanupErrorCode != null || x.Target.Status == "Failed" ||
                 x.Target.Status == "DeliveryFailed")),
            KmQueueFilters.Completed => all.Where(x => x.Job.Status == "Completed"),
            _ => all.Where(x => x.Job.Status != "Completed")
        };
        var ordered = filter == KmQueueFilters.Completed
            ? filtered.OrderByDescending(x => x.Job.CompletedAt).ThenByDescending(x => x.Target.Id)
            : filtered.OrderBy(x => x.Target.RequestedAt).ThenBy(x => x.Target.Id);
        var rows = await ordered.Take(CheckedTake(page)).ToListAsync(cancellationToken);
        return new(rows.Select(x =>
        {
            var sourceType = x.Target.SourceType == "VerificationApplicationAttachment"
                ? KmIntegrationSourceEntityTypes.VerificationApplicationFile
                : x.Target.SourceType;
            return new DeletionRow(
                x.Target.Id, "原始檔案", "驗證案件", sourceType,
                x.Target.SourceEntityId ?? x.Target.DocumentFileId, x.Target.RequestedAt,
                VerificationEffectiveStatus(x.Target, x.Job), x.Target.AttemptCount,
                x.Target.LastErrorCode ?? x.Job.LastCleanupErrorCode, null,
                x.Target.LastErrorRetryable, x.Job.CompletedAt, 1,
                x.Target.Status != "NotDispatched");
        }).ToList(), counts);
    }

    private async Task<DeletionResult> LoadDocumentRowsAsync(
        AppDbContext db, string filter, int page, CancellationToken cancellationToken)
    {
        var all = db.DocumentLibraryFileDeletionJobs.AsNoTracking();
        var counts = new QueueCounts(
            await all.CountAsync(x => x.Status != DocumentLibraryFileDeletionStatuses.Completed, cancellationToken),
            await all.CountAsync(x => x.Status == DocumentLibraryFileDeletionStatuses.Preparing || x.Status == DocumentLibraryFileDeletionStatuses.Pending, cancellationToken),
            await all.CountAsync(x => x.Status == DocumentLibraryFileDeletionStatuses.Dispatching || x.Status == DocumentLibraryFileDeletionStatuses.Accepted || x.Status == DocumentLibraryFileDeletionStatuses.Processing || x.Status == DocumentLibraryFileDeletionStatuses.KmVerified || x.Status == DocumentLibraryFileDeletionStatuses.LocalCleanup, cancellationToken),
            await all.CountAsync(x => x.Status == DocumentLibraryFileDeletionStatuses.Failed || x.Status == DocumentLibraryFileDeletionStatuses.CleanupFailed, cancellationToken),
            await all.CountAsync(x => x.Status == DocumentLibraryFileDeletionStatuses.Completed, cancellationToken));
        var filtered = filter switch
        {
            KmQueueFilters.Waiting => all.Where(x => x.Status == DocumentLibraryFileDeletionStatuses.Preparing || x.Status == DocumentLibraryFileDeletionStatuses.Pending),
            KmQueueFilters.Processing => all.Where(x => x.Status == DocumentLibraryFileDeletionStatuses.Dispatching || x.Status == DocumentLibraryFileDeletionStatuses.Accepted || x.Status == DocumentLibraryFileDeletionStatuses.Processing || x.Status == DocumentLibraryFileDeletionStatuses.KmVerified || x.Status == DocumentLibraryFileDeletionStatuses.LocalCleanup),
            KmQueueFilters.Failed => all.Where(x => x.Status == DocumentLibraryFileDeletionStatuses.Failed || x.Status == DocumentLibraryFileDeletionStatuses.CleanupFailed),
            KmQueueFilters.Completed => all.Where(x => x.Status == DocumentLibraryFileDeletionStatuses.Completed),
            _ => all.Where(x => x.Status != DocumentLibraryFileDeletionStatuses.Completed)
        };
        var ordered = filter == KmQueueFilters.Completed
            ? filtered.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
            : filtered.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id);
        var rows = await ordered.Take(CheckedTake(page)).ToListAsync(cancellationToken);
        return new(rows.Select(x => new DeletionRow(
            x.Id, x.FileName, "文件專區", KmIntegrationSourceEntityTypes.DocumentLibraryFile,
            x.DocumentFileId, x.RequestedAt, x.Status, x.AttemptCount, x.LastErrorCode,
            x.LastErrorMessage, x.LastErrorRetryable, x.CompletedAt, 2,
            x.KmDeletionJobId is not null ||
            x.Status is not (DocumentLibraryFileDeletionStatuses.LocalCleanup or
                DocumentLibraryFileDeletionStatuses.CleanupFailed or
                DocumentLibraryFileDeletionStatuses.Completed))).ToList(), counts);
    }

    private async Task<DeletionResult> LoadEquipmentRowsAsync(
        AppDbContext db, string filter, int page, CancellationToken cancellationToken)
    {
        var all = db.EquipmentFileDeletionJobs.AsNoTracking();
        var counts = new QueueCounts(
            await all.CountAsync(x => x.Status != EquipmentFileDeletionStatuses.Completed, cancellationToken),
            await all.CountAsync(x => x.Status == EquipmentFileDeletionStatuses.Pending, cancellationToken),
            await all.CountAsync(x => x.Status == EquipmentFileDeletionStatuses.Dispatching || x.Status == EquipmentFileDeletionStatuses.Accepted || x.Status == EquipmentFileDeletionStatuses.Processing || x.Status == EquipmentFileDeletionStatuses.KmVerified || x.Status == EquipmentFileDeletionStatuses.LocalCleanup, cancellationToken),
            await all.CountAsync(x => x.Status == EquipmentFileDeletionStatuses.Failed || x.Status == EquipmentFileDeletionStatuses.CleanupFailed, cancellationToken),
            await all.CountAsync(x => x.Status == EquipmentFileDeletionStatuses.Completed, cancellationToken));
        var filtered = filter switch
        {
            KmQueueFilters.Waiting => all.Where(x => x.Status == EquipmentFileDeletionStatuses.Pending),
            KmQueueFilters.Processing => all.Where(x => x.Status == EquipmentFileDeletionStatuses.Dispatching || x.Status == EquipmentFileDeletionStatuses.Accepted || x.Status == EquipmentFileDeletionStatuses.Processing || x.Status == EquipmentFileDeletionStatuses.KmVerified || x.Status == EquipmentFileDeletionStatuses.LocalCleanup),
            KmQueueFilters.Failed => all.Where(x => x.Status == EquipmentFileDeletionStatuses.Failed || x.Status == EquipmentFileDeletionStatuses.CleanupFailed),
            KmQueueFilters.Completed => all.Where(x => x.Status == EquipmentFileDeletionStatuses.Completed),
            _ => all.Where(x => x.Status != EquipmentFileDeletionStatuses.Completed)
        };
        var ordered = filter == KmQueueFilters.Completed
            ? filtered.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id)
            : filtered.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id);
        var rows = await ordered.Take(CheckedTake(page)).ToListAsync(cancellationToken);
        return new(rows.Select(x => new DeletionRow(
            x.Id, x.FileName, "設備附件", KmIntegrationSourceEntityTypes.ApparatusFile,
            x.ApparatusFileId, x.RequestedAt, x.Status, x.AttemptCount, x.LastErrorCode,
            x.LastErrorMessage, x.LastErrorRetryable, x.CompletedAt, 3,
            x.KmDeletionJobId is not null ||
            x.Status is not (EquipmentFileDeletionStatuses.LocalCleanup or
                EquipmentFileDeletionStatuses.CleanupFailed or
                EquipmentFileDeletionStatuses.Completed))).ToList(), counts);
    }

    private static int CheckedTake(int page) =>
        (int)Math.Min((long)Math.Max(1, page) * PageSize, int.MaxValue);

    private static string VerificationEffectiveStatus(
        VerificationCaseDeletionTarget target, VerificationCaseDeletionJob job)
    {
        if (job.Status == "Completed") return "Completed";
        if (job.LastCleanupErrorCode is not null) return "CleanupFailed";
        if (job.Status == "LocalCleanup") return "LocalCleanup";
        if (job.Status == "KmVerified") return "KmVerified";
        if (job.Status == "LocalReady") return "LocalCleanup";
        return target.Status == "Completed" ? "KmVerified" : target.Status;
    }

    private async Task<Dictionary<(string Type, Guid Id), SourceInfo>> LoadSourcesAsync(
        AppDbContext db, IEnumerable<(string Type, Guid Id)> keys,
        CancellationToken cancellationToken)
    {
        var requested = keys.ToHashSet();
        var ids = requested.Select(x => x.Id).Distinct().ToArray();
        var result = new Dictionary<(string Type, Guid Id), SourceInfo>();
        if (ids.Length == 0) return result;
        void Add(string type, Guid id, string fileName, string? uploader, DateTime? uploadedAt)
        {
            if (requested.Contains((type, id)))
                result[(type, id)] = new(fileName, uploader, uploadedAt);
        }
        foreach (var x in await db.DocumentLibraryFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken))
            Add(KmIntegrationSourceEntityTypes.DocumentLibraryFile, x.Id, x.OriginalFileName, x.UploadedBy, x.UploadedAt);
        foreach (var x in await db.VendorFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken))
            Add(KmIntegrationSourceEntityTypes.VendorFile, x.Id, x.OriginalFileName, x.UploadedBy, x.UploadedAt);
        foreach (var x in await db.ApparatusFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken))
            Add(KmIntegrationSourceEntityTypes.ApparatusFile, x.Id, x.OriginalFileName ?? x.FileName, x.UploadEmp, x.CreatedAt);
        foreach (var x in await db.VerificationApplicationFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken))
            Add(KmIntegrationSourceEntityTypes.VerificationApplicationFile, x.Id, x.FileName, x.UploadedBy, x.CreatedAt);
        foreach (var x in await db.ModuleCaseFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken))
            Add(KmIntegrationSourceEntityTypes.TestReport, x.Id, x.FileName, x.UploadEmp, x.CreatedAt);
        return result;
    }

    private string IngestionRetryState(KmIntegrationOutbox item, DateTime now)
    {
        if (item.Status == KmIntegrationStatus.Completed) return string.Empty;
        if (!integrationOptions.Value.Enabled) return "KM 功能停用／等待派送";
        if (!IsIngestionSourceEnabled(item.SourceEntityType)) return "來源派送功能停用／等待派送";
        if (item.KmJobId is not null || item.SentAt is not null)
            return item.Status == KmIntegrationStatus.Failed
                ? "KM 已接收；依回報錯誤處理"
                : "KM 已接收／等待回報";
        if (item.Status != KmIntegrationStatus.Failed) return string.Empty;
        if (item.RetryCount >= integrationOptions.Value.MaxRetryCount) return "已達重試上限";
        if (item.NextRetryAt > now) return "等待自動重試";
        if (item.NextRetryAt.HasValue) return "可以自動重試";
        return "不可自動重試／需要人工處理";
    }

    private bool IsIngestionSourceEnabled(string sourceType) =>
        integrationOptions.Value.Enabled && sourceType switch
        {
            KmIntegrationSourceEntityTypes.VendorFile => integrationOptions.Value.VendorEnabled,
            KmIntegrationSourceEntityTypes.ApparatusFile => integrationOptions.Value.EquipmentEnabled,
            _ => true
        };

    private string DeletionRetryState(DeletionRow item)
    {
        if (item.Status == "Completed") return string.Empty;
        if (item.Status == "CleanupFailed") return "本地清理失敗";
        if (!item.WaitsForKm) return "本地清理";
        if (item.Status is "Failed" or "DeliveryFailed" && item.Retryable != true)
            return "不可自動重試／需要人工處理";
        if (!deletionOptions.Value.Enabled || item.Priority == 3 && !deletionOptions.Value.EquipmentEnabled)
            return "功能停用";
        if (item.Status is not ("Failed" or "DeliveryFailed")) return string.Empty;
        if (item.Attempts >= integrationOptions.Value.MaxRetryCount) return "已達重試上限";
        return "可以自動重試";
    }

    private static string NormalizeFilter(string? filter) =>
        KmQueueFilters.IsValid(filter) ? filter! : KmQueueFilters.Unfinished;

    private static string SourceModule(string type) => type switch
    {
        KmIntegrationSourceEntityTypes.TestReport => "測試報告",
        KmIntegrationSourceEntityTypes.DocumentLibraryFile => "文件專區",
        KmIntegrationSourceEntityTypes.VerificationApplicationFile => "驗證申請附件",
        KmIntegrationSourceEntityTypes.VendorFile => "廠商附件",
        KmIntegrationSourceEntityTypes.ApparatusFile => "設備附件",
        _ => "未知來源"
    };

    private static string IngestionStatusDisplay(KmIntegrationStatus status) => status switch
    {
        KmIntegrationStatus.Pending => "等待派送",
        KmIntegrationStatus.Dispatching => "正在送出 KM 要求",
        KmIntegrationStatus.Sent => "KM 已接收",
        KmIntegrationStatus.Processing => "KM 處理中",
        KmIntegrationStatus.Completed => "KM 處理完成",
        _ => "失敗"
    };

    private static string DeletionStatusDisplay(string status) => status switch
    {
        "Prepared" or "Preparing" => "刪除準備中",
        "Pending" or "WaitingForKm" => "等待 KM 刪除",
        "Dispatching" => "正在派送",
        "Accepted" => "KM 已接收",
        "Processing" => "KM 刪除中",
        "Completed" => "完整刪除完成",
        "KmVerified" => "KM 已驗證清除",
        "LocalCleanup" => "本地清理中",
        "CleanupFailed" => "本地清理失敗",
        "NotDispatched" => "不需 KM／本地清理",
        "Failed" or "DeliveryFailed" => "刪除失敗",
        _ => status
    };

    private static string? Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = KmIntegrationManualRetryAudit.SafeErrorMessage(value)!;
        return safe.Length <= 500 ? safe : safe[..500];
    }

    private sealed record SourceInfo(string FileName, string? Uploader, DateTime? UploadedAt);
    private sealed record DeletionRow(Guid Id, string FileName, string SourceModule,
        string SourceType, Guid? SourceId, DateTime QueuedAt, string Status, int Attempts,
        string? ErrorCode, string? ErrorMessage, bool? Retryable, DateTime? CompletedAt,
        int Priority, bool WaitsForKm)
    {
        public string Category => Priority switch
        {
            1 => "驗證案件／優先級 1",
            2 => "文件專區／優先級 2",
            _ => "設備附件／優先級 3"
        };
    }

    private sealed record DeletionResult(List<DeletionRow> Rows, QueueCounts Counts);
    private readonly record struct QueueCounts(
        int Unfinished, int Waiting, int Processing, int Failed, int Completed)
    {
        public static QueueCounts operator +(QueueCounts left, QueueCounts right) => new(
            left.Unfinished + right.Unfinished, left.Waiting + right.Waiting,
            left.Processing + right.Processing, left.Failed + right.Failed,
            left.Completed + right.Completed);
        public int For(string filter) => filter switch
        {
            KmQueueFilters.Waiting => Waiting,
            KmQueueFilters.Processing => Processing,
            KmQueueFilters.Failed => Failed,
            KmQueueFilters.Completed => Completed,
            _ => Unfinished
        };
        public KmQueueStatsDto ToDto() =>
            new(Unfinished, Waiting, Processing, Failed, Completed);
    }
}

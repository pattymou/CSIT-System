using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmManualRetryConflictException(string message) : Exception(message);
public sealed class KmManualRetryDocumentException(string message) : Exception(message);

public sealed class KmManualRetryService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly Regex AuthorizationHeader = new(
        @"\bAuthorization\s*:\s*Bearer\s+\S{3,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BearerValue = new(
        @"\bBearer\s+[A-Za-z0-9._~+/-]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        @"(?<![A-Za-z0-9_])(?:client_secret|access_token|password|secret|credential)[""']?\s*[:=]\s*[""']?[^\s""']{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CompactJwt = new(
        @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task RetryKmSyncAsync(Guid fileId, string requestedBy, string reason,
        CancellationToken cancellationToken = default)
    {
        if (reason is not null && reason.Any(ch => char.IsControl(ch) ||
            char.GetUnicodeCategory(ch) is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new ArgumentException("重試原因不可包含控制字元或不安全格式。", nameof(reason));
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length is < 5 or > 500)
            throw new ArgumentException("重試原因須為 5 至 500 字。", nameof(reason));
        if (AuthorizationHeader.IsMatch(trimmedReason) || BearerValue.IsMatch(trimmedReason) ||
            SecretAssignment.IsMatch(trimmedReason) || CompactJwt.IsMatch(trimmedReason))
            throw new ArgumentException("重試原因不可包含實際密碼、Token 或其他憑證值。", nameof(reason));
        if (string.IsNullOrWhiteSpace(requestedBy) || requestedBy.Length > 100)
            throw new ArgumentException("無法識別執行者。", nameof(requestedBy));
        if (fileId == Guid.Empty)
            throw new KmManualRetryDocumentException("找不到正式測試報告。");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        // PostgreSQL row lock serializes competing admins and excludes a worker claim.
        var row = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? await db.KmIntegrationOutbox.FromSqlInterpolated(
                $"SELECT * FROM km_integration_outbox WHERE source_entity_id = {fileId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmIntegrationOutbox.SingleOrDefaultAsync(x => x.SourceEntityId == fileId,
                cancellationToken);
        if (row is null)
            throw new KmManualRetryDocumentException("找不到該正式測試報告的 KM 事件。");
        if (row.ProjectId is { } recordId)
            await VerificationCaseDeletionHold.DemandWritableAsync(db, recordId, cancellationToken);
        var now = DateTime.UtcNow;
        if (!row.CanManualRetry(now))
            throw new KmManualRetryConflictException("KM 狀態已變更，或此事件不符合人工重試條件。");

        var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == fileId && x.DocumentId == row.DocumentId && x.VersionNo == row.Version &&
            x.RecordId == row.ProjectId && x.CaseId == row.CaseId && x.TaskId == row.TaskId &&
            x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported &&
            x.FileSize > 0, cancellationToken);
        if (file is null || await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.Id == fileId && x.Status != ApprovalStatus.Committed, cancellationToken))
            throw new KmManualRetryDocumentException("正式測試報告已不存在或不可供 KM 下載。");
        if (string.IsNullOrWhiteSpace(file.FilePath) || string.IsNullOrWhiteSpace(file.FileName) ||
            !File.Exists(Path.Combine(file.FilePath, file.FileName)))
            throw new KmManualRetryDocumentException("正式測試報告檔案不存在。");

        db.KmIntegrationManualRetryAudits.Add(
            KmIntegrationManualRetryAudit.Create(row, requestedBy, trimmedReason, now));
        row.RequeueManually(now);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}

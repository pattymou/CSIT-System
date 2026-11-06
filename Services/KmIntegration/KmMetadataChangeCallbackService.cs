using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmMetadataChangeCallbackService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<KmIntegrationOptions> configured)
{
    public async Task ApplyAsync(KmMetadataChangeStatusCallback callback,
        CancellationToken cancellationToken = default)
    {
        Validate(callback);
        var hash = KmMetadataChangeQueueService.Sha(callback);
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var receipt = await db.KmMetadataChangeCallbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (receipt is not null)
        {
            if (receipt.ChangeId == callback.ChangeId && receipt.PayloadSha256 == hash)
                return;
            throw new KmAnalysisCallbackException(409, "callback_conflict");
        }
        var row = IsPostgres(db)
            ? await db.KmMetadataChanges.FromSqlInterpolated(
                $"SELECT * FROM km_metadata_changes WHERE change_id = {callback.ChangeId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmMetadataChanges.SingleOrDefaultAsync(x => x.ChangeId == callback.ChangeId,
                cancellationToken);
        if (row is null) throw new KmAnalysisCallbackException(404, "change_not_found");
        if (row.KmChangeJobId != callback.KmChangeJobId)
            throw new KmAnalysisCallbackException(409, "correlation_mismatch");
        try
        {
            switch (callback.Status)
            {
                case "Processing": row.Processing(callback.AppliedRevision, now); break;
                case "Completed": row.Complete(callback.AppliedRevision, now); break;
                case "Failed":
                    var retryable = callback.Error!.Retryable == true &&
                        row.RetryCount < configured.Value.MaxRetryCount;
                    row.CallbackFailed(callback.AppliedRevision, callback.Error.Code,
                        callback.Error.Message, retryable,
                        retryable ? KmMetadataChangeWorker.NextRetry(configured.Value,
                            row.RetryCount, now) : null, now);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new KmAnalysisCallbackException(409,
                ex.Message.Contains("revision", StringComparison.OrdinalIgnoreCase)
                    ? "revision_conflict" : "status_conflict");
        }
        db.KmMetadataChangeCallbacks.Add(new KmMetadataChangeCallbackReceipt
        {
            CallbackId = callback.CallbackId, ChangeId = callback.ChangeId,
            Status = callback.Status, AppliedRevision = callback.AppliedRevision,
            PayloadSha256 = hash, ReceivedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static void Validate(KmMetadataChangeStatusCallback value)
    {
        var timestamps = value?.Timestamps;
        if (value is null || value.SchemaVersion != "2.0" || value.CallbackId == Guid.Empty ||
            value.ChangeId == Guid.Empty || value.SourceSystem != "CSIT" ||
            string.IsNullOrWhiteSpace(value.KmChangeJobId) || value.KmChangeJobId.Length > 150 ||
            value.AppliedRevision < 0 || timestamps is null || !(value.Status switch
            {
                "Processing" => Utc(timestamps.StartedAt) && value.Error is null,
                "Completed" => Utc(timestamps.CompletedAt) && value.Error is null &&
                    value.AppliedRevision > 0,
                "Failed" => Utc(timestamps.FailedAt) && value.Error is
                    { Code.Length: > 0 and <= 100, Message.Length: > 0 and <= 500,
                      Retryable: not null },
                _ => false
            }))
            throw new KmAnalysisCallbackException(400, "invalid_callback");
    }
    private static bool Utc(DateTimeOffset? value) =>
        value is { } timestamp && timestamp != default && timestamp.Offset == TimeSpan.Zero;
    private static bool IsPostgres(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}

using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record KmSyncSnapshot(
    int PendingCount, int FailedCount, int RetryCount,
    DateTime? LastAttemptAt, string? LastErrorCode);

public sealed class KmSyncDiagnostics(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<KmSyncSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.KmIntegrationOutbox.AsNoTracking();
        var last = await rows.Where(x => x.LastAttemptAt != null)
            .OrderByDescending(x => x.LastAttemptAt)
            .Select(x => new { x.LastAttemptAt, x.LastErrorCode })
            .FirstOrDefaultAsync(cancellationToken);
        return new KmSyncSnapshot(
            await rows.CountAsync(x => x.Status == KmIntegrationStatus.Pending, cancellationToken),
            await rows.CountAsync(x => x.Status == KmIntegrationStatus.Failed, cancellationToken),
            await rows.SumAsync(x => (int?)x.RetryCount, cancellationToken) ?? 0,
            last?.LastAttemptAt, last?.LastErrorCode);
    }
}

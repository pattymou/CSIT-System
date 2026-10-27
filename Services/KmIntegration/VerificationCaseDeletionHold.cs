using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class VerificationCaseDeletionHold
{
    public static Task<bool> IsActiveAsync(AppDbContext db, Guid recordId,
        CancellationToken cancellationToken = default) =>
        db.VerificationCaseDeletionJobs.AsNoTracking().AnyAsync(x => x.RecordId == recordId &&
            (x.Status == "WaitingForKm" || x.Status == "KmVerified" ||
             x.Status == "LocalReady" ||
             x.Status == "LocalCleanup" || x.Status == "Completed"), cancellationToken);

    public static async Task DemandWritableAsync(AppDbContext db, Guid recordId,
        CancellationToken cancellationToken = default)
    {
        if (await IsActiveAsync(db, recordId, cancellationToken))
            throw new InvalidOperationException("This verification case is frozen for deletion.");
    }
}

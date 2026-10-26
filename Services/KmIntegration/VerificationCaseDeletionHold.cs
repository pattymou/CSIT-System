using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class VerificationCaseDeletionHold
{
    public static Task<bool> IsActiveAsync(AppDbContext db, Guid recordId,
        CancellationToken cancellationToken = default) =>
        db.VerificationCaseDeletionJobs.AsNoTracking().AnyAsync(x => x.RecordId == recordId &&
            (x.Status == "WaitingForKm" || x.Status == "KmVerified"), cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class DocumentLibraryFileDeletionHold
{
    public static Task<bool> ExistsAsync(AppDbContext db, Guid documentFileId,
        CancellationToken cancellationToken = default) =>
        db.DocumentLibraryFileDeletionJobs.AsNoTracking().AnyAsync(
            x => x.DocumentFileId == documentFileId, cancellationToken);

    public static Task<bool> IsActiveAsync(AppDbContext db, Guid documentFileId,
        CancellationToken cancellationToken = default) =>
        db.DocumentLibraryFileDeletionJobs.AsNoTracking().AnyAsync(
            x => x.DocumentFileId == documentFileId &&
                 x.Status != DocumentLibraryFileDeletionStatuses.Completed,
            cancellationToken);
}

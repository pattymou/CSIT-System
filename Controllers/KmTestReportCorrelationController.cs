using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Controllers;

[ApiController]
[Route("api/integrations/km/v2/test-report-correlations")]
[Authorize(Policy = KmIntegrationAuthorization.DocumentReadPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class KmTestReportCorrelationController(
    IDbContextFactory<AppDbContext> dbFactory) : ControllerBase
{
    [HttpGet("{eventId:guid}")]
    public async Task<IActionResult> Get(
        Guid eventId,
        [FromQuery] Guid documentId,
        [FromQuery] int version,
        [FromQuery] string kmJobId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (eventId == Guid.Empty || documentId == Guid.Empty || version < 1 ||
            string.IsNullOrWhiteSpace(kmJobId)) return NotFound();

        var normalizedJobId = kmJobId.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var outbox = await db.KmIntegrationOutbox.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EventId == eventId && x.EventType == KmIntegrationEventTypes.TestReportCommitted &&
            x.SourceSystem == "CSIT" &&
            x.SourceEntityType == KmIntegrationSourceEntityTypes.TestReport &&
            x.DocumentId == documentId && x.Version == version &&
            x.KmJobId == normalizedJobId && x.Status == KmIntegrationStatus.Completed,
            cancellationToken);
        if (outbox is null) return NotFound();

        var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == outbox.SourceEntityId && x.DocumentId == documentId &&
            x.VersionNo == version && x.FileKind == ModuleCaseFileKinds.TestReport &&
            x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported &&
            x.Sha256 != null,
            cancellationToken);
        if (file is null || file.Sha256!.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
            await VerificationCaseDeletionHold.IsActiveAsync(db, file.RecordId, cancellationToken) ||
            await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.Id == file.Id && x.Status != ApprovalStatus.Committed, cancellationToken))
            return NotFound();

        return Ok(new KmV2TestReportCorrelationResponse(
            "2.0", "CSIT", KmAnalysisSourceTypes.TestReport, file.Id,
            documentId, version, outbox.EventId, outbox.Id, normalizedJobId,
            file.Sha256.ToLowerInvariant(), "Completed"));
    }
}

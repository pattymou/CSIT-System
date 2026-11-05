using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmAnalysisQueryService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<KmAnalysisResultPage> ListAsync(ClaimsPrincipal user, string? sourceType,
        Guid? sourceEntityId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sourceType) && !KmAnalysisSourceTypes.All.Contains(sourceType))
            throw new ArgumentException("Unknown sourceType.", nameof(sourceType));
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await ResolveAccessAsync(db, user, cancellationToken);
        var query = Visible(db, db.KmDocumentAnalysisResults.AsNoTracking(), access);
        if (!string.IsNullOrWhiteSpace(sourceType)) query = query.Where(x => x.SourceType == sourceType);
        if (sourceEntityId.HasValue) query = query.Where(x => x.SourceEntityId == sourceEntityId.Value);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new KmAnalysisResultPage(rows.Select(x => new KmAnalysisResultListItem(
            x.Id, x.AnalysisResultId, x.SourceType, x.SourceEntityId, x.DocumentId, x.Version,
            x.Status, Title(x.ResultJson), x.ReceivedAt, x.ReviewStatus)).ToList(),
            page, pageSize, total);
    }

    public async Task<KmAnalysisResultDetail?> GetAsync(ClaimsPrincipal user, Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await ResolveAccessAsync(db, user, cancellationToken);
        var row = await Visible(db, db.KmDocumentAnalysisResults.AsNoTracking(), access)
            .Include(x => x.ReviewRevisions).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : Detail(row);
    }

    public async Task<KmAnalysisResultDetail> ReviewAsync(ClaimsPrincipal user, Guid id,
        KmAnalysisReviewRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.Decision is not (KmAnalysisReviewStatuses.Verified or
                KmAnalysisReviewStatuses.Rejected) ||
            !KmAnalysisMetadataValidator.IsSafeCorrections(request.CorrectionsJson) ||
            request.Decision == KmAnalysisReviewStatuses.Rejected && string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Invalid review.", nameof(request));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var access = await ResolveAccessAsync(db, user, cancellationToken);
        var row = db.Database.IsRelational()
            ? await db.KmDocumentAnalysisResults.FromSqlInterpolated(
                $"SELECT * FROM km_document_analysis_results WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmDocumentAnalysisResults.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null || !await Visible(db, db.KmDocumentAnalysisResults.AsNoTracking(), access)
                .AnyAsync(x => x.Id == id, cancellationToken))
            throw new KeyNotFoundException("Analysis result was not found.");
        await db.Entry(row).Collection(x => x.ReviewRevisions).LoadAsync(cancellationToken);
        var account = Account(user);
        var revision = row.Review(request.Decision,
            request.CorrectionsJson is { ValueKind: not JsonValueKind.Null } corrections
                ? corrections.GetRawText() : null,
            request.Reason, account, DateTime.UtcNow);
        db.Entry(revision).State = EntityState.Added;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Detail(row);
    }

    private static IQueryable<KmDocumentAnalysisResult> Visible(AppDbContext db,
        IQueryable<KmDocumentAnalysisResult> query, Access access)
    {
        if (access.IsAdmin) return query;
        var account = access.Account;
        var teams = access.TeamIds;
        return query.Where(result =>
            result.SourceType == KmAnalysisSourceTypes.DocumentLibraryFile ||
            result.SourceType == KmAnalysisSourceTypes.VendorFile ||
            result.SourceType == KmAnalysisSourceTypes.VerificationApplicationAttachment &&
                db.VerificationApplicationFiles.Any(file => file.Id == result.SourceEntityId &&
                    (file.VerificationApplication.ApplicantAccount == account ||
                     file.VerificationApplication.AssignedLeaderAccount == account ||
                     file.VerificationApplication.TeamOptionId.HasValue &&
                        teams.Contains(file.VerificationApplication.TeamOptionId.Value))) ||
            result.SourceType == KmAnalysisSourceTypes.TestReport &&
                db.ModuleCaseFiles.Any(file => file.Id == result.SourceEntityId &&
                    db.VerificationApplications.Any(app => app.ModuleRecordId == file.RecordId &&
                        (app.ApplicantAccount == account || app.AssignedLeaderAccount == account ||
                         app.TeamOptionId.HasValue && teams.Contains(app.TeamOptionId.Value)))) ||
            result.SourceType == KmAnalysisSourceTypes.EquipmentAttachment &&
                db.ApparatusFiles.Any(file => file.Id == result.SourceEntityId &&
                    (file.Apparatus.CustodianAccount == account ||
                     file.Apparatus.OwnerTeamOptionId.HasValue && teams.Contains(file.Apparatus.OwnerTeamOptionId.Value))));
    }

    private static async Task<Access> ResolveAccessAsync(AppDbContext db, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (user.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");
        var account = Account(user);
        if (user.IsInRole("Admin")) return new(true, account, []);
        var teams = await db.TeamRoutings.AsNoTracking().Where(x =>
                x.IsEnabled && x.LeaderAccount.ToLower() == account)
            .Select(x => x.TeamOptionId).Distinct().ToListAsync(cancellationToken);
        return new(false, account, teams);
    }

    private static string Account(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(value)
            ? throw new UnauthorizedAccessException("Account claim is required.")
            : value.Trim().ToLowerInvariant();
    }

    private static string? Title(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("document", out var metadata) &&
               metadata.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() : null;
    }

    private static KmAnalysisResultDetail Detail(KmDocumentAnalysisResult row)
    {
        var result = JsonDocument.Parse(row.ResultJson).RootElement.Clone();
        var revisions = row.ReviewRevisions.OrderBy(x => x.Revision).Select(x =>
        {
            JsonElement? corrections = string.IsNullOrWhiteSpace(x.CorrectionsJson) ? null :
                JsonDocument.Parse(x.CorrectionsJson).RootElement.Clone();
            return new KmAnalysisReviewRevisionDto(x.Id, x.Revision, x.Decision, corrections,
                x.Reason, x.ReviewedBy, x.ReviewedAt);
        }).ToList();
        return new KmAnalysisResultDetail(row.Id, row.AnalysisResultId, row.CallbackId,
            row.SourceType, row.SourceEntityId, row.DocumentId, row.Version, row.EventId,
            row.CsitJobId, row.KmJobId, row.SourceSha256, row.MetadataSchemaVersion, result,
            row.AnalyzedAt, row.ReceivedAt, row.IndexedAt, row.SourceRevision,
            row.SupersedesResultId, row.SupersededAt, row.ReviewStatus,
            row.ReviewedBy, row.ReviewedAt, revisions);
    }

    private sealed record Access(bool IsAdmin, string Account, List<Guid> TeamIds);
}

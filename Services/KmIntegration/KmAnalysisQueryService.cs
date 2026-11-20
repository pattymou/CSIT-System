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

    public async Task<KmMetadataAdminPage> ListAdminAsync(ClaimsPrincipal user, string? sourceType,
        string? fileName, string? kmJobId, bool? hasWarnings, bool? missingSummary,
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        DemandAdmin(user);
        if (!string.IsNullOrWhiteSpace(sourceType) && !KmAnalysisSourceTypes.All.Contains(sourceType))
            throw new ArgumentException("Unknown sourceType.", nameof(sourceType));
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, 100);
        var normalizedFileName = NormalizeSearch(fileName);
        var normalizedJobId = NormalizeSearch(kmJobId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var useDatabaseJsonFilters = string.Equals(db.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);
        IQueryable<KmDocumentAnalysisResult> query = useDatabaseJsonFilters
            ? JsonFiltered(db, hasWarnings, missingSummary)
            : db.KmDocumentAnalysisResults.AsNoTracking();
        query = ExistingSources(db, query);
        if (!string.IsNullOrWhiteSpace(sourceType)) query = query.Where(x => x.SourceType == sourceType);
        if (normalizedJobId is not null)
            query = query.Where(x => x.KmJobId.ToLower().Contains(normalizedJobId));
        if (normalizedFileName is not null)
            query = query.Where(result =>
                result.SourceType == KmAnalysisSourceTypes.TestReport &&
                    db.ModuleCaseFiles.Any(file => file.Id == result.SourceEntityId &&
                        file.FileName.ToLower().Contains(normalizedFileName)) ||
                result.SourceType == KmAnalysisSourceTypes.DocumentLibraryFile &&
                    db.DocumentLibraryFiles.Any(file => file.Id == result.SourceEntityId &&
                        file.OriginalFileName.ToLower().Contains(normalizedFileName)) ||
                result.SourceType == KmAnalysisSourceTypes.VerificationApplicationAttachment &&
                    db.VerificationApplicationFiles.Any(file => file.Id == result.SourceEntityId &&
                        file.FileName.ToLower().Contains(normalizedFileName)) ||
                result.SourceType == KmAnalysisSourceTypes.VendorFile &&
                    db.VendorFiles.Any(file => file.Id == result.SourceEntityId &&
                        file.OriginalFileName.ToLower().Contains(normalizedFileName)) ||
                result.SourceType == KmAnalysisSourceTypes.EquipmentAttachment &&
                    db.ApparatusFiles.Any(file => file.Id == result.SourceEntityId &&
                        (file.OriginalFileName ?? file.FileName).ToLower().Contains(normalizedFileName)));

        List<KmDocumentAnalysisResult> rows;
        int total;
        if (useDatabaseJsonFilters)
        {
            total = await query.CountAsync(cancellationToken);
            rows = await query.OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        }
        else
        {
            var filtered = (await query.ToListAsync(cancellationToken))
                .Where(x => Matches(Inspect(x.ResultJson), hasWarnings, missingSummary))
                .OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id).ToList();
            total = filtered.Count;
            rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        var names = await ResolveFileNamesAsync(db, rows, cancellationToken);
        var items = rows.Select(row =>
        {
            var quality = Inspect(row.ResultJson);
            return new KmMetadataAdminListItem(row.Id,
                names.GetValueOrDefault((row.SourceType, row.SourceEntityId), "來源檔案已刪除"),
                row.SourceType, row.KmJobId, row.AnalyzedAt, row.MetadataSchemaVersion,
                quality.WarningCount, quality.MissingSummary);
        }).ToList();
        return new KmMetadataAdminPage(items, page, pageSize, total);
    }

    public async Task<KmAnalysisResultDetail?> GetAdminAsync(ClaimsPrincipal user, Guid id,
        CancellationToken cancellationToken = default)
    {
        DemandAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.KmDocumentAnalysisResults.AsNoTracking().Include(x => x.ReviewRevisions)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : Detail(row);
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

    private static IQueryable<KmDocumentAnalysisResult> ExistingSources(AppDbContext db,
        IQueryable<KmDocumentAnalysisResult> query) => query.Where(result =>
            result.SourceType == KmAnalysisSourceTypes.TestReport &&
                db.ModuleCaseFiles.Any(file => file.Id == result.SourceEntityId) ||
            result.SourceType == KmAnalysisSourceTypes.DocumentLibraryFile &&
                db.DocumentLibraryFiles.Any(file => file.Id == result.SourceEntityId) ||
            result.SourceType == KmAnalysisSourceTypes.VerificationApplicationAttachment &&
                db.VerificationApplicationFiles.Any(file => file.Id == result.SourceEntityId) ||
            result.SourceType == KmAnalysisSourceTypes.VendorFile &&
                db.VendorFiles.Any(file => file.Id == result.SourceEntityId) ||
            result.SourceType == KmAnalysisSourceTypes.EquipmentAttachment &&
                db.ApparatusFiles.Any(file => file.Id == result.SourceEntityId));

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

    private static void DemandAdmin(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Admin"))
            throw new UnauthorizedAccessException("Admin role is required.");
    }

    private static string? NormalizeSearch(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim().ToLowerInvariant();

    private static IQueryable<KmDocumentAnalysisResult> JsonFiltered(AppDbContext db,
        bool? hasWarnings, bool? missingSummary) => (hasWarnings, missingSummary) switch
    {
        (null, null) => db.KmDocumentAnalysisResults.AsNoTracking(),
        (true, null) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') > 0").AsNoTracking(),
        (false, null) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') = 0").AsNoTracking(),
        (null, true) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE COALESCE(result_json->'document'->>'summary', '') = ''").AsNoTracking(),
        (null, false) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE COALESCE(result_json->'document'->>'summary', '') <> ''").AsNoTracking(),
        (true, true) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') > 0 AND COALESCE(result_json->'document'->>'summary', '') = ''").AsNoTracking(),
        (true, false) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') > 0 AND COALESCE(result_json->'document'->>'summary', '') <> ''").AsNoTracking(),
        (false, true) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') = 0 AND COALESCE(result_json->'document'->>'summary', '') = ''").AsNoTracking(),
        (false, false) => db.KmDocumentAnalysisResults.FromSqlRaw(
            "SELECT * FROM km_document_analysis_results WHERE jsonb_array_length(result_json->'warnings') = 0 AND COALESCE(result_json->'document'->>'summary', '') <> ''").AsNoTracking()
    };

    private static MetadataQuality Inspect(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var warningCount = root.TryGetProperty("warnings", out var warnings) &&
                           warnings.ValueKind == JsonValueKind.Array
            ? warnings.GetArrayLength() : 0;
        var missingSummary = !root.TryGetProperty("document", out var metadata) ||
                             metadata.ValueKind != JsonValueKind.Object ||
                             !metadata.TryGetProperty("summary", out var summary) ||
                             summary.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                             summary.ValueKind == JsonValueKind.String &&
                             string.IsNullOrWhiteSpace(summary.GetString());
        return new MetadataQuality(warningCount, missingSummary);
    }

    private static bool Matches(MetadataQuality quality, bool? hasWarnings, bool? missingSummary) =>
        (!hasWarnings.HasValue || (quality.WarningCount > 0) == hasWarnings.Value) &&
        (!missingSummary.HasValue || quality.MissingSummary == missingSummary.Value);

    private static async Task<Dictionary<(string SourceType, Guid SourceEntityId), string>>
        ResolveFileNamesAsync(AppDbContext db, IReadOnlyCollection<KmDocumentAnalysisResult> rows,
            CancellationToken cancellationToken)
    {
        var names = new Dictionary<(string, Guid), string>();
        var ids = rows.Where(x => x.SourceType == KmAnalysisSourceTypes.TestReport)
            .Select(x => x.SourceEntityId).Distinct().ToArray();
        foreach (var item in await db.ModuleCaseFiles.AsNoTracking().Where(x => ids.Contains(x.Id))
                     .Select(x => new { x.Id, Name = x.FileName }).ToListAsync(cancellationToken))
            names[(KmAnalysisSourceTypes.TestReport, item.Id)] = item.Name;

        ids = rows.Where(x => x.SourceType == KmAnalysisSourceTypes.DocumentLibraryFile)
            .Select(x => x.SourceEntityId).Distinct().ToArray();
        foreach (var item in await db.DocumentLibraryFiles.AsNoTracking().Where(x => ids.Contains(x.Id))
                     .Select(x => new { x.Id, Name = x.OriginalFileName }).ToListAsync(cancellationToken))
            names[(KmAnalysisSourceTypes.DocumentLibraryFile, item.Id)] = item.Name;

        ids = rows.Where(x => x.SourceType == KmAnalysisSourceTypes.VerificationApplicationAttachment)
            .Select(x => x.SourceEntityId).Distinct().ToArray();
        foreach (var item in await db.VerificationApplicationFiles.AsNoTracking().Where(x => ids.Contains(x.Id))
                     .Select(x => new { x.Id, Name = x.FileName }).ToListAsync(cancellationToken))
            names[(KmAnalysisSourceTypes.VerificationApplicationAttachment, item.Id)] = item.Name;

        ids = rows.Where(x => x.SourceType == KmAnalysisSourceTypes.VendorFile)
            .Select(x => x.SourceEntityId).Distinct().ToArray();
        foreach (var item in await db.VendorFiles.AsNoTracking().Where(x => ids.Contains(x.Id))
                     .Select(x => new { x.Id, Name = x.OriginalFileName }).ToListAsync(cancellationToken))
            names[(KmAnalysisSourceTypes.VendorFile, item.Id)] = item.Name;

        ids = rows.Where(x => x.SourceType == KmAnalysisSourceTypes.EquipmentAttachment)
            .Select(x => x.SourceEntityId).Distinct().ToArray();
        foreach (var item in await db.ApparatusFiles.AsNoTracking().Where(x => ids.Contains(x.Id))
                     .Select(x => new { x.Id, Name = x.OriginalFileName ?? x.FileName })
                     .ToListAsync(cancellationToken))
            names[(KmAnalysisSourceTypes.EquipmentAttachment, item.Id)] = item.Name;
        return names;
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
    private sealed record MetadataQuality(int WarningCount, bool MissingSummary);
}

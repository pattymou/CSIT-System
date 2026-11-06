using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmMetadataChangeQueueService(
    IDbContextFactory<AppDbContext> dbFactory,
    KmAnalysisQueryService analysisQueries)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<KmMetadataChangeQueued> QueueAsync(ClaimsPrincipal user, Guid resultId,
        KmMetadataChangeQueueRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Classification is null || request.Attributes is null)
            throw new ArgumentException("A complete metadata snapshot is required.", nameof(request));
        if (await analysisQueries.GetAsync(user, resultId, cancellationToken) is null)
            throw new KeyNotFoundException("Analysis result was not found.");

        var classification = Normalize(request.Classification);
        var attributes = Normalize(request.Attributes);
        var account = Account(user);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var result = db.Database.IsRelational()
            ? await db.KmDocumentAnalysisResults.FromSqlInterpolated(
                $"SELECT * FROM km_document_analysis_results WHERE id = {resultId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.KmDocumentAnalysisResults.SingleOrDefaultAsync(x => x.Id == resultId,
                cancellationToken);
        if (result is null || result.Status != "Completed" ||
            result.SourceType is not (KmAnalysisSourceTypes.TestReport or
                KmAnalysisSourceTypes.DocumentLibraryFile) ||
            result.ReviewStatus != KmAnalysisReviewStatuses.Verified)
            throw new InvalidOperationException("Only a verified completed Task C result can be synchronized.");
        var review = await db.KmAnalysisReviewRevisions.Where(x => x.AnalysisResultId == result.Id)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken);
        if (review is null || review.Decision != KmAnalysisReviewStatuses.Verified ||
            review.ReviewedAt != result.ReviewedAt || review.ReviewedBy != result.ReviewedBy)
            throw new InvalidOperationException("The current Verified review evidence is incomplete.");
        var outbox = await db.KmIntegrationOutbox.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == result.OutboxId && x.Status == KmIntegrationStatus.Completed &&
            x.SourceEntityType == result.SourceType && x.SourceEntityId == result.SourceEntityId &&
            x.DocumentId == result.DocumentId && x.Version == result.Version,
            cancellationToken) ?? throw new InvalidOperationException("The canonical KM job is not Completed.");
        await DemandFormalSourceAsync(db, result, cancellationToken);

        var previous = await db.KmMetadataChanges.AsNoTracking().Where(x =>
                x.SourceType == result.SourceType && x.SourceEntityId == result.SourceEntityId &&
                x.DocumentId == result.DocumentId && x.Version == result.Version)
            .OrderByDescending(x => x.SourceRevision).FirstOrDefaultAsync(cancellationToken);
        if (previous is not null && previous.Status is not (KmMetadataChangeStatuses.Completed or
                KmMetadataChangeStatuses.Failed) ||
            previous is { Status: KmMetadataChangeStatuses.Failed, LastErrorRetryable: true })
            throw new InvalidOperationException("A prior metadata change is still active.");
        var baseline = previous is null
            ? await SourceSnapshotAsync(db, result, cancellationToken)
            : new Snapshot(
                Deserialize<KmV2DocumentClassification>(previous.ClassificationJson),
                Deserialize<KmMetadataAttributes>(previous.AttributesJson));
        var changed = ChangedFields(baseline, new Snapshot(classification, attributes));
        if (changed.Count == 0)
            throw new InvalidOperationException("The requested snapshot contains no metadata change.");

        var sourceRevision = Math.Max(result.SourceRevision,
            previous?.SourceRevision ?? result.SourceRevision) + 1;
        var changeId = Guid.NewGuid();
        var now = PostgreSqlTimestamp(DateTime.UtcNow);
        var payload = new KmMetadataChangeRequest("2.0", changeId, "CSIT", result.SourceType,
            result.SourceEntityId, result.DocumentId, result.Version, sourceRevision,
            new DateTimeOffset(now), classification, attributes, changed);
        var payloadHash = Sha(payload);
        var change = KmMetadataChange.Create(changeId, result.Id, review.Id, result.SourceType,
            result.SourceEntityId, result.DocumentId, result.Version, sourceRevision,
            JsonSerializer.Serialize(classification, JsonOptions),
            JsonSerializer.Serialize(attributes, JsonOptions),
            JsonSerializer.Serialize(changed, JsonOptions), payloadHash, account, now);
        db.KmMetadataChanges.Add(change);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(change.ChangeId, change.SourceType, change.SourceEntityId, change.DocumentId,
            change.Version, change.SourceRevision, changed, change.Status);
    }

    internal static KmMetadataChangeRequest ToRequest(KmMetadataChange row) => new(
        "2.0", row.ChangeId, row.SourceSystem, row.SourceType, row.SourceEntityId,
        row.DocumentId, row.Version, row.SourceRevision, new DateTimeOffset(
            DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)),
        Deserialize<KmV2DocumentClassification>(row.ClassificationJson),
        Deserialize<KmMetadataAttributes>(row.AttributesJson),
        Deserialize<List<string>>(row.ChangedFieldsJson));

    internal static string Sha<T>(T payload) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))).ToLowerInvariant();

    private static async Task DemandFormalSourceAsync(AppDbContext db,
        KmDocumentAnalysisResult result, CancellationToken cancellationToken)
    {
        if (result.SourceType == KmAnalysisSourceTypes.TestReport)
        {
            var file = await db.ModuleCaseFiles.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == result.SourceEntityId && x.DocumentId == result.DocumentId &&
                x.VersionNo == result.Version && x.FileKind == ModuleCaseFileKinds.TestReport &&
                x.ReviewStatus == TestReportReviewStatuses.Approved && x.IsRawDataExported &&
                x.Sha256 == result.SourceSha256, cancellationToken);
            if (file is null || await VerificationCaseDeletionHold.IsActiveAsync(db, file.RecordId,
                    cancellationToken))
                throw new InvalidOperationException("The TestReport is not an active formal source.");
            return;
        }
        var document = await db.DocumentLibraryFiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == result.SourceEntityId && (x.DocumentId == result.DocumentId ||
                x.DocumentId == Guid.Empty && x.Id == result.DocumentId) &&
            (x.Version == result.Version || x.Version == 0 && result.Version == 1) &&
            x.StorageArea == DocumentStorageAreas.Library && x.Sha256 == result.SourceSha256,
            cancellationToken);
        if (document is null || await DocumentLibraryFileDeletionHold.ExistsAsync(db, document.Id,
                cancellationToken))
            throw new InvalidOperationException("The DocumentLibrary version is not an active formal source.");
        var job = await db.DocumentProcessingJobs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DocumentFileId == document.Id && x.Status == DocumentProcessingStatuses.Completed &&
            x.ArchiveStatus == DocumentArchiveStatuses.Completed && x.ArchiveSha256 == document.Sha256,
            cancellationToken);
        if (job is null) throw new InvalidOperationException("The DocumentLibrary archive is incomplete.");
    }

    private static async Task<Snapshot> SourceSnapshotAsync(AppDbContext db,
        KmDocumentAnalysisResult result, CancellationToken cancellationToken)
    {
        if (result.SourceType == KmAnalysisSourceTypes.TestReport)
        {
            var report = await db.ModuleCaseFiles.AsNoTracking().SingleAsync(x => x.Id == result.SourceEntityId,
                cancellationToken);
            return new(new(null, null, null), EmptyAttributes(report.FileName));
        }
        var file = await db.DocumentLibraryFiles.AsNoTracking().Include(x => x.Folder)
            .ThenInclude(x => x.Parent).ThenInclude(x => x!.Parent)
            .SingleAsync(x => x.Id == result.SourceEntityId, cancellationToken);
        var l3 = file.Folder;
        var l2 = l3.Parent;
        var l1 = l2?.Parent;
        return new(new(Level(l1), Level(l2), Level(l3)), EmptyAttributes(file.OriginalFileName));
    }

    private static KmV2DocumentClassification Normalize(KmV2DocumentClassification value) =>
        new(Normalize(value.L1), Normalize(value.L2), Normalize(value.L3));
    private static KmV2ClassificationLevel? Normalize(KmV2ClassificationLevel? value) => value is null
        ? null : value.Id == Guid.Empty || string.IsNullOrWhiteSpace(value.Name) || value.Name.Trim().Length > 200
            ? throw new ArgumentException("Invalid classification level.")
            : new(value.Id, value.Name.Trim());
    private static KmMetadataAttributes Normalize(KmMetadataAttributes value) => new(
        Normalize(value.Tags, "tags"), Normalize(value.Keywords, "keywords"),
        Clean(value.DisplayName, 300), Normalize(value.ModelNumbers, "modelNumbers"),
        Clean(value.VendorReference, 300));
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values, string name)
    {
        if (values is null || values.Count > 200) throw new ArgumentException($"Invalid {name} snapshot.");
        return values.Select(x => Clean(x, 300) ?? throw new ArgumentException($"Invalid {name} value."))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("Metadata value is too large.");
    private static DateTime PostgreSqlTimestamp(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);
    private static KmV2ClassificationLevel? Level(DocumentFolder? value) => value is null
        ? null : new(value.Id, value.Name);
    private static KmMetadataAttributes EmptyAttributes(string displayName) =>
        new([], [], displayName, [], null);

    private static List<string> ChangedFields(Snapshot before, Snapshot after)
    {
        var fields = new List<string>();
        if (JsonSerializer.Serialize(before.Classification, JsonOptions) !=
            JsonSerializer.Serialize(after.Classification, JsonOptions)) fields.Add("classification");
        if (!before.Attributes.Tags.SequenceEqual(after.Attributes.Tags)) fields.Add("attributes.tags");
        if (!before.Attributes.Keywords.SequenceEqual(after.Attributes.Keywords)) fields.Add("attributes.keywords");
        if (before.Attributes.DisplayName != after.Attributes.DisplayName) fields.Add("attributes.displayName");
        if (!before.Attributes.ModelNumbers.SequenceEqual(after.Attributes.ModelNumbers)) fields.Add("attributes.modelNumbers");
        if (before.Attributes.VendorReference != after.Attributes.VendorReference) fields.Add("attributes.vendorReference");
        return fields;
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Stored metadata snapshot is invalid.");
    private static string Account(ClaimsPrincipal user) =>
        (user.FindFirst("account")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value)?.Trim().ToLowerInvariant()
        ?? throw new UnauthorizedAccessException("Account claim is required.");
    private sealed record Snapshot(KmV2DocumentClassification Classification,
        KmMetadataAttributes Attributes);
}

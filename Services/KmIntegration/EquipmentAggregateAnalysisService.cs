using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class EquipmentAggregateAnalysisException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

public sealed class EquipmentAggregateAnalysisService(
    AppDbContext db, IOptions<EquipmentAggregateAnalysisOptions> configured, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public async Task<EquipmentAggregateAnalysisView?> GetAsync(string apparatusId, string moduleCode,
        CancellationToken cancellationToken)
    {
        var apparatus = await db.Apparatuses.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == apparatusId && x.ModuleCode == moduleCode, cancellationToken);
        if (apparatus is null) return null;

        var manifest = await BuildManifestAsync(apparatusId, moduleCode, cancellationToken);
        var options = configured.Value;
        if (!options.Enabled)
            return new(apparatusId, moduleCode, false, manifest.Hash, manifest.Documents.Count,
                manifest.PendingDocumentCount, "FeatureDisabled", false, null, null, null, null);

        var now = clock.GetUtcNow().UtcDateTime;
        if (manifest.Documents.Count > 0)
            await EnsureRequestAsync(apparatusId, moduleCode, manifest, options.AnalysisVersion, now, cancellationToken);

        var snapshots = db.EquipmentAggregateAnalysisSnapshots.AsNoTracking()
            .Where(x => x.ApparatusId == apparatusId && x.ModuleCode == moduleCode);
        var snapshot = await snapshots
            .Where(x => x.ManifestHash == manifest.Hash && x.AnalysisVersion == options.AnalysisVersion)
            .OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(cancellationToken)
            ?? await snapshots.OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(cancellationToken);
        var request = await db.EquipmentAggregateAnalysisRequests.AsNoTracking()
            .Where(x => x.ApparatusId == apparatusId && x.ModuleCode == moduleCode &&
                x.ManifestHash == manifest.Hash && x.AnalysisVersion == options.AnalysisVersion)
            .OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(cancellationToken);

        var stale = snapshot is not null && (snapshot.ManifestHash != manifest.Hash ||
            snapshot.AnalysisVersion != options.AnalysisVersion);
        EquipmentAggregateAnalysisResult? result = null;
        if (snapshot is not null)
            result = JsonSerializer.Deserialize<EquipmentAggregateAnalysisResult>(snapshot.ResultJson, JsonOptions);
        var requestStatus = request?.Status == EquipmentAggregateAnalysisStatuses.Pending
            ? request.DispatchStatus switch
            {
                EquipmentAggregateDispatchStatuses.Failed => "Failed",
                EquipmentAggregateDispatchStatuses.Deferred => "Deferred",
                _ => "Pending"
            }
            : request?.Status;
        var status = stale ? "Stale" : snapshot is not null ? "Completed" :
            requestStatus ?? (manifest.Documents.Count == 0 ? "NoEligibleDocuments" : "Pending");
        return new(apparatusId, moduleCode, options.Enabled, manifest.Hash, manifest.Documents.Count,
            manifest.PendingDocumentCount, status, stale, snapshot?.Id, snapshot?.AnalyzedAt,
            snapshot?.ReviewStatus, result);
    }

    public async Task EnsureCurrentRequestAsync(string apparatusId, string moduleCode,
        CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.Enabled) return;
        var manifest = await BuildManifestAsync(apparatusId, moduleCode, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (manifest.Documents.Count > 0)
            await EnsureRequestAsync(apparatusId, moduleCode, manifest, options.AnalysisVersion,
                now, cancellationToken);
        else
            await SupersedePendingRequestsAsync(apparatusId, moduleCode, now, cancellationToken);
    }

    public async Task ApplyCallbackAsync(EquipmentAggregateAnalysisCallback callback,
        string payloadSha256, CancellationToken cancellationToken)
    {
        if (!configured.Value.Enabled)
            throw new EquipmentAggregateAnalysisException(409, "feature_disabled");
        if (callback.SchemaVersion != "1.0" || callback.CallbackId == Guid.Empty ||
            callback.RequestId == Guid.Empty || !IsSha256(callback.ManifestHash) || !IsSha256(payloadSha256) ||
            callback.Status is not ("Completed" or "Failed"))
            throw new EquipmentAggregateAnalysisException(400, "invalid_callback");

        IDbContextTransaction? transaction = null;
        if (db.Database.IsNpgsql())
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var identity = await db.EquipmentAggregateAnalysisRequests.AsNoTracking()
                .Where(x => x.Id == callback.RequestId)
                .Select(x => new { x.ApparatusId, x.ModuleCode })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new EquipmentAggregateAnalysisException(404, "request_not_found");
            await LockApparatusAsync(identity.ApparatusId, identity.ModuleCode, cancellationToken);
        }
        await using var transactionScope = transaction;

        var requestQuery = transaction is not null
            ? db.EquipmentAggregateAnalysisRequests.FromSqlInterpolated(
                $"SELECT * FROM equipment_aggregate_analysis_requests WHERE id = {callback.RequestId} FOR UPDATE")
            : db.EquipmentAggregateAnalysisRequests.Where(x => x.Id == callback.RequestId);
        var request = await requestQuery.SingleOrDefaultAsync(cancellationToken)
            ?? throw new EquipmentAggregateAnalysisException(404, "request_not_found");
        var receipt = await db.EquipmentAggregateAnalysisCallbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CallbackId == callback.CallbackId, cancellationToken);
        if (receipt is not null)
        {
            if (receipt.RequestId != callback.RequestId || receipt.PayloadSha256 != payloadSha256)
                throw new EquipmentAggregateAnalysisException(409, "callback_id_conflict");
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (request.Status == EquipmentAggregateAnalysisStatuses.Superseded ||
            request.ManifestHash != callback.ManifestHash || request.ApparatusId != callback.ApparatusId ||
            request.ModuleCode != callback.ModuleCode || request.AnalysisVersion != callback.AnalysisVersion)
            throw new EquipmentAggregateAnalysisException(409, "stale_or_mismatched_manifest");
        if (request.Status == EquipmentAggregateAnalysisStatuses.Failed)
            throw new EquipmentAggregateAnalysisException(409, "request_already_failed");

        var current = await BuildManifestAsync(request.ApparatusId, request.ModuleCode, cancellationToken);
        if (current.Hash != request.ManifestHash)
        {
            request.Supersede(clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            throw new EquipmentAggregateAnalysisException(409, "stale_manifest");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (callback.Status == "Completed")
        {
            if (callback.Result is null || callback.Error is not null || !callback.AnalyzedAt.HasValue ||
                !ValidResult(callback.Result, current.Documents))
                throw new EquipmentAggregateAnalysisException(400, "invalid_result");
            if (request.Status == EquipmentAggregateAnalysisStatuses.Completed)
            {
                var existing = await db.EquipmentAggregateAnalysisSnapshots.AsNoTracking()
                    .SingleAsync(x => x.RequestId == request.Id, cancellationToken);
                if (existing.ResultJson != JsonSerializer.Serialize(callback.Result, JsonOptions))
                    throw new EquipmentAggregateAnalysisException(409, "completed_result_conflict");
            }
            else
            {
                var json = JsonSerializer.Serialize(callback.Result, JsonOptions);
                db.EquipmentAggregateAnalysisSnapshots.Add(EquipmentAggregateAnalysisSnapshot.Create(
                    request, json, callback.AnalyzedAt.Value.UtcDateTime, now));
                request.Complete(now);
            }
        }
        else
        {
            if (callback.Result is not null || callback.Error is null)
                throw new EquipmentAggregateAnalysisException(400, "invalid_failure");
            if (request.Status == EquipmentAggregateAnalysisStatuses.Pending)
                request.Fail(callback.Error.Code, callback.Error.Message, now);
        }

        db.EquipmentAggregateAnalysisCallbacks.Add(new()
        {
            CallbackId = callback.CallbackId, RequestId = request.Id,
            PayloadSha256 = payloadSha256, ReceivedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReviewAsync(Guid snapshotId, ClaimsPrincipal user,
        EquipmentAggregateAnalysisReviewRequest review, CancellationToken cancellationToken)
    {
        var actor = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException();
        var snapshot = await db.EquipmentAggregateAnalysisSnapshots.SingleOrDefaultAsync(
            x => x.Id == snapshotId, cancellationToken) ?? throw new KeyNotFoundException();
        snapshot.Review(review.Decision, actor, review.Note, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<EquipmentAggregateAnalysisRequestContract?> GetPendingRequestAsync(Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!configured.Value.Enabled) return null;
        var request = await db.EquipmentAggregateAnalysisRequests.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == requestId && x.Status == EquipmentAggregateAnalysisStatuses.Pending, cancellationToken);
        if (request is null) return null;
        var documents = JsonSerializer.Deserialize<List<EquipmentAggregateSourceDocument>>(
            request.SourceDocumentsJson, JsonOptions) ?? [];
        return new("1.0", request.Id, "CSIT", request.ModuleCode, request.ApparatusId,
            request.ManifestHash, request.AnalysisVersion, documents,
            new DateTimeOffset(DateTime.SpecifyKind(request.RequestedAt, DateTimeKind.Utc)));
    }

    private async Task EnsureRequestAsync(string apparatusId, string moduleCode, Manifest manifest,
        string analysisVersion, DateTime now, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsNpgsql())
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await LockApparatusAsync(apparatusId, moduleCode, cancellationToken);
        }
        await using var transactionScope = transaction;

        var existing = await db.EquipmentAggregateAnalysisRequests.SingleOrDefaultAsync(x =>
            x.ApparatusId == apparatusId && x.ModuleCode == moduleCode &&
            x.ManifestHash == manifest.Hash && x.AnalysisVersion == analysisVersion, cancellationToken);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return;
        }
        var pending = await db.EquipmentAggregateAnalysisRequests.Where(x =>
            x.ApparatusId == apparatusId && x.ModuleCode == moduleCode &&
            x.Status == EquipmentAggregateAnalysisStatuses.Pending).ToListAsync(cancellationToken);
        pending.ForEach(x => x.Supersede(now));
        db.EquipmentAggregateAnalysisRequests.Add(EquipmentAggregateAnalysisRequest.Create(
            Guid.NewGuid(), apparatusId, moduleCode, manifest.Hash, analysisVersion,
            JsonSerializer.Serialize(manifest.Documents, JsonOptions), now));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task SupersedePendingRequestsAsync(string apparatusId, string moduleCode,
        DateTime now, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsNpgsql())
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken);
            await LockApparatusAsync(apparatusId, moduleCode, cancellationToken);
        }
        await using var transactionScope = transaction;
        var pending = await db.EquipmentAggregateAnalysisRequests.Where(x =>
            x.ApparatusId == apparatusId && x.ModuleCode == moduleCode &&
            x.Status == EquipmentAggregateAnalysisStatuses.Pending).ToListAsync(cancellationToken);
        pending.ForEach(x => x.Supersede(now));
        if (pending.Count > 0) await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task LockApparatusAsync(string apparatusId, string moduleCode,
        CancellationToken cancellationToken)
    {
        // Materializing Apparatus from a raw subquery asks EF for its xmin column,
        // which PostgreSQL does not expose through SELECT * on that subquery.
        var lockedId = await db.Database.SqlQuery<string>(
                $"SELECT \"Id\" AS \"Value\" FROM apparatus WHERE \"Id\" = {apparatusId} AND \"ModuleCode\" = {moduleCode} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (lockedId is null)
            throw new EquipmentAggregateAnalysisException(404, "apparatus_not_found");
    }

    private async Task<Manifest> BuildManifestAsync(string apparatusId, string moduleCode,
        CancellationToken cancellationToken)
    {
        var files = await db.ApparatusFiles.AsNoTracking().Where(x => x.ApparatusId == apparatusId &&
            x.Apparatus.ModuleCode == moduleCode && x.ArchiveStatus == "Archived" && x.Sha256 != null)
            .Select(x => new { x.Id, Name = x.OriginalFileName ?? x.FileName, Sha256 = x.Sha256! })
            .ToListAsync(cancellationToken);
        var fileIds = files.Select(x => x.Id).ToArray();
        var deleting = await db.EquipmentFileDeletionJobs.AsNoTracking().Where(x =>
            fileIds.Contains(x.ApparatusFileId))
            .Select(x => x.ApparatusFileId).ToListAsync(cancellationToken);
        var results = await db.KmDocumentAnalysisResults.AsNoTracking().Where(x =>
            fileIds.Contains(x.SourceEntityId) && x.SourceType == KmAnalysisSourceTypes.EquipmentAttachment &&
            x.Status == "Completed" && x.SupersededAt == null).ToListAsync(cancellationToken);
        var documents = new List<EquipmentAggregateSourceDocument>();
        foreach (var file in files.Where(x => !deleting.Contains(x.Id)).OrderBy(x => x.Id))
        {
            var result = results.Where(x => x.SourceEntityId == file.Id &&
                    string.Equals(x.SourceSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.SourceRevision).ThenByDescending(x => x.ReceivedAt).FirstOrDefault();
            if (result is not null)
                documents.Add(new(file.Id, result.Version, file.Sha256.ToLowerInvariant(), result.KmJobId,
                    result.AnalysisResultId, file.Name, result.AnalyzedAt));
        }
        var canonical = new StringBuilder().Append("1\n").Append(moduleCode).Append('\n')
            .Append(apparatusId).Append('\n');
        foreach (var item in documents)
            canonical.Append(item.FileId.ToString("N")).Append('|').Append(item.Version).Append('|')
                .Append(item.Sha256).Append('|').Append(item.KmJobId).Append('|')
                .Append(item.AnalysisResultId.ToString("N")).Append('\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return new(hash, documents, files.Count(x => !deleting.Contains(x.Id)) - documents.Count);
    }

    private static bool ValidResult(EquipmentAggregateAnalysisResult result,
        IReadOnlyList<EquipmentAggregateSourceDocument> documents)
    {
        if (result.TechnicalSpecifications is null || result.TestCapabilities is null ||
            result.Limitations is null || result.Conflicts is null || result.Evidence is null ||
            result.Warnings is null) return false;
        var sources = documents.ToDictionary(x => x.FileId);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in result.Evidence)
            if (string.IsNullOrWhiteSpace(evidence.EvidenceId) || !evidenceIds.Add(evidence.EvidenceId) ||
                !sources.TryGetValue(evidence.FileId, out var source) ||
                source.AnalysisResultId != evidence.AnalysisResultId || string.IsNullOrWhiteSpace(evidence.Description))
                return false;
        return result.TechnicalSpecifications.All(x => ValidEvidence(x.EvidenceIds, evidenceIds) &&
                   !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value)) &&
               result.TestCapabilities.All(x => ValidEvidence(x.EvidenceIds, evidenceIds) &&
                   !string.IsNullOrWhiteSpace(x.Name)) &&
               result.Conflicts.All(x => ValidEvidence(x.EvidenceIds, evidenceIds) &&
                   !string.IsNullOrWhiteSpace(x.Field) && !string.IsNullOrWhiteSpace(x.Description));
    }

    private static bool ValidEvidence(IReadOnlyList<string>? ids, HashSet<string> valid) =>
        ids is not null && ids.Count > 0 && ids.All(valid.Contains);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private sealed record Manifest(string Hash, IReadOnlyList<EquipmentAggregateSourceDocument> Documents,
        int PendingDocumentCount);
}

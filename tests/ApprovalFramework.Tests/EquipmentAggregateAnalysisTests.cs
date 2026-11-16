using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentAggregateAnalysisTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Manifest_CallbackAndChangedCollection_AreCorrelatedAndIdempotent()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new AppDbContext(options);
        var first = SeedDocument(db, "EQ-AGG-1", "manual.pdf", ShaA, "KM-1");
        await db.SaveChangesAsync();
        var service = new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions { Enabled = true, AnalysisVersion = "1.0" }),
            TimeProvider.System);

        var pending = Assert.IsType<EquipmentAggregateAnalysisView>(
            await service.GetAsync("EQ-AGG-1", "equipment", default));
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.EligibleDocumentCount);
        var request = Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        var outbound = Assert.IsType<EquipmentAggregateAnalysisRequestContract>(
            await service.GetPendingRequestAsync(request.Id, default));
        Assert.Equal("EQ-AGG-1", outbound.ApparatusId);
        Assert.Equal(first.File.Id, Assert.Single(outbound.Documents).FileId);

        var result = Result(first.File.Id, first.AnalysisResultId);
        var callback = new EquipmentAggregateAnalysisCallback("1.0", Guid.NewGuid(), request.Id,
            "equipment", "EQ-AGG-1", request.ManifestHash, "1.0", "Completed", result, null,
            DateTimeOffset.UtcNow);
        var payloadHash = Hash(JsonSerializer.Serialize(callback));
        await service.ApplyCallbackAsync(callback, payloadHash, default);
        await service.ApplyCallbackAsync(callback, payloadHash, default);
        Assert.Single(await db.EquipmentAggregateAnalysisSnapshots.ToListAsync());
        Assert.Single(await db.EquipmentAggregateAnalysisCallbacks.ToListAsync());
        Assert.Equal("Completed", (await service.GetAsync("EQ-AGG-1", "equipment", default))!.Status);

        SeedDocument(db, "EQ-AGG-1", "spec.xlsx", ShaB, "KM-2");
        await db.SaveChangesAsync();
        var changed = (await service.GetAsync("EQ-AGG-1", "equipment", default))!;
        Assert.True(changed.IsStale);
        Assert.Equal("Stale", changed.Status);
        Assert.NotEqual(request.ManifestHash, changed.ManifestHash);
        Assert.Equal(2, await db.EquipmentAggregateAnalysisRequests.CountAsync());

        var currentRequest = await db.EquipmentAggregateAnalysisRequests.SingleAsync(x =>
            x.ManifestHash == changed.ManifestHash);
        var currentResult = ResultForDocuments(await db.KmDocumentAnalysisResults
            .OrderBy(x => x.SourceEntityId).ToListAsync());
        var currentCallback = new EquipmentAggregateAnalysisCallback("1.0", Guid.NewGuid(), currentRequest.Id,
            "equipment", "EQ-AGG-1", currentRequest.ManifestHash, "1.0", "Completed", currentResult, null,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await service.ApplyCallbackAsync(currentCallback,
            Hash(JsonSerializer.Serialize(currentCallback)), default);
        var current = (await service.GetAsync("EQ-AGG-1", "equipment", default))!;
        Assert.False(current.IsStale);
        Assert.Equal("Consolidated 2 documents", current.Result!.Overview);

        var late = callback with { CallbackId = Guid.NewGuid() };
        var exception = await Assert.ThrowsAsync<EquipmentAggregateAnalysisException>(() =>
            service.ApplyCallbackAsync(late, Hash(JsonSerializer.Serialize(late)), default));
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(2, await db.EquipmentAggregateAnalysisSnapshots.CountAsync());
        Assert.Equal("Consolidated 2 documents",
            (await service.GetAsync("EQ-AGG-1", "equipment", default))!.Result!.Overview);

        var callbackConflict = callback with { Result = result with { Overview = "different payload" } };
        var conflict = await Assert.ThrowsAsync<EquipmentAggregateAnalysisException>(() =>
            service.ApplyCallbackAsync(callbackConflict,
                Hash(JsonSerializer.Serialize(callbackConflict)), default));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("callback_id_conflict", conflict.Error);

        var disabled = new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions { Enabled = false }), TimeProvider.System);
        var disabledView = (await disabled.GetAsync("EQ-AGG-1", "equipment", default))!;
        Assert.Equal("FeatureDisabled", disabledView.Status);
        Assert.Null(disabledView.Result);
        Assert.Null(disabledView.SnapshotId);
        var disabledCallback = await Assert.ThrowsAsync<EquipmentAggregateAnalysisException>(() =>
            disabled.ApplyCallbackAsync(currentCallback,
                Hash(JsonSerializer.Serialize(currentCallback)), default));
        Assert.Equal("feature_disabled", disabledCallback.Error);
    }

    [Fact]
    public async Task FeatureDisabled_DoesNotCreateRequestOrSynthesizeResult()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new AppDbContext(options);
        SeedDocument(db, "EQ-OFF", "manual.pdf", ShaA, "KM-1");
        await db.SaveChangesAsync();
        var service = new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions { Enabled = false }), TimeProvider.System);

        var view = (await service.GetAsync("EQ-OFF", "equipment", default))!;
        Assert.Equal("FeatureDisabled", view.Status);
        Assert.Null(view.Result);
        Assert.Empty(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
    }

    [Fact]
    public async Task PendingDocument_PreservesSnapshotAndDoesNotCreateDuplicateRequest()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new AppDbContext(options);
        var completed = SeedDocument(db, "EQ-PENDING", "manual.pdf", ShaA, "KM-1");
        await db.SaveChangesAsync();
        var service = new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions { Enabled = true, AnalysisVersion = "1.0" }),
            TimeProvider.System);

        await service.GetAsync("EQ-PENDING", "equipment", default);
        var request = Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        var callback = new EquipmentAggregateAnalysisCallback("1.0", Guid.NewGuid(), request.Id,
            "equipment", "EQ-PENDING", request.ManifestHash, "1.0", "Completed",
            Result(completed.File.Id, completed.AnalysisResultId), null, DateTimeOffset.UtcNow);
        await service.ApplyCallbackAsync(callback, Hash(JsonSerializer.Serialize(callback)), default);

        SeedPendingDocument(db, "EQ-PENDING", "new-spec.pdf", ShaB);
        await db.SaveChangesAsync();
        var firstRead = (await service.GetAsync("EQ-PENDING", "equipment", default))!;
        var secondRead = (await service.GetAsync("EQ-PENDING", "equipment", default))!;

        Assert.Equal("Completed", firstRead.Status);
        Assert.Equal(1, firstRead.PendingDocumentCount);
        Assert.Equal("Consolidated tester", firstRead.Result!.Overview);
        Assert.Equal(firstRead.ManifestHash, secondRead.ManifestHash);
        Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
    }

    [Fact]
    public void Migration_CreatesIndependentAggregateTablesAndKeepsSingleFileContract()
    {
        var root = FindRoot();
        var migration = File.ReadAllText(Path.Combine(root, "Migrations",
            "20261116083516_AddEquipmentAggregateAnalysis.cs"));
        Assert.Contains("equipment_aggregate_analysis_requests", migration);
        Assert.Contains("equipment_aggregate_analysis_snapshots", migration);
        Assert.Contains("equipment_aggregate_analysis_callbacks", migration);
        Assert.DoesNotContain("km_document_analysis_results", migration);
    }

    private static (ApparatusFile File, Guid AnalysisResultId) SeedDocument(AppDbContext db,
        string apparatusId, string fileName, string sha, string kmJobId)
    {
        var now = DateTime.UtcNow;
        var equipment = db.Apparatuses.Local.SingleOrDefault(x => x.Id == apparatusId);
        if (equipment is null)
        {
            equipment = new Apparatus { Id = apparatusId, ModuleCode = "equipment", Name = "Tester", Kind = "Test", CreatedAt = now, UpdatedAt = now };
            db.Apparatuses.Add(equipment);
        }
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = apparatusId, Apparatus = equipment, FileName = fileName,
            OriginalFileName = fileName, FilePath = "formal", ContentType = "application/pdf",
            FileSize = 10, Sha256 = sha, ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = now, CreatedAt = now
        };
        equipment.Files.Add(file);
        db.ApparatusFiles.Add(file);
        var outbox = KmIntegrationOutbox.ForArchivedEquipmentDocument(equipment, file, now);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        outbox.Sent(lease, kmJobId, now);
        outbox.CallbackCompleted(now);
        db.KmIntegrationOutbox.Add(outbox);
        var analysisId = Guid.NewGuid();
        db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(
            analysisId, Guid.NewGuid(), outbox, KmAnalysisSourceTypes.EquipmentAttachment,
            file.Id, file.Id, 1, file.Id, kmJobId, sha, "2.0", ValidSingleFileResult(),
            now, now, now, 1, new string('c', 64)));
        return (file, analysisId);
    }

    private static ApparatusFile SeedPendingDocument(AppDbContext db, string apparatusId,
        string fileName, string sha)
    {
        var equipment = db.Apparatuses.Local.Single(x => x.Id == apparatusId);
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = apparatusId, Apparatus = equipment, FileName = fileName,
            OriginalFileName = fileName, FilePath = "formal", ContentType = "application/pdf",
            FileSize = 10, Sha256 = sha, ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        equipment.Files.Add(file);
        db.ApparatusFiles.Add(file);
        return file;
    }

    private static EquipmentAggregateAnalysisResult Result(Guid fileId, Guid analysisResultId) => new(
        "Consolidated tester", [new("Frequency", "6", "GHz", ["E1"])],
        [new("RF validation", "Validated from source", ["E1"])], ["Indoor use"], [],
        [new("E1", fileId, analysisResultId, "Manual specification", "page 2")], []);

    private static EquipmentAggregateAnalysisResult ResultForDocuments(
        IReadOnlyList<KmDocumentAnalysisResult> results)
    {
        var evidence = results.Select((x, index) => new EquipmentAggregateEvidence(
            $"E{index + 1}", x.SourceEntityId, x.AnalysisResultId, "Document specification", "page 1")).ToList();
        return new($"Consolidated {results.Count} documents",
            [new("Frequency", "6", "GHz", evidence.Select(x => x.EvidenceId).ToList())],
            [new("RF validation", "Validated from sources", evidence.Select(x => x.EvidenceId).ToList())],
            [], [], evidence, []);
    }

    private static string ValidSingleFileResult() => """
        {"metadataSchemaVersion":"2.0","document":{"title":"Manual","documentType":"Manual","summary":"Source","language":"en","documentDate":null,"authors":[],"department":null,"documentVersionLabel":null,"keywords":[],"tags":[]},"technical":{"productNames":[],"modelNumbers":[],"vendorNames":[],"technologies":[],"standards":[],"softwareVersions":[],"hardwareVersions":[],"projectNames":[],"testItems":[],"issueKeys":[],"errorCodes":[],"measurements":[]},"extraction":{"sourceFormat":"pdf","method":"Text","pageCount":1,"sheetNames":[],"extractedCharacterCount":10,"ocrUsed":false},"indexing":{"status":"Completed","indexId":"idx","chunkCount":1,"indexedAt":"2026-11-16T00:00:00Z"},"warnings":[],"sourceSpecific":{}}
        """;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}

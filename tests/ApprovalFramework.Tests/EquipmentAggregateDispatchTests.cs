using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentAggregateDispatchTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task CompletedSingleFileAnalysis_AutomaticallyCreatesAndDispatchesAggregateRequest()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var first = SeedCompletedDocument(factory, "EQ-AUTO", "manual.pdf", ShaA, "KM-1");
        var km = new AggregateKmClient(KmDeliveryResult.Success("KM-AGG-1"));

        Assert.Equal(1, await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync());

        await using var db = factory.CreateDbContext();
        var request = Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        Assert.Equal(EquipmentAggregateDispatchStatuses.Accepted, request.DispatchStatus);
        Assert.Equal("KM-AGG-1", request.KmJobId);
        Assert.Equal(1, request.DispatchAttemptCount);
        Assert.Equal(request.Id, km.LastRequest!.RequestId);
        Assert.Equal(first.FileId, Assert.Single(km.LastRequest.Documents).FileId);

        Assert.Equal(0, await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync());
        Assert.Equal(1, km.CallCount);
        Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
    }

    [Fact]
    public async Task NewDocumentChangesManifest_SupersedesOldRequestAndDispatchesExactlyOnce()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        SeedCompletedDocument(factory, "EQ-MULTI", "manual.pdf", ShaA, "KM-1");
        var km = new AggregateKmClient(KmDeliveryResult.Success("KM-AGG-1"),
            KmDeliveryResult.Success("KM-AGG-2"));
        await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync();

        SeedCompletedDocument(factory, "EQ-MULTI", "spec.xlsx", ShaB, "KM-2");
        Assert.Equal(1, await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync());

        await using var db = factory.CreateDbContext();
        var rows = await db.EquipmentAggregateAnalysisRequests.OrderBy(x => x.RequestedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(EquipmentAggregateAnalysisStatuses.Superseded, rows[0].Status);
        Assert.Equal(EquipmentAggregateDispatchStatuses.Superseded, rows[0].DispatchStatus);
        Assert.Equal(EquipmentAggregateDispatchStatuses.Accepted, rows[1].DispatchStatus);
        Assert.Equal(2, km.LastRequest!.Documents.Count);
        Assert.NotEqual(rows[0].ManifestHash, rows[1].ManifestHash);
    }

    [Fact]
    public async Task InvalidatedLastDocument_SupersedesPendingAcceptedRequestWithoutResend()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var seeded = SeedCompletedDocument(factory, "EQ-DELETE", "manual.pdf", ShaA, "KM-1");
        var km = new AggregateKmClient(KmDeliveryResult.Success("KM-AGG-1"));
        await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync();
        await using (var mutate = factory.CreateDbContext())
        {
            var file = await mutate.ApparatusFiles.SingleAsync(x => x.Id == seeded.FileId);
            file.ArchiveStatus = "Deleted";
            await mutate.SaveChangesAsync();
        }

        Assert.Equal(0, await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync());
        await using var db = factory.CreateDbContext();
        var request = Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        Assert.Equal(EquipmentAggregateAnalysisStatuses.Superseded, request.Status);
        Assert.Equal(EquipmentAggregateDispatchStatuses.Superseded, request.DispatchStatus);
        Assert.Equal(1, km.CallCount);
    }

    [Fact]
    public async Task AggregateFeatureOff_DoesNotCreateOrDispatchRequest()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        SeedCompletedDocument(factory, "EQ-OFF", "manual.pdf", ShaA, "KM-1");
        var km = new AggregateKmClient(KmDeliveryResult.Success("unexpected"));

        Assert.Equal(0, await Worker(factory, km, aggregateEnabled: false).ProcessBatchAsync());
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        Assert.Equal(0, km.CallCount);
    }

    [Fact]
    public async Task KmFeatureDisabled503_IsDeferredAndDoesNotConsumeRetryBudget()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        SeedCompletedDocument(factory, "EQ-DEFER", "manual.pdf", ShaA, "KM-1");
        var km = new AggregateKmClient(KmDeliveryResult.Failure(true, "KM_FEATURE_DISABLED",
            "KM equipment aggregate analysis is temporarily disabled."));

        Assert.Equal(1, await Worker(factory, km, aggregateEnabled: true).ProcessBatchAsync());
        await using var db = factory.CreateDbContext();
        var request = Assert.Single(await db.EquipmentAggregateAnalysisRequests.ToListAsync());
        Assert.Equal(EquipmentAggregateDispatchStatuses.Deferred, request.DispatchStatus);
        Assert.NotNull(request.DispatchNextRetryAt);
        Assert.Equal(1, request.DispatchAttemptCount);
        Assert.Null(request.KmJobId);
    }

    [Fact]
    public async Task FailedCallback_IsIdempotentAndPreservesFailureEvidence()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        SeedCompletedDocument(factory, "EQ-FAILED", "manual.pdf", ShaA, "KM-1");
        await using var db = factory.CreateDbContext();
        var service = new EquipmentAggregateAnalysisService(db,
            Options.Create(new EquipmentAggregateAnalysisOptions
            {
                Enabled = true, AnalysisVersion = "1.0"
            }), TimeProvider.System);
        await service.EnsureCurrentRequestAsync("EQ-FAILED", "equipment", default);
        var request = await db.EquipmentAggregateAnalysisRequests.SingleAsync();
        var callback = new EquipmentAggregateAnalysisCallback("1.0", Guid.NewGuid(), request.Id,
            "equipment", "EQ-FAILED", request.ManifestHash, "1.0", "Failed", null,
            new KmV2CallbackError("NO_TECHNICAL_CONTENT", "No usable analysis was produced.", false),
            null);

        await service.ApplyCallbackAsync(callback, ShaB, default);
        await service.ApplyCallbackAsync(callback, ShaB, default);

        Assert.Equal(EquipmentAggregateAnalysisStatuses.Failed, request.Status);
        Assert.Equal("NO_TECHNICAL_CONTENT", request.FailureCode);
        Assert.Single(await db.EquipmentAggregateAnalysisCallbacks.ToListAsync());
        Assert.Empty(await db.EquipmentAggregateAnalysisSnapshots.ToListAsync());
    }

    [Fact]
    public void DispatchLease_RestartRecoveryRetryLimitAndDeferredPolicy_AreBounded()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var firstLease = request.ClaimDispatch(now, TimeSpan.FromSeconds(10), 3);
        Assert.Throws<InvalidOperationException>(() =>
            request.ClaimDispatch(now.AddSeconds(1), TimeSpan.FromSeconds(10), 3));
        var recovered = request.ClaimDispatch(now.AddSeconds(11), TimeSpan.FromSeconds(10), 3);
        request.FailDispatch(recovered, "HTTP_500", "temporary", true, false,
            now.AddSeconds(12), 3, now.AddSeconds(11));
        var last = request.ClaimDispatch(now.AddSeconds(12), TimeSpan.FromSeconds(10), 3);
        request.FailDispatch(last, "HTTP_500", "temporary", true, false, null, 3,
            now.AddSeconds(12));
        Assert.Equal(EquipmentAggregateDispatchStatuses.Failed, request.DispatchStatus);
        Assert.Equal(3, request.DispatchAttemptCount);

        var deferred = Request(now);
        for (var i = 0; i < 5; i++)
        {
            var at = now.AddMinutes(i);
            var lease = deferred.ClaimDispatch(at, TimeSpan.FromSeconds(10), 3);
            deferred.FailDispatch(lease, "KM_FEATURE_DISABLED", "disabled", true, true,
                at.AddSeconds(1), 3, at);
        }
        Assert.Equal(EquipmentAggregateDispatchStatuses.Deferred, deferred.DispatchStatus);
        Assert.Equal(5, deferred.DispatchAttemptCount);
    }

    [Fact]
    public async Task HttpClient_SendsExactContractAndValidatesAcceptedResponse()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var request = Contract();
        var handler = new StubHandler(async message =>
        {
            captured = message;
            body = await message.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Accepted,
                $"{{\"requestId\":\"{request.RequestId:D}\",\"kmJobId\":\"KM-AGG-1\",\"status\":\"Accepted\"}}");
        });

        var result = await Client(handler).SubmitEquipmentAggregateAnalysisAsync(request, default);

        Assert.True(result.Accepted);
        Assert.Equal("KM-AGG-1", result.JobId);
        Assert.Equal(KmIntegrationClient.EquipmentAggregateAnalysisPath, captured!.RequestUri!.AbsolutePath);
        Assert.Equal(request.RequestId.ToString("D"), captured.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Contains("\"schemaVersion\":\"1.0\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"requestId\":\"{request.RequestId:D}\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(409, false)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public async Task HttpClient_ClassifiesStatusCodes(int status, bool retryable)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json((HttpStatusCode)status, "{}")));
        var result = await Client(handler).SubmitEquipmentAggregateAnalysisAsync(Contract(), default);
        Assert.False(result.Accepted);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal("HTTP_" + status, result.ErrorCode);
    }

    [Fact]
    public async Task HttpClient_RecognizesFeatureDisabled503AndRejectsMismatched202()
    {
        var disabled = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable,
            "{\"detail\":\"Equipment aggregate analysis disabled; no requests accepted\"}")));
        var deferred = await Client(disabled).SubmitEquipmentAggregateAnalysisAsync(Contract(), default);
        Assert.Equal("KM_FEATURE_DISABLED", deferred.ErrorCode);
        Assert.True(deferred.Retryable);

        var mismatch = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.Accepted,
            $"{{\"requestId\":\"{Guid.NewGuid():D}\",\"kmJobId\":\"KM-X\",\"status\":\"Accepted\"}}")));
        var invalid = await Client(mismatch).SubmitEquipmentAggregateAnalysisAsync(Contract(), default);
        Assert.Equal("INVALID_RESPONSE", invalid.ErrorCode);
        Assert.False(invalid.Retryable);
    }

    [Fact]
    public async Task HttpClient_ClassifiesTimeoutAndConnectionFailureAsRetryable()
    {
        var cancelled = new CancellationToken(canceled: true);
        var timeout = new StubHandler(_ =>
            Task.FromCanceled<HttpResponseMessage>(cancelled));
        var timeoutResult = await Client(timeout)
            .SubmitEquipmentAggregateAnalysisAsync(Contract(), default);
        Assert.Equal("TIMEOUT", timeoutResult.ErrorCode);
        Assert.True(timeoutResult.Retryable);

        var connection = new StubHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")));
        var connectionResult = await Client(connection)
            .SubmitEquipmentAggregateAnalysisAsync(Contract(), default);
        Assert.Equal("CONNECTION", connectionResult.ErrorCode);
        Assert.True(connectionResult.Retryable);
    }

    [Fact]
    public void AggregateUi_ExplicitlyLabelsWarningOnlyOrEmptyResultAsInsufficient()
    {
        var root = FindRoot();
        var component = File.ReadAllText(Path.Combine(root, "Components", "Shared",
            "EquipmentMetadataSummary.razor"));
        Assert.Contains("資料不足／沒有可用分析", component, StringComparison.Ordinal);
        Assert.Contains("!HasSubstantiveAnalysis(_view.Result)", component, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchMigration_IsIncrementalReviewableAndDefaultsExistingRowsToPending()
    {
        var root = FindRoot();
        var migration = File.ReadAllText(Path.Combine(root, "Migrations",
            "20261117055627_AddEquipmentAggregateDispatch.cs"));
        Assert.Contains("defaultValue: \"Pending\"", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable", migration, StringComparison.Ordinal);
        var sqlPath = Path.Combine(root, "docs", "equipment-aggregate-dispatch-migration.sql");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sqlPath)));
        Assert.Equal("1F81A0BEF3A46D0F1216120BAA36EAB146B849A6F652E09545FD7F965C3697E5", hash);
    }

    private static KmSyncWorker Worker(TestDbFactory factory, IKmIntegrationClient client,
        bool aggregateEnabled) => new(factory, client, Options.Create(new KmIntegrationOptions
        {
            Enabled = true, EquipmentEnabled = true, BaseUrl = "https://km.invalid/",
            ServiceBaseUrl = "https://csit.invalid/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 3, RetryScheduleSeconds = [1], PollSeconds = 1
        }), NullLogger<KmSyncWorker>.Instance, documentStorage: null,
        aggregateConfigured: Options.Create(new EquipmentAggregateAnalysisOptions
        {
            Enabled = aggregateEnabled, AnalysisVersion = "1.0"
        }));

    private static (Guid FileId, Guid AnalysisResultId) SeedCompletedDocument(TestDbFactory factory,
        string apparatusId, string name, string sha, string kmJobId)
    {
        using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var apparatus = db.Apparatuses.SingleOrDefault(x => x.Id == apparatusId) ?? new Apparatus
        {
            Id = apparatusId, ModuleCode = "equipment", Name = "Tester", Kind = "Test",
            CreatedAt = now, UpdatedAt = now
        };
        if (db.Entry(apparatus).State == EntityState.Detached) db.Apparatuses.Add(apparatus);
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = apparatus.Id, Apparatus = apparatus, FileName = name,
            OriginalFileName = name, FilePath = "formal", ContentType = "application/pdf",
            FileSize = 10, Sha256 = sha, ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = now, CreatedAt = now
        };
        db.ApparatusFiles.Add(file);
        var outbox = KmIntegrationOutbox.ForArchivedEquipmentDocument(apparatus, file, now);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, now, TimeSpan.FromMinutes(1));
        outbox.Sent(lease, kmJobId, now);
        outbox.CallbackCompleted(now);
        db.KmIntegrationOutbox.Add(outbox);
        var resultId = Guid.NewGuid();
        db.KmDocumentAnalysisResults.Add(KmDocumentAnalysisResult.Create(resultId, Guid.NewGuid(),
            outbox, KmAnalysisSourceTypes.EquipmentAttachment, file.Id, file.Id, 1, file.Id,
            kmJobId, sha, "2.0", ValidSingleFileResult(), now, now, now, 1, new string('c', 64)));
        db.SaveChanges();
        return (file.Id, resultId);
    }

    private static EquipmentAggregateAnalysisRequest Request(DateTime now) =>
        EquipmentAggregateAnalysisRequest.Create(Guid.NewGuid(), "EQ-LEASE", "equipment",
            ShaA, "1.0", "[]", now);

    private static EquipmentAggregateAnalysisRequestContract Contract()
    {
        var fileId = Guid.NewGuid();
        return new("1.0", Guid.NewGuid(), "CSIT", "equipment", "EQ-HTTP", ShaA, "1.0",
            [new(fileId, 1, ShaA, "KM-1", Guid.NewGuid(), "manual.pdf", DateTime.UtcNow)],
            DateTimeOffset.UtcNow);
    }

    private static KmIntegrationClient Client(HttpMessageHandler handler) => new(
        new HttpClient(handler), Options.Create(new KmIntegrationOptions
        {
            Enabled = true, BaseUrl = "https://km.example/", TimeoutSeconds = 2
        }), new TokenProvider());

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static string ValidSingleFileResult() => """
        {"metadataSchemaVersion":"2.0","document":{"title":"Manual","documentType":"Manual","summary":"Source","language":"en","documentDate":null,"authors":[],"department":null,"documentVersionLabel":null,"keywords":[],"tags":[]},"technical":{"productNames":[],"modelNumbers":[],"vendorNames":[],"technologies":[],"standards":[],"softwareVersions":[],"hardwareVersions":[],"projectNames":[],"testItems":[],"issueKeys":[],"errorCodes":[],"measurements":[]},"extraction":{"sourceFormat":"pdf","method":"Text","pageCount":1,"sheetNames":[],"extractedCharacterCount":10,"ocrUsed":false},"indexing":{"status":"Completed","indexId":"idx","chunkCount":1,"indexedAt":"2026-11-17T00:00:00Z"},"warnings":[],"sourceSpecific":{}}
        """;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class AggregateKmClient(params KmDeliveryResult[] results) : IKmIntegrationClient
    {
        private readonly Queue<KmDeliveryResult> _results = new(results);
        public EquipmentAggregateAnalysisRequestContract? LastRequest { get; private set; }
        public int CallCount { get; private set; }
        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(
            KmDeliveryResult.Failure(false, "UNEXPECTED", "Unexpected single-document request."));
        public Task<KmDeliveryResult> SubmitEquipmentAggregateAnalysisAsync(
            EquipmentAggregateAnalysisRequestContract request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }

    private sealed class TokenProvider : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("test-token");
    }

    private sealed class TestDbFactory(string database) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

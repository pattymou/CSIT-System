using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class CanonicalKmContractTests
{
    private static readonly Guid DocumentId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    private static readonly string ContractDirectory = FindContractDirectory();

    [Fact]
    public async Task IngestionProducerUsesCanonicalSchemaAndNumberFields()
    {
        var factory = new ContractDbFactory();
        var file = Seed(factory);
        string? wireJson = null;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(KmIntegrationClient.IngestionPath, request.RequestUri!.AbsolutePath);
            wireJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new
                {
                    jobId = "contract-job-001", sourceSystem = "CSIT",
                    documentId = file.DocumentId, version = file.VersionNo, status = "Accepted"
                })
            };
        }));
        var options = Options.Create(EnabledOptions());
        var client = new KmIntegrationClient(http, options, new ContractToken());
        var worker = new KmSyncWorker(factory, client, options, NullLogger<KmSyncWorker>.Instance);

        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.NotNull(wireJson);
        Assert.True(Valid("ingestion-request.schema.json", wireJson));
        using var document = JsonDocument.Parse(wireJson);
        foreach (var level in new[] { "project", "case", "task" })
        {
            var value = document.RootElement.GetProperty(level);
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            Assert.True(value.TryGetProperty("number", out _));
            Assert.False(value.TryGetProperty("code", out _));
            Assert.True(Guid.TryParse(value.GetProperty("id").GetString(), out _));
        }
        Assert.Equal(file.DocumentId, document.RootElement.GetProperty("documentId").GetGuid());
        using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Sent, check.KmIntegrationOutbox.Single().Status);
    }

    [Theory]
    [InlineData("Processing", KmIntegrationStatus.Processing)]
    [InlineData("Completed", KmIntegrationStatus.Completed)]
    [InlineData("Failed", KmIntegrationStatus.Failed)]
    public async Task CanonicalCallbackFixturePassesSchemaAndRealConsumer(
        string status, KmIntegrationStatus expected)
    {
        var factory = new ContractDbFactory();
        Seed(factory);
        MarkSent(factory);
        var name = status.ToLowerInvariant();
        var json = File.ReadAllText(Path.Combine(ContractDirectory, "fixtures", $"callback-{name}.json"));
        Assert.True(Valid($"callback-{name}.schema.json", json));
        var callback = JsonSerializer.Deserialize<KmStatusCallback>(json, WireOptions);
        Assert.NotNull(callback);
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);

        Assert.IsType<NoContentResult>(await controller.Status(callback, default));
        using var check = factory.CreateDbContext();
        Assert.Equal(expected, check.KmIntegrationOutbox.Single().Status);
    }

    [Theory]
    [InlineData("Processing", "startedAt")]
    [InlineData("Completed", "completedAt")]
    [InlineData("Failed", "failedAt")]
    public async Task MissingStatusTimestampFailsSchemaAndConsumer(string status, string property)
    {
        var factory = new ContractDbFactory();
        Seed(factory);
        MarkSent(factory);
        var name = status.ToLowerInvariant();
        var node = Fixture(name);
        node["timestamps"]!.AsObject().Remove(property);
        var json = node.ToJsonString();
        Assert.False(Valid($"callback-{name}.schema.json", json));
        var callback = JsonSerializer.Deserialize<KmStatusCallback>(json, WireOptions);
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<BadRequestObjectResult>(await controller.Status(callback!, default));
        using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Sent, check.KmIntegrationOutbox.Single().Status);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("message")]
    public async Task MissingFailedErrorPartFailsSchemaAndConsumer(string property)
    {
        var factory = new ContractDbFactory();
        Seed(factory);
        MarkSent(factory);
        var node = Fixture("failed");
        node["error"]!.AsObject().Remove(property);
        var json = node.ToJsonString();
        Assert.False(Valid("callback-failed.schema.json", json));
        var callback = JsonSerializer.Deserialize<KmStatusCallback>(json, WireOptions);
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<BadRequestObjectResult>(await controller.Status(callback!, default));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("case")]
    [InlineData("task")]
    public void CodeCannotReplaceNumber(string level)
    {
        var request = new KmIngestionRequest(Guid.NewGuid(), "TestReportCommitted", "CSIT",
            DocumentId, 3, new KmReference(Guid.NewGuid(), "P1", "Project"),
            new KmReference(Guid.NewGuid(), "C1", "Case"),
            new KmReference(Guid.NewGuid(), "T1", "Task"),
            new KmDocumentMetadata("report.pdf", "application/pdf", 38815), DateTime.UtcNow);
        var node = JsonSerializer.SerializeToNode(request, WireOptions)!.AsObject();
        var reference = node[level]!.AsObject();
        reference["code"] = reference["number"]!.DeepClone();
        reference.Remove("number");
        Assert.False(Valid("ingestion-request.schema.json", node.ToJsonString()));
    }

    [Theory]
    [InlineData("eventId")]
    [InlineData("documentId")]
    public void GuidFieldsRequireUuidWireFormat(string field)
    {
        var request = new KmIngestionRequest(Guid.NewGuid(), "TestReportCommitted", "CSIT",
            DocumentId, 3, new KmReference(Guid.NewGuid(), "P1", "Project"),
            new KmReference(Guid.NewGuid(), "C1", "Case"),
            new KmReference(Guid.NewGuid(), "T1", "Task"),
            new KmDocumentMetadata("report.pdf", "application/pdf", 38815), DateTime.UtcNow);
        var node = JsonSerializer.SerializeToNode(request, WireOptions)!.AsObject();
        node[field] = "not-a-uuid";
        Assert.False(Valid("ingestion-request.schema.json", node.ToJsonString()));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("case")]
    [InlineData("task")]
    public void ReferenceIdsRequireUuidWireFormat(string level)
    {
        var node = SampleRequest();
        node[level]!["id"] = "not-a-uuid";
        Assert.False(Valid("ingestion-request.schema.json", node.ToJsonString()));
    }

    [Fact]
    public void ReferenceNullabilityMatchesDtoAndWorkerInvariant()
    {
        var node = SampleRequest();
        node["project"]!["number"] = null;
        node["case"]!["name"] = null;
        Assert.True(Valid("ingestion-request.schema.json", node.ToJsonString()));
        node["case"] = null;
        Assert.False(Valid("ingestion-request.schema.json", node.ToJsonString()));
    }

    [Fact]
    public void InvalidCallbackTimestampIsRejectedByFormatAssertion()
    {
        var node = Fixture("processing");
        node["timestamps"]!["startedAt"] = "not-a-date-time";
        Assert.False(Valid("callback-processing.schema.json", node.ToJsonString()));
    }

    private static JsonObject SampleRequest()
    {
        var request = new KmIngestionRequest(Guid.NewGuid(), "TestReportCommitted", "CSIT",
            DocumentId, 3, new KmReference(Guid.NewGuid(), "P1", "Project"),
            new KmReference(Guid.NewGuid(), "C1", "Case"),
            new KmReference(Guid.NewGuid(), "T1", "Task"),
            new KmDocumentMetadata("report.pdf", "application/pdf", 38815), DateTime.UtcNow);
        return JsonSerializer.SerializeToNode(request, WireOptions)!.AsObject();
    }

    private static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ContractDirectory, "fixtures",
            $"callback-{name}.json")))!.AsObject();

    private static bool Valid(string schemaFile, string json)
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            ContractDirectory, schemaFile)));
        using var instance = JsonDocument.Parse(json);
        var schema = JsonSchema.Build(schemaDocument.RootElement);
        return schema.Evaluate(instance.RootElement,
            new EvaluationOptions { RequireFormatValidation = true }).IsValid;
    }

    private static string FindContractDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null;
             current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "contracts", "csit-km-integration-v1");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Canonical KM contract directory is missing.");
    }

    private static ModuleCaseFile Seed(ContractDbFactory factory)
    {
        var projectId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var file = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), DocumentId = DocumentId, VersionNo = 3,
            RecordId = projectId, CaseId = caseId, TaskId = taskId,
            FileKind = ModuleCaseFileKinds.TestReport,
            ReviewStatus = TestReportReviewStatuses.Approved,
            IsRawDataExported = true, FileName = "report.pdf",
            ContentType = "application/pdf", FileSize = 38815
        };
        using var db = factory.CreateDbContext();
        db.ModuleRecords.Add(new ModuleRecord { Id = projectId, RecordNo = "P1", Name = "Project" });
        db.ModuleRecordCases.Add(new ModuleRecordCase { Id = caseId, RecordId = projectId,
            CaseNo = "C1", Name = "Case" });
        db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = taskId, CaseId = caseId,
            TaskNo = "T1", Name = "Task" });
        db.ModuleCaseFiles.Add(file);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(file, DateTime.UtcNow));
        db.SaveChanges();
        return file;
    }

    private static void MarkSent(ContractDbFactory factory)
    {
        using var db = factory.CreateDbContext();
        var row = db.KmIntegrationOutbox.Single();
        var now = DateTime.UtcNow;
        var lease = Guid.NewGuid();
        row.Claim(lease, now, TimeSpan.FromMinutes(10));
        row.Sent(lease, "contract-job-001", now);
        db.SaveChanges();
    }

    private static KmIntegrationOptions EnabledOptions() => new()
    {
        Enabled = true, BaseUrl = "https://example.invalid",
        TimeoutSeconds = 2, MaxRetryCount = 3, PollSeconds = 1,
        LeaseSeconds = 30, RetryScheduleSeconds = [60]
    };

    private sealed class ContractDbFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ContractToken : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("test-token");
    }

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}

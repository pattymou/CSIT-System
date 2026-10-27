using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class TestReportKmEndToEndTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FormalReportsFlowThroughMockKmAndPersistAllStatesInIsolatedPostgreSql()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_TESTREPORT_KM_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var connectionInfo = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", connectionInfo.Host);
        Assert.Equal(55432, connectionInfo.Port);
        Assert.StartsWith("csit_testreport_km_e2e_", connectionInfo.Database, StringComparison.Ordinal);
        Assert.Equal("postgres", connectionInfo.Username);
        Assert.True(string.IsNullOrEmpty(connectionInfo.Password));

        var factory = new PostgreSqlFactory(connection);
        var storage = Path.Combine(Path.GetTempPath(), "csit-testreport-km-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        try
        {
            var normal = await SeedProjectAsync(factory, storage, "NORMAL");
            var v1 = await CommitInitialAsync(factory, storage, normal.TaskId, "report-v1.pdf", true);
            var v2 = await CommitVersionAsync(factory, storage, v1.Id, "report-v2.pdf", true);
            var pending = await SeedProjectAsync(factory, storage, "PENDING");
            var pendingReport = await CommitInitialAsync(factory, storage, pending.TaskId,
                "pending-report.pdf", false);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var outbox = await db.KmIntegrationOutbox.AsNoTracking()
                    .Where(x => x.SourceEntityId == v1.Id || x.SourceEntityId == v2.Id)
                    .OrderBy(x => x.Version).ToListAsync();
                Assert.Equal([1, 2], outbox.Select(x => x.Version).ToArray());
                Assert.All(outbox, x => Assert.Equal(KmIntegrationStatus.Pending, x.Status));
                Assert.False(await db.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == pendingReport.Id));
            }

            await AssertUniqueOutboxConstraintAsync(factory, v1);

            await using var mockKm = await MockKmIngestionServer.StartAsync();
            await using var callbacks = await CsitKmStatusCallbackTestServer.StartAsync(factory);
            var options = OptionsFor(timeoutSeconds: 2, leaseSeconds: 8);
            var firstWorker = Worker(factory, mockKm, options);
            var secondWorker = Worker(factory, mockKm, options);
            var concurrent = await Task.WhenAll(firstWorker.ProcessBatchAsync(), secondWorker.ProcessBatchAsync());
            Assert.Equal(2, concurrent.Sum());
            Assert.Equal(2, mockKm.RequestCount);

            KmIntegrationOutbox v1Outbox;
            KmIntegrationOutbox v2Outbox;
            await using (var db = await factory.CreateDbContextAsync())
            {
                v1Outbox = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.SourceEntityId == v1.Id);
                v2Outbox = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.SourceEntityId == v2.Id);
                Assert.Equal(KmIntegrationStatus.Sent, v1Outbox.Status);
                Assert.Equal(KmIntegrationStatus.Sent, v2Outbox.Status);
            }

            var processing = Callback(v1Outbox, "Processing");
            using (var unauthorized = await callbacks.SendAsync(processing, authorize: false))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var wrongJob = await callbacks.SendAsync(processing with { JobId = "wrong-job" }))
                Assert.Equal(HttpStatusCode.Conflict, wrongJob.StatusCode);
            using (var wrongDocument = await callbacks.SendAsync(processing with { DocumentId = Guid.NewGuid() }))
                Assert.Equal(HttpStatusCode.Conflict, wrongDocument.StatusCode);
            using (var wrongVersion = await callbacks.SendAsync(processing with { Version = 99 }))
                Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
            using (var response = await callbacks.SendAsync(processing))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(KmIntegrationStatus.Processing,
                    (await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.Id == v1Outbox.Id)).Status);

            var completed = Callback(v1Outbox, "Completed");
            using (var response = await callbacks.SendAsync(completed))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            DateTime? completedAt;
            await using (var db = await factory.CreateDbContextAsync())
                completedAt = (await db.KmIntegrationOutbox.AsNoTracking()
                    .SingleAsync(x => x.Id == v1Outbox.Id)).CompletedAt;
            using (var duplicate = await callbacks.SendAsync(completed))
                Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(completedAt, (await db.KmIntegrationOutbox.AsNoTracking()
                    .SingleAsync(x => x.Id == v1Outbox.Id)).CompletedAt);

            var failed = Callback(v2Outbox, "Failed");
            using (var response = await callbacks.SendAsync(failed))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var persisted = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.Id == v2Outbox.Id);
                Assert.Equal(KmIntegrationStatus.Failed, persisted.Status);
                Assert.Equal("KM_ANALYSIS_FAILED", persisted.LastErrorCode);
                Assert.Contains("synthetic", persisted.LastErrorMessage);
            }

            var requestsBeforeRestart = mockKm.RequestCount;
            Assert.Equal(0, await Worker(factory, mockKm, options).ProcessBatchAsync());
            Assert.Equal(requestsBeforeRestart, mockKm.RequestCount);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var work = new VerificationWorkManagementService(db, new EmptyEngineerDirectory(),
                    new AllowWrite(), Options.Create(new KmIntegrationOptions { Enabled = true }));
                var reports = (await work.GetAsync(normal.RecordId))!.Tasks.Single().Reports
                    .ToDictionary(x => x.VersionNo);
                Assert.Equal("完成", reports[1].KmAnalysisStatusDisplay);
                Assert.Equal("失敗", reports[2].KmAnalysisStatusDisplay);
                Assert.Equal("KM_ANALYSIS_FAILED：synthetic KM failure", reports[2].KmAnalysisError);
            }

            var retryProject = await SeedProjectAsync(factory, storage, "RETRY");
            var retryReport = await CommitInitialAsync(factory, storage, retryProject.TaskId,
                "retry-report.pdf", true);
            mockKm.FailNext(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            await AssertFailureAsync(factory, retryReport.Id, "HTTP_503", retryScheduled: true);
            await Task.Delay(1100);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            await AssertStatusAsync(factory, retryReport.Id, KmIntegrationStatus.Sent, retryCount: 1);

            var timeoutProject = await SeedProjectAsync(factory, storage, "TIMEOUT");
            var timeoutReport = await CommitInitialAsync(factory, storage, timeoutProject.TaskId,
                "timeout-report.pdf", true);
            var timeoutOptions = OptionsFor(timeoutSeconds: 1, leaseSeconds: 7);
            mockKm.DelayNext(TimeSpan.FromSeconds(2));
            Assert.Equal(1, await Worker(factory, mockKm, timeoutOptions).ProcessBatchAsync());
            await AssertFailureAsync(factory, timeoutReport.Id, "TIMEOUT", retryScheduled: true);
            await Task.Delay(1100);
            Assert.Equal(1, await Worker(factory, mockKm, timeoutOptions).ProcessBatchAsync());
            await AssertStatusAsync(factory, timeoutReport.Id, KmIntegrationStatus.Sent, retryCount: 1);

            var leaseProject = await SeedProjectAsync(factory, storage, "LEASE");
            var leaseReport = await CommitInitialAsync(factory, storage, leaseProject.TaskId,
                "lease-report.pdf", true);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.KmIntegrationOutbox.SingleAsync(x => x.SourceEntityId == leaseReport.Id);
                row.Claim(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(1));
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            await AssertStatusAsync(factory, leaseReport.Id, KmIntegrationStatus.Sent, retryCount: 1);

            var countBeforePendingProbe = mockKm.RequestCount;
            Assert.Equal(0, await Worker(factory, mockKm, options).ProcessBatchAsync());
            Assert.Equal(countBeforePendingProbe, mockKm.RequestCount);
            await AssertConnectionFailureLeavesCommittedStateAsync(factory, connectionInfo, v1Outbox.Id,
                completedAt!.Value);
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public async Task FormalTestReportFlowsThroughMockKmAndAuthorizedCallbacks()
    {
        var factory = new TestFactory();
        var file = Seed(factory);
        await using var mockKm = await MockKmIngestionServer.StartAsync();
        await using var callbackHost = await CsitKmStatusCallbackTestServer.StartAsync(factory);
        var options = new KmIntegrationOptions
        {
            Enabled = true, BaseUrl = "https://mock-km.test/", PollSeconds = 1,
            TimeoutSeconds = 5, LeaseSeconds = 20, MaxRetryCount = 3,
            RetryScheduleSeconds = [1, 2, 3]
        };
        var client = new KmIntegrationClient(mockKm.Client, Options.Create(options), new MockTokenProvider());
        var worker = new KmSyncWorker(factory, client, Options.Create(options),
            NullLogger<KmSyncWorker>.Instance);

        Assert.Equal(1, await worker.ProcessBatchAsync());
        using (var sent = factory.CreateDbContext())
        {
            var row = sent.KmIntegrationOutbox.Single();
            Assert.Equal(KmIntegrationStatus.Sent, row.Status);
            Assert.Equal(mockKm.JobId, row.KmJobId);
            Assert.Equal(file.DocumentId, mockKm.LastRequest!.DocumentId);
            Assert.Equal(file.VersionNo, mockKm.LastRequest.Version);
            Assert.Equal(1, mockKm.RequestCount);
        }

        var processing = new KmStatusCallback(mockKm.JobId, "CSIT", file.DocumentId, file.VersionNo,
            "Processing", new KmCallbackTimestamps(DateTime.UtcNow, null, null), null);
        using (var unauthorized = await callbackHost.SendAsync(processing, authorize: false))
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using (var response = await callbackHost.SendAsync(processing))
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        using (var db = factory.CreateDbContext())
            Assert.Equal(KmIntegrationStatus.Processing, db.KmIntegrationOutbox.Single().Status);

        var completed = new KmStatusCallback(mockKm.JobId, "CSIT", file.DocumentId, file.VersionNo,
            "Completed", new KmCallbackTimestamps(null, DateTime.UtcNow, null), null);
        using (var response = await callbackHost.SendAsync(completed))
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        using (var duplicate = await callbackHost.SendAsync(completed))
            Assert.Equal(System.Net.HttpStatusCode.NoContent, duplicate.StatusCode);
        using var verified = factory.CreateDbContext();
        var persisted = verified.KmIntegrationOutbox.Single();
        Assert.Equal(KmIntegrationStatus.Completed, persisted.Status);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Equal(1, mockKm.RequestCount);
    }

    private static async Task<(Guid RecordId, Guid TaskId)> SeedProjectAsync(
        PostgreSqlFactory factory, string storage, string scenario)
    {
        var tag = $"PG-KM-{scenario}-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var moduleId = Guid.NewGuid();
        await using (var infrastructure = await factory.CreateDbContextAsync())
        {
            var module = await infrastructure.Modules.SingleOrDefaultAsync(x => x.Code == "verification");
            if (module is null)
            {
                module = new ModuleEntity { Id = moduleId, Code = "verification", Name = "Verification",
                    DisplayName = "Verification", RoutePrefix = "verification", IsEnabled = true,
                    CreatedAt = now, UpdatedAt = now };
                infrastructure.Modules.Add(module);
            }
            else moduleId = module.Id;
            var root = await infrastructure.SystemOptions.SingleOrDefaultAsync(x =>
                x.Category == "ThreeLevelRootPath" && x.Name == "verification");
            if (root is null)
                infrastructure.SystemOptions.Add(new SystemOption { Id = Guid.NewGuid(),
                    Category = "ThreeLevelRootPath", Name = "verification",
                    Value = Path.Combine(storage, "formal"), IsEnabled = true,
                    CreatedAt = now, UpdatedAt = now });
            else
            {
                root.Value = Path.Combine(storage, "formal");
                root.IsEnabled = true;
                root.UpdatedAt = now;
            }
            var reportRoot = await infrastructure.SystemOptions.SingleOrDefaultAsync(x =>
                x.Category == "TestReportRootPath" && x.Name == "default");
            if (reportRoot is null)
                infrastructure.SystemOptions.Add(new SystemOption { Id = Guid.NewGuid(),
                    Category = "TestReportRootPath", Name = "default",
                    Value = Path.Combine(storage, "reports"), IsEnabled = true,
                    CreatedAt = now, UpdatedAt = now });
            else
            {
                reportRoot.Value = Path.Combine(storage, "reports");
                reportRoot.IsEnabled = true;
                reportRoot.UpdatedAt = now;
            }
            await infrastructure.SaveChangesAsync();
        }

        var recordId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var testItemCode = "ITEM-" + tag[^12..];
        var record = new ModuleRecord { Id = recordId, ModuleId = moduleId, RecordNo = tag,
            Name = tag, CustomerProductName = "MODEL-" + scenario, Status = "Open", Team = "PG-TEST",
            ExpectedEndDate = DateOnly.FromDateTime(now.AddMonths(1)), CreatedAt = now, UpdatedAt = now };
        var recordCase = new ModuleRecordCase { Id = caseId, RecordId = recordId,
            CaseNo = "CASE-" + Guid.NewGuid().ToString("N"), Name = "Radio", Status = "Open",
            SourceTestCategoryCode = "RADIO", CreatedAt = now, UpdatedAt = now };
        var task = new ModuleRecordTask { Id = taskId, CaseId = caseId,
            TaskNo = "TASK-" + Guid.NewGuid().ToString("N"), Name = "Wi-Fi", Status = "Open",
            SourceTestItemCode = testItemCode, CreatedAt = now, UpdatedAt = now };
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var item = new ValidationTestItemDto(Guid.NewGuid(), categoryId, testItemCode, "Wi-Fi", null,
            null, false, true, false, 1, []);
        var category = new ValidationTestCategoryDto(categoryId, "RADIO", "Radio", true, true, 1, [item]);
        var submission = new ValidationRequestSubmissionDto
        {
            TemplateVersionId = templateId, TemplateVersion = 1, ProductTypeCode = "PG-TEST",
            SelectedTestItemCodes = new HashSet<string>([testItemCode], StringComparer.OrdinalIgnoreCase),
            FormSnapshot = new ValidationRequestPreviewDto("VALIDATION", templateId, 1,
                "PG-TEST", "PostgreSQL Test", [], [category])
        };
        var application = VerificationApplication.CreateDraft(Guid.NewGuid(),
            "VA-PG-" + Guid.NewGuid().ToString("N")[..20],
            departmentId, teamId, "pg-test", "PostgreSQL Test", "pg@example.test", "TEST", null,
            new VerificationApplicationContent { ProjectName = tag, ProductModel = "MODEL-" + scenario,
                Customer = "Synthetic PostgreSQL Customer" }, now);
        application.SetValidationRequest(templateId, 1, "PG-TEST",
            JsonSerializer.Serialize(submission, new JsonSerializerOptions(JsonSerializerDefaults.Web)), now);
        application.Submit(new VerificationApplicationRouting(departmentId, "TEST", "Test", teamId,
            "PG-TEST", "PostgreSQL Test", "verification", "leader", "Leader"), now);
        application.Accept(recordId, record.ExpectedEndDate!.Value, "leader", now);

        await using var db = await factory.CreateDbContextAsync();
        db.AddRange(
            new SystemOption { Id = departmentId, Category = SystemOptionCategories.Department,
                Name = "TEST-" + tag[^12..], Value = "TEST", IsEnabled = true,
                CreatedAt = now, UpdatedAt = now },
            new SystemOption { Id = teamId, Category = SystemOptionCategories.Team,
                Name = "PG-" + tag[^12..], Value = "PG-TEST", IsEnabled = true,
                CreatedAt = now, UpdatedAt = now },
            record, recordCase, task, application);
        await db.SaveChangesAsync();
        return (recordId, taskId);
    }

    private static async Task<ModuleCaseFile> CommitInitialAsync(PostgreSqlFactory factory,
        string storage, Guid taskId, string name, bool autoApprove)
    {
        await using var db = await factory.CreateDbContextAsync();
        var service = FileService(db, storage);
        await service.UploadTaskReportAsync(taskId, [Upload(name)], "pg-test", autoApprove,
            approvedBy: autoApprove ? "pg-supervisor" : null);
        return await db.ModuleCaseFiles.AsNoTracking().SingleAsync(x => x.TaskId == taskId);
    }

    private static async Task<ModuleCaseFile> CommitVersionAsync(PostgreSqlFactory factory,
        string storage, Guid previousId, string name, bool autoApprove)
    {
        await using var db = await factory.CreateDbContextAsync();
        var service = FileService(db, storage);
        await service.UploadNewTestReportVersionAsync(previousId, Upload(name), "pg-test", autoApprove,
            approvedBy: autoApprove ? "pg-supervisor" : null);
        return await db.ModuleCaseFiles.AsNoTracking().Where(x => x.DocumentId ==
            db.ModuleCaseFiles.Where(y => y.Id == previousId).Select(y => y.DocumentId).Single())
            .OrderByDescending(x => x.VersionNo).FirstAsync();
    }

    private static CaseFileService FileService(AppDbContext db, string storage)
    {
        var raw = Options.Create(new RawDataSettings { Enabled = true,
            NasRootPath = Path.Combine(storage, "raw") });
        return new CaseFileService(db,
            Options.Create(new UploadSettings { BasePath = Path.Combine(storage, "formal"), UploadKind = "Lab" }),
            raw, new RawDataExportService(raw), NullLogger<CaseFileService>.Instance);
    }

    private static IFormFile Upload(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("synthetic PostgreSQL TestReport " + name);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    private static KmIntegrationOptions OptionsFor(int timeoutSeconds, int leaseSeconds) => new()
    {
        Enabled = true, BaseUrl = "https://mock-km.test/", PollSeconds = 1,
        TimeoutSeconds = timeoutSeconds, LeaseSeconds = leaseSeconds, MaxRetryCount = 3,
        RetryScheduleSeconds = [1]
    };

    private static KmSyncWorker Worker(PostgreSqlFactory factory, MockKmIngestionServer mock,
        KmIntegrationOptions options) => new(factory,
        new KmIntegrationClient(mock.Client, Options.Create(options), new MockTokenProvider()),
        Options.Create(options), NullLogger<KmSyncWorker>.Instance);

    private static KmStatusCallback Callback(KmIntegrationOutbox row, string status) => new(
        row.KmJobId!, "CSIT", row.DocumentId, row.Version, status,
        new KmCallbackTimestamps(status == "Processing" ? DateTime.UtcNow : null,
            status == "Completed" ? DateTime.UtcNow : null,
            status == "Failed" ? DateTime.UtcNow : null),
        status == "Failed" ? new KmCallbackError("KM_ANALYSIS_FAILED", "synthetic KM failure") : null);

    private static async Task AssertUniqueOutboxConstraintAsync(PostgreSqlFactory factory,
        ModuleCaseFile report)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(report, DateTime.UtcNow));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation,
                Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using var verified = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verified.KmIntegrationOutbox.CountAsync(x => x.SourceEntityId == report.Id));
    }

    private static async Task AssertFailureAsync(PostgreSqlFactory factory, Guid reportId,
        string code, bool retryScheduled)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.SourceEntityId == reportId);
        Assert.Equal(KmIntegrationStatus.Failed, row.Status);
        Assert.Equal(code, row.LastErrorCode);
        Assert.Equal(retryScheduled, row.NextRetryAt.HasValue);
    }

    private static async Task AssertStatusAsync(PostgreSqlFactory factory, Guid reportId,
        KmIntegrationStatus status, int retryCount)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.SourceEntityId == reportId);
        Assert.Equal(status, row.Status);
        Assert.Equal(retryCount, row.RetryCount);
    }

    private static async Task AssertConnectionFailureLeavesCommittedStateAsync(PostgreSqlFactory factory,
        NpgsqlConnectionStringBuilder valid, Guid completedOutboxId, DateTime completedAt)
    {
        var unavailable = new NpgsqlConnectionStringBuilder(valid.ConnectionString)
        {
            Port = 55431, Timeout = 1, CommandTimeout = 1, Pooling = false
        };
        var badFactory = new PostgreSqlFactory(unavailable.ConnectionString);
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var db = await badFactory.CreateDbContextAsync();
            await db.KmIntegrationOutbox.CountAsync();
        });
        Assert.True(failure is NpgsqlException || failure.InnerException is NpgsqlException,
            $"Unexpected database connection exception: {failure.GetType().Name}");
        await using var verified = await factory.CreateDbContextAsync();
        var row = await verified.KmIntegrationOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == completedOutboxId);
        Assert.Equal(KmIntegrationStatus.Completed, row.Status);
        Assert.Equal(completedAt, row.CompletedAt);
    }

    private static ModuleCaseFile Seed(TestFactory factory)
    {
        var now = DateTime.UtcNow;
        var record = new ModuleRecord { Id = Guid.NewGuid(), ModuleId = Guid.NewGuid(), RecordNo = "MOCK-PROJECT",
            Name = "Mock project", CreatedAt = now, UpdatedAt = now };
        var recordCase = new ModuleRecordCase { Id = Guid.NewGuid(), RecordId = record.Id,
            CaseNo = "MOCK-CASE", Name = "Mock case", CreatedAt = now, UpdatedAt = now };
        var task = new ModuleRecordTask { Id = Guid.NewGuid(), CaseId = recordCase.Id,
            TaskNo = "MOCK-TASK", Name = "Mock task", CreatedAt = now, UpdatedAt = now };
        var file = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = record.Id, CaseId = recordCase.Id, CaseNo = recordCase.CaseNo,
            TaskId = task.Id, TaskNo = task.TaskNo, FileName = "synthetic-report.pdf", FilePath = "synthetic",
            ContentType = "application/pdf", FileSize = 128, DocumentId = Guid.NewGuid(), VersionNo = 1,
            FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = TestReportReviewStatuses.Approved,
            IsRawDataExported = true, CreatedAt = now, UpdatedAt = now
        };
        using var db = factory.CreateDbContext();
        db.AddRange(record, recordCase, task, file, KmIntegrationOutbox.ForCommittedTestReport(file, now));
        db.SaveChanges();
        return file;
    }

    private sealed class MockTokenProvider : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("mock-csit-token");
    }

    private sealed class PostgreSqlFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class EmptyEngineerDirectory : IAssignableEngineerDirectory
    {
        public Task<IReadOnlyList<AssignableEngineerDto>> GetForRecordAsync(Guid recordId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssignableEngineerDto>>([]);

        public Task<string?> ResolveAssignableAccountAsync(Guid recordId, string? account,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

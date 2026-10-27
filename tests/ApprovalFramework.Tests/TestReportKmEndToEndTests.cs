using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class TestReportKmEndToEndTests
{
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

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

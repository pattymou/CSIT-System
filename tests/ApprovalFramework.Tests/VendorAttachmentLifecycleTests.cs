using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class VendorAttachmentLifecycleTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task TaskDVendorDeletionRunsAgainstIsolatedPostgreSql()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_TASK_D_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var info = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", info.Host);
        Assert.Equal(55435, info.Port);
        Assert.StartsWith("csit_task_d_", info.Database, StringComparison.Ordinal);
        var factory = new PostgreSqlFactory(connection);
        await using (var db = factory.CreateDbContext())
            Assert.Contains("20261108070415_AddKmTaskDDocumentDeletionV141",
                await db.Database.GetAppliedMigrationsAsync());
        var file = Seed(factory, "task-d-vendor.pdf", "application/pdf");
        await using (var db = factory.CreateDbContext())
        {
            var outbox = await db.KmIntegrationOutbox.SingleAsync(x =>
                x.SourceEntityId == file.Id);
            var lease = Guid.NewGuid();
            outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            outbox.Sent(lease, "KM-PG-INGEST-1", DateTime.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(VendorFileDeletionStatuses.Pending,
                (await new VendorFileDeletionService(db)
                    .RequestAsync(file.Id, "pg-test"))!.Status);
        }
        var api = new VendorDeletionApi();
        Assert.Equal(1, await DeletionDispatcher(factory, api).ProcessBatchAsync());
        var request = Assert.Single(api.Requests);
        var now = DateTimeOffset.UtcNow;
        var callback = new KmDocumentDeletionV2Callback("2.0", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, api.JobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1), new KmDeletionCleanup(
                "Deleted", "Deleted", "NotPresent", "Deleted", "Deleted", "Deleted",
                "Deleted")), null);
        Assert.Equal(KmDeletionCallbackOutcome.Accepted,
            await new KmDocumentDeletionV2CallbackService(factory).ApplyAsync(callback));
        await using var verify = factory.CreateDbContext();
        Assert.False(await verify.VendorFiles.AnyAsync(x => x.Id == file.Id));
        Assert.Equal(VendorFileDeletionStatuses.Completed,
            (await verify.VendorFileDeletionJobs.SingleAsync(x =>
                x.VendorFileId == file.Id)).Status);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("label.png", "image/png")]
    public async Task VendorAcceptedFormatsUseSameV2Contract(string fileName, string contentType)
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory, fileName, contentType);
        var mock = new VendorMockKmClient();

        Assert.Equal(1, await Worker(factory, mock, OptionsForVendor(true)).ProcessBatchAsync());
        var request = Assert.IsType<KmV2DocumentJobRequest>(mock.LastRequest);
        Assert.Equal("2.0", request.SchemaVersion);
        Assert.Equal(fileName, request.Document.OriginalFileName);
        Assert.Equal(contentType, request.Document.ContentType);
        Assert.Equal(KmV2ApprovalModes.ApprovalBypassed, request.Document.Approval.Mode);
        Assert.Equal(file.VendorId, request.Source.VendorId);
    }

    [Fact]
    public async Task SameNameAndShaDifferentIdsDispatchAsSeparateJobs()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var first = Seed(factory, "same.txt", "text/plain");
        var second = Seed(factory, "same.txt", "text/plain");
        var mock = new VendorMockKmClient();

        Assert.Equal(2, await Worker(factory, mock, OptionsForVendor(true)).ProcessBatchAsync());
        Assert.Equal(2, mock.Requests.Count);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(mock.Requests[0].Document.Sha256, mock.Requests[1].Document.Sha256);
        Assert.NotEqual(mock.Requests[0].Source.EntityId, mock.Requests[1].Source.EntityId);
        Assert.NotEqual(mock.Requests[0].CsitJobId, mock.Requests[1].CsitJobId);
    }

    [Fact]
    public async Task VendorApprovalPolicy_PendsEmployee_BypassesLeaderAndAdmin_AndRejectsReadOnlyLeader()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        await using (var db = factory.CreateDbContext())
        {
            db.MenuItems.Add(new MenuItem
            {
                Id = Guid.NewGuid(), SectionId = Guid.NewGuid(), Code = "vendors",
                Title = "廠商資訊管理", RoutePath = "/vendors", SortOrder = 1,
                IsEnabled = true, RequiresSupervisorApproval = true, ModuleCode = "vendor",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var root = new CountingRoot();
        var policy = new ApprovalPolicyResolver(new ApprovalMenuResolver(factory),
            new ApprovalCommitHandlerRegistry([new NoOpVendorHandler()]), root,
            new MatrixAuthorization());
        var source = new ApprovalSourceIdentity("vendor", "Vendor", Guid.NewGuid().ToString("N"),
            null, null, null, null);

        var employee = await policy.ResolveAsync(source, User("employee", write: true),
            sourceMenuCode: "vendors");
        var leader = await policy.ResolveAsync(source, User("leader", write: true, leader: true),
            sourceMenuCode: "vendors");
        var admin = await policy.ResolveAsync(source, User("admin", write: true, admin: true),
            sourceMenuCode: "vendors");

        Assert.Equal(ApprovalUploadDecisionKind.Pending, employee.Kind);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, leader.Kind);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, admin.Kind);
        Assert.Equal(1, root.CallCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => policy.ResolveAsync(source,
            User("readonly-leader", leader: true), sourceMenuCode: "vendors"));
    }

    [Fact]
    public async Task VendorOutboxWaitsWhileVendorContractDisabled_ThenMockDispatchAndCallbackComplete()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory);
        var mock = new VendorMockKmClient();
        var disabled = OptionsForVendor(false);
        Assert.Equal(0, await Worker(factory, mock, disabled).ProcessBatchAsync());
        Assert.Null(mock.LastRequest);
        await using (var db = factory.CreateDbContext())
            Assert.Equal(KmIntegrationStatus.Pending, (await db.KmIntegrationOutbox.SingleAsync()).Status);

        var enabled = OptionsForVendor(true);
        Assert.Equal(1, await Worker(factory, mock, enabled).ProcessBatchAsync());
        Assert.NotNull(mock.LastRequest);
        Assert.Equal(file.Id, mock.LastRequest!.Document.Id);
        Assert.Equal(KmIntegrationEventTypes.VendorFileArchived, mock.LastRequest.EventType);
        Assert.Equal(KmAnalysisSourceTypes.VendorFile, mock.LastRequest.Source.Type);
        Assert.Equal(mock.LastRequest.CsitJobId,
            (await factory.CreateDbContext().KmIntegrationOutbox.SingleAsync()).Id);

        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        var callback = new KmStatusCallback(
            "vendor-job-" + mock.LastRequest.CsitJobId.ToString("N"), "CSIT", file.Id, 1,
            "Processing", new KmCallbackTimestamps(DateTime.UtcNow, null, null), null);
        Assert.IsType<NoContentResult>(await controller.Status(callback, default));
        callback = callback with
        {
            Status = "Completed",
            Timestamps = new KmCallbackTimestamps(null, DateTime.UtcNow, null)
        };
        Assert.IsType<NoContentResult>(await controller.Status(callback, default));
        await using var check = factory.CreateDbContext();
        var row = await check.KmIntegrationOutbox.SingleAsync();
        Assert.Equal(KmIntegrationStatus.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task VendorCallbackCannotRecreateDeletedOrMismatchedJob()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory);
        var mock = new VendorMockKmClient();
        Assert.Equal(1, await Worker(factory, mock, OptionsForVendor(true)).ProcessBatchAsync());
        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        var mismatch = new KmStatusCallback(
            "vendor-job-" + mock.LastRequest!.CsitJobId.ToString("N"), "CSIT", Guid.NewGuid(), 1,
            "Processing", new KmCallbackTimestamps(DateTime.UtcNow, null, null), null);
        Assert.IsType<ConflictObjectResult>(await controller.Status(mismatch, default));

        await using (var db = factory.CreateDbContext())
        {
            db.KmIntegrationOutbox.Remove(await db.KmIntegrationOutbox.SingleAsync());
            db.VendorFiles.Remove(await db.VendorFiles.SingleAsync());
            await db.SaveChangesAsync();
        }
        var delayed = mismatch with { DocumentId = file.Id };
        Assert.IsType<ConflictObjectResult>(await controller.Status(delayed, default));
        await using var check = factory.CreateDbContext();
        Assert.Empty(check.KmIntegrationOutbox);
        Assert.Empty(check.VendorFiles);
    }

    [Fact]
    public async Task SentVendorDeletionUsesV2HoldCallbackIdempotencyAndLocalCleanup()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory);
        var ingestion = new VendorMockKmClient();
        Assert.Equal(1, await Worker(factory, ingestion, OptionsForVendor(true)).ProcessBatchAsync());

        VendorFileDeletionRequestResult requested;
        await using (var db = factory.CreateDbContext())
        {
            requested = Assert.IsType<VendorFileDeletionRequestResult>(
                await new VendorFileDeletionService(db).RequestAsync(file.Id, "writer"));
            Assert.Equal(VendorFileDeletionStatuses.Pending, requested.Status);
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                new SIT.DepartmentSystem.Web.Services.Implementations.VendorService(
                    db, new ManagedAssetFileStorage(db)).DownloadAsync(file.Id));
        }

        var deletionApi = new VendorDeletionApi();
        var dispatcher = DeletionDispatcher(factory, deletionApi);
        Assert.Equal(1, await dispatcher.ProcessBatchAsync());
        var request = Assert.Single(deletionApi.Requests);
        Assert.Equal("2.0", request.SchemaVersion);
        Assert.Equal(KmDocumentDeletionV2SourceTypes.VendorFile, request.Resource.SourceType);
        Assert.Equal(file.Id, request.Resource.CanonicalIngestionEventId);
        Assert.Equal(file.VendorId, request.Resource.VendorId);
        Assert.Equal("DocumentVersion", request.Resource.Scope);

        var now = DateTimeOffset.UtcNow;
        var callbackId = Guid.NewGuid();
        var completed = new KmDocumentDeletionV2Callback("2.0", callbackId, "CSIT",
            request.DeletionRequestId, deletionApi.JobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1), new KmDeletionCleanup(
                "Deleted", "Deleted", "Deleted", "Deleted", "Deleted", "NotPresent",
                "Deleted")), null);
        var callbacks = new KmDocumentDeletionV2CallbackService(factory);
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(completed));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(completed));
        Assert.Equal(KmDeletionCallbackOutcome.Conflict,
            await callbacks.ApplyAsync(completed with { Status = "Processing", Result = null,
                Timestamps = new KmDeletionTimestamps(now, null, null) }));

        await using var check = factory.CreateDbContext();
        Assert.False(await check.VendorFiles.AnyAsync(x => x.Id == file.Id));
        Assert.False(await check.KmIntegrationOutbox.AnyAsync(x => x.SourceEntityId == file.Id));
        Assert.Equal(VendorFileDeletionStatuses.Completed,
            (await check.VendorFileDeletionJobs.SingleAsync()).Status);
        Assert.Single(await check.KmDocumentDeletionV2Callbacks.ToListAsync());
    }

    [Fact]
    public async Task VendorDeletionFailedCallbackRetriesWithSameRequestAndJobIdentity()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory);
        Assert.Equal(1, await Worker(factory, new VendorMockKmClient(),
            OptionsForVendor(true)).ProcessBatchAsync());
        await using (var db = factory.CreateDbContext())
            await new VendorFileDeletionService(db).RequestAsync(file.Id, "writer");
        var api = new VendorDeletionApi();
        var dispatcher = DeletionDispatcher(factory, api);
        Assert.Equal(1, await dispatcher.ProcessBatchAsync());
        var request = Assert.Single(api.Requests);
        var now = DateTimeOffset.UtcNow;
        var failed = new KmDocumentDeletionV2Callback("2.0", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, api.JobId, request.Resource, "Failed",
            new KmDeletionTimestamps(now, null, now.AddSeconds(1)), null,
            new KmDeletionError("KM_BUSY", "retry", true));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted,
            await new KmDocumentDeletionV2CallbackService(factory).ApplyAsync(failed));
        Assert.Equal(1, await dispatcher.ProcessBatchAsync());
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, x => Assert.Equal(request.DeletionRequestId,
            x.DeletionRequestId));
        await using var check = factory.CreateDbContext();
        Assert.Equal(api.JobId, (await check.VendorFileDeletionJobs.SingleAsync()).KmDeletionJobId);
    }

    private static VendorFile Seed(IDbContextFactory<AppDbContext> factory,
        string originalFileName = "manual.pdf", string contentType = "application/pdf")
    {
        using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(), Name = "Mock Vendor", NormalizedName = "MOCK VENDOR",
            StorageFolderName = "Mock Vendor", Types = [], CreatedBy = "tester",
            UpdatedBy = "tester", CreatedAt = now, UpdatedAt = now
        };
        var id = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 12).Select(x => (byte)x).ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var file = new VendorFile
        {
            Id = id, VendorId = vendor.Id, Vendor = vendor, OriginalFileName = originalFileName,
            StoredFileName = id.ToString("N") + Path.GetExtension(originalFileName),
            FolderPath = Path.GetTempPath(), ContentType = contentType,
            FileSize = bytes.Length, Sha256 = sha256,
            ArchiveStatus = FileArchiveStatuses.Archived, ArchiveAttemptCount = 1,
            ArchiveUpdatedAt = now, UploadedBy = "tester", UploadedAt = now,
            ApprovalOperationId = id, ApprovedBy = "leader", ApprovedAt = now,
            MetadataVerificationStatus = MetadataVerificationStatuses.NotApplicable
        };
        db.Vendors.Add(vendor);
        db.VendorFiles.Add(file);
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, "vendors", "vendor",
            ApprovalAuditActions.ApprovalBypassed, "leader", now, null, null));
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVendorFile(file, now));
        File.WriteAllBytes(Path.Combine(file.FolderPath, file.StoredFileName), bytes);
        db.SaveChanges();
        return file;
    }

    private static KmIntegrationOptions OptionsForVendor(bool enabled) => new()
    {
        Enabled = true, VendorEnabled = enabled,
        BaseUrl = "https://km.invalid", ServiceBaseUrl = "https://csit.invalid/",
        TimeoutSeconds = 2, LeaseSeconds = 30, MaxRetryCount = 3,
        RetryScheduleSeconds = [1], PollSeconds = 1
    };

    private static KmSyncWorker Worker(IDbContextFactory<AppDbContext> factory, IKmIntegrationClient client,
        KmIntegrationOptions options) => new(factory, client, Options.Create(options),
        NullLogger<KmSyncWorker>.Instance);

    private static KmDeletionDispatchService DeletionDispatcher(IDbContextFactory<AppDbContext> factory,
        IKmDocumentDeletionV2Client client) => new(factory, new NoopDeletionClient(),
        Options.Create(new KmDeletionOptions
        {
            Enabled = true, VendorEnabled = true, Scope = "csit.document.delete"
        }), Options.Create(new KmIntegrationOptions
        {
            BaseUrl = "https://km.invalid/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 4
        }), client);

    private sealed class VendorMockKmClient : IKmIntegrationClient
    {
        public KmV2DocumentJobRequest? LastRequest { get; private set; }
        public List<KmV2DocumentJobRequest> Requests { get; } = [];
        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(KmDeliveryResult.Failure(false, "UNEXPECTED", "Unexpected request."));

        public Task<KmDeliveryResult> SubmitDocumentJobAsync(KmV2DocumentJobRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(KmDeliveryResult.Success(
                "vendor-job-" + request.CsitJobId.ToString("N")));
        }
    }

    private sealed class VendorDeletionApi : IKmDocumentDeletionV2Client
    {
        public string JobId { get; } = "KM-VENDOR-DELETE-1";
        public List<KmDocumentDeletionV2Request> Requests { get; } = [];
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDocumentDeletionV2Request request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(KmDeletionDeliveryResult.Success(JobId));
        }
    }

    private sealed class NoopDeletionClient : IKmDeletionClient
    {
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
                KmDeletionDeliveryResult.Failure(false, "UNEXPECTED_V1"));
    }

    private static ClaimsPrincipal User(string account, bool write = false,
        bool leader = false, bool admin = false)
    {
        var claims = new List<Claim> { new("account", account) };
        if (write) claims.Add(new("test_write", "true"));
        if (leader) claims.Add(new("test_leader", "true"));
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class MatrixAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            string policyName)
        {
            var allowed = policyName == SystemAuthorization.Policies.BusinessWrite
                ? user.IsInRole("Admin") || user.HasClaim("test_write", "true")
                : policyName == SystemAuthorization.Policies.Administration &&
                  (user.IsInRole("Admin") || user.HasClaim("test_leader", "true"));
            return Task.FromResult(allowed
                ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }

    private sealed class CountingRoot : IApprovalStagingRootResolver
    {
        public int CallCount { get; private set; }
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Path.GetTempPath());
        }
    }

    private sealed class NoOpVendorHandler : IApprovalCommitHandler
    {
        public string ModuleCode => VendorApprovalSourceResolver.ModuleCode;
        public Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ApprovalCommitResult(submission.Id.ToString("N")));
    }

    private sealed class TestDbFactory(string database) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PostgreSqlFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

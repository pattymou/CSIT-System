using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class DocumentLibraryKmIntegrationTests
{
    [Fact]
    public async Task DocumentLibraryFile_FlowsThrough202DownloadCallbacksMetadataAndNasArchive()
    {
        var roots = TemporaryRoots.Create();
        try
        {
            var factory = new InMemoryFactory();
            var folderId = await SeedFoldersAsync(factory);
            var uploaded = await UploadAsync(factory, roots, folderId, "WiFi_Throughput_測試.pdf");
            await using var mockKm = await MockKmIngestionServer.StartAsync();
            var options = IntegrationOptions();
            var worker = Worker(factory, mockKm, options);

            Assert.Equal(1, await worker.ProcessBatchAsync());
            Assert.Equal(1, mockKm.DocumentRequestCount);
            var request = Assert.IsType<DocumentLibraryIngestionRequest>(mockKm.LastDocumentLibraryRequest);
            Assert.Equal("1.0", request.SchemaVersion);
            Assert.Equal("DocumentLibraryFileUploaded", request.EventType);
            Assert.Equal(uploaded.Id, request.DocumentFileId);
            Assert.Equal(uploaded.CsitJobId, request.CsitJobId);
            Assert.Equal(["L1", "L2", "L3"],
                new[] { request.Classification.L1.Name, request.Classification.L2.Name,
                    request.Classification.L3.Name });
            Assert.DoesNotContain("documentVersion", JsonSerializer.Serialize(request),
                StringComparison.OrdinalIgnoreCase);

            await using var csit = await CsitKmStatusCallbackTestServer.StartAsync(
                factory, roots.Staging, roots.Library);
            using (var unauthorized = await csit.DownloadDocumentLibraryAsync(uploaded.Id, authorize: false))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var download = await csit.DownloadDocumentLibraryAsync(uploaded.Id))
            {
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal("document-library-content", await download.Content.ReadAsStringAsync());
            }

            var startedAt = DateTimeOffset.UtcNow;
            var processing = Callback(uploaded, mockKm.JobId, "Processing", startedAt);
            using (var response = await csit.SendDocumentLibraryAsync(processing))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            var completed = Callback(uploaded, mockKm.JobId, "Completed", startedAt);
            using (var response = await csit.SendDocumentLibraryAsync(completed))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using (var duplicate = await csit.SendDocumentLibraryAsync(completed))
                Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
            using (var conflictingDuplicate = await csit.SendDocumentLibraryAsync(completed with
            {
                Result = completed.Result! with
                {
                    Document = completed.Result.Document with { Title = "Conflicting title" }
                }
            }))
                Assert.Equal(HttpStatusCode.Conflict, conflictingDuplicate.StatusCode);
            using (var wrongJob = await csit.SendDocumentLibraryAsync(
                completed with { CallbackId = Guid.NewGuid(), KmJobId = "wrong-job" }))
                Assert.Equal(HttpStatusCode.Conflict, wrongJob.StatusCode);
            using (var late = await csit.SendDocumentLibraryAsync(
                processing with { CallbackId = Guid.NewGuid() }))
                Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var job = await db.DocumentProcessingJobs.SingleAsync(x => x.Id == uploaded.CsitJobId);
                Assert.Equal(DocumentProcessingStatuses.Completed, job.Status);
                Assert.Equal(DocumentArchiveStatuses.Pending, job.ArchiveStatus);
                Assert.Equal(2, await db.DocumentProcessingCallbacks.CountAsync());
                using var metadata = JsonDocument.Parse(job.MetadataJson!);
                Assert.Equal("1.0", metadata.RootElement.GetProperty("metadataSchemaVersion").GetString());
                Assert.True(metadata.RootElement.TryGetProperty("document", out _));
                Assert.True(metadata.RootElement.TryGetProperty("technical", out _));
                Assert.True(metadata.RootElement.TryGetProperty("extraction", out _));
                Assert.True(metadata.RootElement.TryGetProperty("indexing", out _));
                Assert.True(metadata.RootElement.TryGetProperty("warnings", out _));
            }

            // Simulate a crash after the atomic NAS move but before the database transaction.
            // The persistent archive job must verify and adopt the existing target on restart.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var file = await db.DocumentLibraryFiles.AsNoTracking().SingleAsync(x => x.Id == uploaded.Id);
                var source = Path.Combine(roots.Staging, file.RelativePath);
                var target = Path.Combine(roots.Library, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            var archive = ArchiveWorker(factory, roots, options);
            Assert.Equal(1, await archive.ProcessBatchAsync());
            Assert.Equal(0, await archive.ProcessBatchAsync());
            await using (var db = await factory.CreateDbContextAsync())
            {
                var file = await db.DocumentLibraryFiles.SingleAsync(x => x.Id == uploaded.Id);
                var job = await db.DocumentProcessingJobs.SingleAsync(x => x.Id == uploaded.CsitJobId);
                Assert.Equal(DocumentStorageAreas.Library, file.StorageArea);
                Assert.Equal(DocumentArchiveStatuses.Completed, job.ArchiveStatus);
                Assert.Equal(file.Sha256, job.ArchiveSha256);
                Assert.NotNull(job.ArchivedAt);
                Assert.False(File.Exists(Path.Combine(roots.Staging, file.RelativePath)));
                Assert.True(File.Exists(Path.Combine(roots.Library, file.RelativePath)));
            }
            using (var archivedDownload = await csit.DownloadDocumentLibraryAsync(uploaded.Id))
                Assert.Equal(HttpStatusCode.OK, archivedDownload.StatusCode);
            var displayed = Assert.Single(await Service(factory, roots).GetFilesAsync(folderId));
            Assert.Equal("已完成／NAS 已歸檔", displayed.ProcessingStatusDisplay);
            Assert.True(displayed.MetadataAvailable);
        }
        finally { roots.Dispose(); }
    }

    [Fact]
    public async Task DocumentLibraryWorker_Retries503TimeoutAndExpiredLeaseWithoutDuplicateJobs()
    {
        var roots = TemporaryRoots.Create();
        try
        {
            var factory = new InMemoryFactory();
            var folderId = await SeedFoldersAsync(factory);
            await using var mockKm = await MockKmIngestionServer.StartAsync();
            var options = IntegrationOptions();

            var retry = await UploadAsync(factory, roots, folderId, "retry.pdf");
            mockKm.FailNext(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            await AssertPendingRetryAsync(factory, retry.CsitJobId!.Value, "HTTP_503");
            await Task.Delay(1100);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());

            var timeout = await UploadAsync(factory, roots, folderId, "timeout.docx");
            var timeoutOptions = IntegrationOptions(timeoutSeconds: 1, leaseSeconds: 7);
            mockKm.DelayNext(TimeSpan.FromSeconds(2));
            Assert.Equal(1, await Worker(factory, mockKm, timeoutOptions).ProcessBatchAsync());
            await AssertPendingRetryAsync(factory, timeout.CsitJobId!.Value, "TIMEOUT");
            await Task.Delay(1100);
            Assert.Equal(1, await Worker(factory, mockKm, timeoutOptions).ProcessBatchAsync());

            var lease = await UploadAsync(factory, roots, folderId, "lease.xlsx");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.KmIntegrationOutbox.SingleAsync(x => x.EventId == lease.CsitJobId);
                row.Claim(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-2), TimeSpan.FromSeconds(1));
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());

            var exhausted = await UploadAsync(factory, roots, folderId, "exhausted.pdf");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.KmIntegrationOutbox.SingleAsync(x => x.EventId == exhausted.CsitJobId);
                var expiredAt = DateTime.UtcNow.AddMinutes(-5);
                for (var i = 0; i < options.MaxRetryCount; i++)
                {
                    var failedLease = Guid.NewGuid();
                    row.Claim(failedLease, expiredAt, TimeSpan.FromSeconds(1));
                    row.DeliveryFailed(failedLease, "HTTP_503", "Synthetic exhaustion.", expiredAt, expiredAt);
                }
                row.Claim(Guid.NewGuid(), expiredAt, TimeSpan.FromSeconds(1));
                await db.SaveChangesAsync();
            }
            Assert.Equal(0, await Worker(factory, mockKm, options).ProcessBatchAsync());

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(4, await verify.KmIntegrationOutbox.CountAsync(x =>
                x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded));
            Assert.All(await verify.KmIntegrationOutbox.Where(x =>
                    x.EventType == KmIntegrationEventTypes.DocumentLibraryFileUploaded &&
                    x.EventId != exhausted.CsitJobId).ToListAsync(),
                x => Assert.Equal(KmIntegrationStatus.Sent, x.Status));
            var exhaustedOutbox = await verify.KmIntegrationOutbox.SingleAsync(x =>
                x.EventId == exhausted.CsitJobId);
            var exhaustedJob = await verify.DocumentProcessingJobs.SingleAsync(x =>
                x.Id == exhausted.CsitJobId);
            Assert.Equal(KmIntegrationStatus.Failed, exhaustedOutbox.Status);
            Assert.Equal("RETRY_EXHAUSTED", exhaustedOutbox.LastErrorCode);
            Assert.Null(exhaustedOutbox.NextRetryAt);
            Assert.Equal(DocumentProcessingStatuses.Failed, exhaustedJob.Status);
            Assert.Equal("RETRY_EXHAUSTED", exhaustedJob.KmErrorCode);
            Assert.Equal(5, mockKm.DocumentRequestCount);
        }
        finally { roots.Dispose(); }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task MigrationAndDocumentLibraryFlow_RunAgainstIsolatedPostgreSql()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_DOCUMENTLIBRARY_KM_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", info.Host);
        Assert.Equal(55432, info.Port);
        Assert.StartsWith("csit_documentlibrary_km_e2e_", info.Database, StringComparison.Ordinal);
        Assert.Equal("postgres", info.Username);
        Assert.True(string.IsNullOrEmpty(info.Password));

        var factory = new PostgreSqlFactory(connection);
        await VerifyMigrationUpAndLegacyCompatibilityAsync(factory, connection);
        var roots = TemporaryRoots.Create();
        try
        {
            var folderId = await SeedFoldersAsync(factory);
            var files = new[]
            {
                await UploadAsync(factory, roots, folderId, "one.pdf"),
                await UploadAsync(factory, roots, folderId, "two.docx"),
                await UploadAsync(factory, roots, folderId, "three.xlsx")
            };
            await using var mockKm = await MockKmIngestionServer.StartAsync();
            var options = IntegrationOptions();
            var batches = await Task.WhenAll(
                Worker(factory, mockKm, options).ProcessBatchAsync(),
                Worker(factory, mockKm, options).ProcessBatchAsync());
            Assert.Equal(3, batches.Sum());
            Assert.Equal(3, mockKm.DocumentRequestCount);

            await using var csit = await CsitKmStatusCallbackTestServer.StartAsync(
                factory, roots.Staging, roots.Library);
            foreach (var file in files)
            {
                using var download = await csit.DownloadDocumentLibraryAsync(file.Id);
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            }
            using (var unauthorized = await csit.DownloadDocumentLibraryAsync(files[0].Id, false))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            var rows = await ReadDocumentOutboxAsync(factory, files);
            for (var i = 0; i < 2; i++)
            {
                var start = DateTimeOffset.UtcNow;
                using var processing = await csit.SendDocumentLibraryAsync(
                    Callback(files[i], rows[i].KmJobId!, "Processing", start));
                Assert.Equal(HttpStatusCode.NoContent, processing.StatusCode);
                var completed = Callback(files[i], rows[i].KmJobId!, "Completed", start);
                var concurrentDuplicate = await Task.WhenAll(
                    csit.SendDocumentLibraryAsync(completed),
                    csit.SendDocumentLibraryAsync(completed));
                Assert.All(concurrentDuplicate,
                    response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
                foreach (var response in concurrentDuplicate) response.Dispose();
            }
            using (var failed = await csit.SendDocumentLibraryAsync(
                Callback(files[2], rows[2].KmJobId!, "Failed", DateTimeOffset.UtcNow)))
                Assert.Equal(HttpStatusCode.NoContent, failed.StatusCode);

            Assert.Equal(2, await ArchiveWorker(factory, roots, options).ProcessBatchAsync());
            await using (var db = await factory.CreateDbContextAsync())
            {
                var jobs = await db.DocumentProcessingJobs
                    .Where(x => files.Select(f => f.CsitJobId).Contains(x.Id)).ToListAsync();
                Assert.Equal(2, jobs.Count(x => x.ArchiveStatus == DocumentArchiveStatuses.Completed));
                Assert.Single(jobs, x => x.Status == DocumentProcessingStatuses.Failed &&
                    x.KmErrorCode == "PARSE_FAILED" && x.KmErrorRetryable == false);
                Assert.Equal(5, await db.DocumentProcessingCallbacks.CountAsync(x =>
                    files.Select(f => f.CsitJobId).Contains(x.DocumentProcessingJobId)));
            }

            var retry = await UploadAsync(factory, roots, folderId, "pg-retry.pdf");
            mockKm.FailNext(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            await AssertPendingRetryAsync(factory, retry.CsitJobId!.Value, "HTTP_503");
            await Task.Delay(1100);
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());

            var lease = await UploadAsync(factory, roots, folderId, "pg-lease.pdf");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.KmIntegrationOutbox.SingleAsync(x => x.EventId == lease.CsitJobId);
                row.Claim(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(1));
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, await Worker(factory, mockKm, options).ProcessBatchAsync());
            Assert.Equal(0, await Worker(factory, mockKm, options).ProcessBatchAsync());
        }
        finally { roots.Dispose(); }
    }

    private static async Task VerifyMigrationUpAndLegacyCompatibilityAsync(
        PostgreSqlFactory factory,
        string connection)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var baseline = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20261026061746_AddVerificationTwoLevelManagement", baseline);
            await db.GetService<IMigrator>().MigrateAsync("20261026135657_AddVerificationCaseLocalCleanup");
        }

        var l1 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        var l3 = Guid.NewGuid();
        var file = Guid.NewGuid();
        var job = Guid.NewGuid();
        await using (var dbConnection = new NpgsqlConnection(connection))
        {
            await dbConnection.OpenAsync();
            await using var command = dbConnection.CreateCommand();
            command.CommandText = """
                INSERT INTO document_folders (id,parent_id,level,name,description,sort_order,created_at,updated_at)
                VALUES (@l1,NULL,1,'Legacy L1',NULL,1,NOW(),NOW()),
                       (@l2,@l1,2,'Legacy L2',NULL,1,NOW(),NOW()),
                       (@l3,@l2,3,'Legacy L3',NULL,1,NOW(),NOW());
                INSERT INTO document_files
                    (id,folder_id,original_file_name,standard_file_name,content_type,uploaded_by,
                     uploaded_at,file_size,sha256,relative_path,storage_area)
                VALUES (@file,@l3,'legacy.pdf','DOC-legacy.pdf','application/pdf','legacy',NOW(),1,
                        repeat('a',64),'legacy/DOC-legacy.pdf','Library');
                INSERT INTO document_processing_jobs
                    (id,document_file_id,status,attempt_count,created_at,updated_at,last_attempt_at,last_error)
                VALUES (@job,@file,'Completed',0,NOW(),NOW(),NULL,NULL);
                """;
            command.Parameters.AddWithValue("l1", l1);
            command.Parameters.AddWithValue("l2", l2);
            command.Parameters.AddWithValue("l3", l3);
            command.Parameters.AddWithValue("file", file);
            command.Parameters.AddWithValue("job", job);
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            var legacy = await db.DocumentProcessingJobs.AsNoTracking().SingleAsync(x => x.Id == job);
            Assert.Equal(DocumentProcessingStatuses.Completed, legacy.Status);
            Assert.Equal(DocumentArchiveStatuses.NotStarted, legacy.ArchiveStatus);
            Assert.Null(legacy.MetadataJson);
            Assert.Contains("20261027061524_AddDocumentLibraryKmIntegrationV1",
                await db.Database.GetAppliedMigrationsAsync());
        }

        await using (var dbConnection = new NpgsqlConnection(connection))
        {
            await dbConnection.OpenAsync();
            await using var invalid = dbConnection.CreateCommand();
            invalid.CommandText = """
                UPDATE document_processing_jobs
                SET archive_status='Pending', km_completed_at=NOW()
                WHERE id=@job
                """;
            invalid.Parameters.AddWithValue("job", job);
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    private static async Task<Guid> SeedFoldersAsync(IDbContextFactory<AppDbContext> factory)
    {
        var now = DateTime.UtcNow;
        var l1 = new DocumentFolder { Id = Guid.NewGuid(), Level = 1, Name = "L1",
            SortOrder = 1, CreatedAt = now, UpdatedAt = now };
        var l2 = new DocumentFolder { Id = Guid.NewGuid(), ParentId = l1.Id, Level = 2, Name = "L2",
            SortOrder = 1, CreatedAt = now, UpdatedAt = now };
        var l3 = new DocumentFolder { Id = Guid.NewGuid(), ParentId = l2.Id, Level = 3, Name = "L3",
            SortOrder = 1, CreatedAt = now, UpdatedAt = now };
        await using var db = await factory.CreateDbContextAsync();
        db.AddRange(l1, l2, l3);
        await db.SaveChangesAsync();
        return l3.Id;
    }

    private static async Task<SIT.DepartmentSystem.Web.Models.Api.DocumentLibraryFileDto> UploadAsync(
        IDbContextFactory<AppDbContext> factory,
        TemporaryRoots roots,
        Guid folderId,
        string name)
    {
        var service = Service(factory, roots);
        var bytes = Encoding.UTF8.GetBytes("document-library-content");
        await using var stream = new MemoryStream(bytes);
        var upload = new FormFile(stream, 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = ContentType(name) };
        return await service.UploadAsync(folderId, upload, "km-e2e");
    }

    private static DocumentLibraryService Service(
        IDbContextFactory<AppDbContext> factory,
        TemporaryRoots roots) => new(factory,
        Options.Create(new DocumentStorageOptions { RootPath = roots.Library }),
        Options.Create(new DocumentStagingOptions { RootPath = roots.Staging }),
        new AllowWrite());

    private static KmSyncWorker Worker(
        IDbContextFactory<AppDbContext> factory,
        MockKmIngestionServer mock,
        KmIntegrationOptions options) => new(factory,
        new KmIntegrationClient(mock.Client, Options.Create(options), new MockTokenProvider()),
        Options.Create(options), NullLogger<KmSyncWorker>.Instance);

    private static DocumentLibraryArchiveWorker ArchiveWorker(
        IDbContextFactory<AppDbContext> factory,
        TemporaryRoots roots,
        KmIntegrationOptions options) => new(factory, Options.Create(options),
        Options.Create(new DocumentStorageOptions { RootPath = roots.Library }),
        Options.Create(new DocumentStagingOptions { RootPath = roots.Staging }),
        NullLogger<DocumentLibraryArchiveWorker>.Instance);

    private static KmIntegrationOptions IntegrationOptions(int timeoutSeconds = 2, int leaseSeconds = 8) => new()
    {
        Enabled = true,
        BaseUrl = "https://mock-km.test/",
        ServiceBaseUrl = "https://mock-csit.test/",
        TimeoutSeconds = timeoutSeconds,
        LeaseSeconds = leaseSeconds,
        MaxRetryCount = 3,
        RetryScheduleSeconds = [1],
        PollSeconds = 1,
        ArchivePollSeconds = 1,
        ArchiveLeaseSeconds = 8,
        ArchiveMaxRetryCount = 3,
        ArchiveRetryScheduleSeconds = [1]
    };

    private static DocumentLibraryStatusCallback Callback(
        SIT.DepartmentSystem.Web.Models.Api.DocumentLibraryFileDto file,
        string kmJobId,
        string status,
        DateTimeOffset startedAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentLibraryStatusCallback("1.0", Guid.NewGuid(), "CSIT", file.Id,
            file.CsitJobId!.Value, kmJobId, status,
            new DocumentLibraryCallbackTimestamps(startedAt,
                status == "Completed" ? now : null,
                status == "Failed" ? now : null),
            status == "Completed" ? CompletedResult(now) : null,
            status == "Failed" ? new DocumentLibraryCallbackError(
                "PARSE_FAILED", "Synthetic parse failure.", false) : null);
    }

    private static DocumentLibraryCompletedResult CompletedResult(DateTimeOffset indexedAt) => new(
        "1.0",
        new DocumentLibraryDocumentMetadata("WiFi Throughput", "TestReport", "Synthetic summary",
            "zh-TW", "2026-10-27", [], null, "Rev 1.0", ["WiFi"], ["測試"]),
        new DocumentLibraryTechnicalMetadata([], [], [], ["WiFi 6"], [], [], [], [],
            ["DL Throughput"], [new("DL Throughput", "853.2", "Mbps", "5 GHz")], [], []),
        new DocumentLibraryExtractionMetadata("pdf", "Text", 1, [], 24, false),
        new DocumentLibraryIndexingMetadata("Completed", "KM-INDEX-SYNTHETIC", 1, indexedAt),
        []);

    private static async Task<KmIntegrationOutbox[]> ReadDocumentOutboxAsync(
        IDbContextFactory<AppDbContext> factory,
        IEnumerable<SIT.DepartmentSystem.Web.Models.Api.DocumentLibraryFileDto> files)
    {
        var ids = files.Select(x => x.CsitJobId!.Value).ToArray();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.KmIntegrationOutbox.AsNoTracking().Where(x => ids.Contains(x.EventId)).ToListAsync();
        return ids.Select(id => rows.Single(x => x.EventId == id)).ToArray();
    }

    private static async Task AssertPendingRetryAsync(
        IDbContextFactory<AppDbContext> factory,
        Guid jobId,
        string code)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.KmIntegrationOutbox.AsNoTracking().SingleAsync(x => x.EventId == jobId);
        var job = await db.DocumentProcessingJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
        Assert.Equal(KmIntegrationStatus.Failed, row.Status);
        Assert.Equal(code, row.LastErrorCode);
        Assert.NotNull(row.NextRetryAt);
        Assert.Equal(DocumentProcessingStatuses.Pending, job.Status);
        Assert.NotNull(job.LastError);
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    private sealed class MockTokenProvider : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("mock-csit-token");
    }

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InMemoryFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PostgreSqlFactory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options;
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TemporaryRoots : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "csit-document-km-" + Guid.NewGuid().ToString("N"));
        public string Staging => Path.Combine(Root, "staging");
        public string Library => Path.Combine(Root, "library");
        public static TemporaryRoots Create()
        {
            var value = new TemporaryRoots();
            Directory.CreateDirectory(value.Staging);
            Directory.CreateDirectory(value.Library);
            return value;
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

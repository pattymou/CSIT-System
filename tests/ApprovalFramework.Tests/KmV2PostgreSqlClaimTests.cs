using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmV2PostgreSqlClaimTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlManualReanalysis_CreatesDistinctJobs_AndDeletionFindsCanonicalAttempt()
    {
        if (!KmMockE2EPostgreSql.TryGetConnection(
                "CSIT_KM_V2_POSTGRES_CONNECTION", ["csit_km_v2_"], [55432], out var connection)) return;
        var factory = new Factory(connection);
        var root = Path.Combine(Path.GetTempPath(), "csit-km-reanalysis-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var schema = await factory.CreateDbContextAsync())
                Assert.True(await schema.Database.EnsureCreatedAsync());
            await SeedVendorAsync(factory, root);
            await SeedEquipmentImageAsync(factory, root);
            var client = new CaptureClient();
            var options = Options.Create(new KmIntegrationOptions
            {
                Enabled = true, VendorEnabled = true, EquipmentEnabled = true,
                BaseUrl = "https://km.invalid/", ServiceBaseUrl = "https://csit.invalid/",
                TimeoutSeconds = 2, LeaseSeconds = 30, MaxRetryCount = 3,
                RetryScheduleSeconds = [1]
            });
            var worker = new KmSyncWorker(factory, client, options, NullLogger<KmSyncWorker>.Instance);
            Assert.Equal(2, await worker.ProcessBatchAsync());
            var originals = await ReadOutboxesAsync(factory);
            var callback = new KmAnalysisCallbackService(factory);
            foreach (var original in originals)
                await callback.ApplyDocumentStatusAsync(Failed(original));

            var service = new KmManualReanalysisService(factory, options);
            var attempts = new List<Guid>();
            foreach (var original in originals)
            {
                var requestId = Guid.NewGuid();
                Assert.Equal(requestId, await service.CreateAsync(original.Id, requestId, "admin", default));
                Assert.Equal(requestId, await service.CreateAsync(original.Id, requestId, "admin", default));
                await Assert.ThrowsAsync<KmReanalysisRejectedException>(() =>
                    service.CreateAsync(original.Id, Guid.NewGuid(), "admin", default));
                attempts.Add(requestId);
            }
            await using (var check = await factory.CreateDbContextAsync())
            {
                Assert.Equal(4, await check.KmIntegrationOutbox.CountAsync());
                Assert.Equal(2, await check.KmIntegrationManualRetryAudits.CountAsync(x =>
                    x.Action == "ManualReanalysis"));
                Assert.All(await check.KmIntegrationOutbox.Where(x => attempts.Contains(x.Id)).ToListAsync(),
                    x => { Assert.Equal(0, x.RetryCount); Assert.Equal(KmIntegrationStatus.Pending, x.Status); });
            }
            Assert.Equal(2, await worker.ProcessBatchAsync());
            foreach (var attempt in (await ReadOutboxesAsync(factory)).Where(x => attempts.Contains(x.Id)))
                await callback.ApplyDocumentStatusAsync(Failed(attempt));
            var equipmentOriginal = originals.Single(x =>
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile);
            var equipmentFailedAttempt = (await ReadOutboxesAsync(factory)).Single(x =>
                attempts.Contains(x.Id) &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile);
            await File.WriteAllBytesAsync(Path.Combine(root, "front.png"), [12, 11, 10, 9]);
            await Assert.ThrowsAsync<KmReanalysisRejectedException>(() =>
                service.CreateAsync(equipmentFailedAttempt.Id, Guid.NewGuid(), "admin", default));
            await File.WriteAllBytesAsync(Path.Combine(root, "front.png"), [9, 10, 11, 12]);
            var competingIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var competing = await Task.WhenAll(competingIds.Select(async id =>
            {
                try { return await service.CreateAsync(equipmentFailedAttempt.Id, id, "admin", default) == id; }
                catch (KmReanalysisRejectedException) { return false; }
            }));
            Assert.Single(competing, x => x);
            var equipmentSecondAttempt = competingIds[Array.IndexOf(competing, true)];
            Assert.Equal(1, await worker.ProcessBatchAsync());
            var completedAttempt = (await ReadOutboxesAsync(factory)).Single(x => x.Id == equipmentSecondAttempt);
            await callback.ApplyDocumentStatusAsync(Completed(completedAttempt));
            await using (var check = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await check.KmIntegrationManualRetryAudits.CountAsync(x =>
                    x.DocumentId == equipmentOriginal.DocumentId && x.Action == "ManualReanalysis"));
                Assert.Equal(KmIntegrationStatus.Completed,
                    (await check.KmIntegrationOutbox.SingleAsync(x => x.Id == equipmentSecondAttempt)).Status);
                Assert.Single(await check.KmDocumentAnalysisResults.Where(x =>
                    x.OutboxId == equipmentSecondAttempt).ToListAsync());
            }
            await using (var check = await factory.CreateDbContextAsync())
            {
                var vendor = originals.Single(x => x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile);
                var deletion = await new VendorFileDeletionService(check).RequestAsync(vendor.SourceEntityId, "admin");
                Assert.NotNull(deletion);
                var job = await check.VendorFileDeletionJobs.SingleAsync();
                Assert.Equal(attempts[(originals.ToList().IndexOf(vendor))], job.CanonicalIngestionEventId);
                Assert.Equal("DocumentVersion", job.ToRequest().Resource.Scope);
                var equipmentDeletion = await new EquipmentFileDeletionService(check)
                    .RequestAsync(equipmentOriginal.SourceEntityId, "admin");
                Assert.NotNull(equipmentDeletion);
                var equipmentJob = await check.EquipmentFileDeletionJobs.SingleAsync();
                Assert.Equal(equipmentSecondAttempt, equipmentJob.CanonicalIngestionEventId);
                Assert.Equal("DocumentVersion", equipmentJob.ToRequest().Resource.Scope);
                Assert.Equal(5, await check.KmIntegrationOutbox.CountAsync());
                var vendorFailedAttempt = (await ReadOutboxesAsync(factory)).Single(x =>
                    attempts.Contains(x.Id) &&
                    x.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile);
                await Assert.ThrowsAsync<KmReanalysisRejectedException>(() =>
                    service.CreateAsync(vendorFailedAttempt.Id, Guid.NewGuid(), "admin", default));
            }
            Assert.All((await ReadOutboxesAsync(factory)).Where(x => originals.Any(o => o.Id == x.Id)),
                x => Assert.Equal(KmIntegrationStatus.Failed, x.Status));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<List<KmIntegrationOutbox>> ReadOutboxesAsync(Factory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.KmIntegrationOutbox.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
    }

    private static KmV2DocumentJobStatusCallback Failed(KmIntegrationOutbox outbox)
    {
        var now = DateTimeOffset.UtcNow;
        return new("2.0", Guid.NewGuid(), outbox.EventId, outbox.Id, outbox.KmJobId!,
            "CSIT", outbox.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile
                ? KmAnalysisSourceTypes.VendorFile : KmAnalysisSourceTypes.EquipmentAttachment,
            outbox.SourceEntityId, outbox.DocumentId, outbox.Version,
            outbox.SourceEntityType == KmIntegrationSourceEntityTypes.VendorFile
                ? Convert.ToHexString(SHA256.HashData([5, 6, 7, 8])).ToLowerInvariant()
                : Convert.ToHexString(SHA256.HashData([9, 10, 11, 12])).ToLowerInvariant(),
            "Failed", new(now, null, now), null,
            new("TRANSIENT_FAILURE", "Temporary analysis failure.", true));
    }

    private static KmV2DocumentJobStatusCallback Completed(KmIntegrationOutbox outbox)
    {
        const string json = """
            {"metadataSchemaVersion":"2.0","document":{"title":"Equipment analysis","documentType":"Other","summary":null,"language":null,"documentDate":null,"authors":[],"department":null,"documentVersionLabel":null,"keywords":[],"tags":[]},"technical":{"productNames":[],"modelNumbers":[],"vendorNames":[],"technologies":[],"standards":[],"softwareVersions":[],"hardwareVersions":[],"projectNames":[],"testItems":[],"measurements":[],"issueKeys":[],"errorCodes":[]},"extraction":{"sourceFormat":"txt","method":"Text","pageCount":null,"sheetNames":[],"extractedCharacterCount":1,"ocrUsed":false},"indexing":{"status":"Completed","indexId":"idx-test","chunkCount":1,"indexedAt":"2026-11-05T01:08:24Z"},"warnings":[],"sourceSpecific":{}}
            """;
        var now = DateTimeOffset.UtcNow;
        return new("2.0", Guid.NewGuid(), outbox.EventId, outbox.Id, outbox.KmJobId!,
            "CSIT", KmAnalysisSourceTypes.EquipmentAttachment, outbox.SourceEntityId,
            outbox.DocumentId, outbox.Version,
            Convert.ToHexString(SHA256.HashData([9, 10, 11, 12])).ToLowerInvariant(),
            "Completed", new(now.AddSeconds(-1), now, null),
            JsonDocument.Parse(json).RootElement.Clone(), null);
    }
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlClaimDispatchesAllV2SourcesIncludingEquipmentImage()
    {
        if (!KmMockE2EPostgreSql.TryGetConnection(
                "CSIT_KM_V2_POSTGRES_CONNECTION",
                ["csit_km_v2_"], [55432], out var connection)) return;

        var factory = new Factory(connection);
        var root = Path.Combine(Path.GetTempPath(), "csit-km-v2-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var schema = await factory.CreateDbContextAsync())
                Assert.True(await schema.Database.EnsureCreatedAsync());
            await SeedVerificationAsync(factory, root);
            await SeedVendorAsync(factory, root);
            await SeedEquipmentImageAsync(factory, root);
            var client = new CaptureClient();
            var options = Options.Create(new KmIntegrationOptions
            {
                Enabled = true,
                VendorEnabled = true,
                EquipmentEnabled = true,
                BaseUrl = "https://km.invalid/",
                ServiceBaseUrl = "https://csit.invalid/",
                TimeoutSeconds = 2,
                LeaseSeconds = 30,
                MaxRetryCount = 3,
                RetryScheduleSeconds = [1],
                PollSeconds = 1
            });
            var worker = new KmSyncWorker(factory, client, options,
                NullLogger<KmSyncWorker>.Instance,
                Options.Create(new DocumentStorageOptions { RootPath = root }));

            Assert.Equal(3, await worker.ProcessBatchAsync());
            Assert.Equal(3, client.Requests.Count);
            Assert.Contains(client.Requests, x =>
                x.Source.Type == KmAnalysisSourceTypes.VerificationApplicationAttachment);
            Assert.Contains(client.Requests, x => x.Source.Type == KmAnalysisSourceTypes.VendorFile);
            Assert.Contains(client.Requests, x =>
                x.Source.Type == KmAnalysisSourceTypes.EquipmentAttachment &&
                x.EventType == KmIntegrationEventTypes.EquipmentAttachmentArchived);
            await using var check = await factory.CreateDbContextAsync();
            Assert.All(await check.KmIntegrationOutbox.AsNoTracking().ToListAsync(),
                x => Assert.Equal(KmIntegrationStatus.Sent, x.Status));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedVerificationAsync(Factory factory, string root)
    {
        var now = DateTime.UtcNow;
        var department = Guid.NewGuid();
        var team = Guid.NewGuid();
        var app = VerificationApplication.CreateDraft(Guid.NewGuid(), "VA-PG-V2", department, team,
            "owner", "Owner", "owner@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "PG V2", ProductModel = "PG-V2", Customer = "Customer",
                ValidationRequirement = "Requirement",
                RequestedFinishDate = DateOnly.FromDateTime(now.AddDays(1))
            }, now);
        app.Submit(new VerificationApplicationRouting(department, "D", "Department", team,
            "T", "Team", "verification", "leader", "Leader"), now);
        var recordId = Guid.NewGuid();
        app.Accept(recordId, DateOnly.FromDateTime(now.AddDays(1)), "leader", now);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = new DocumentFolder
        {
            Id = Guid.NewGuid(), Level = 1, Name = "PG V2", SortOrder = 1,
            CreatedAt = now, UpdatedAt = now
        };
        var document = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = folder.Id, Folder = folder,
            OriginalFileName = "claim.xlsx", StandardFileName = "claim.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            UploadedBy = "owner", UploadedAt = now, FileSize = bytes.Length, Sha256 = sha,
            RelativePath = "claim.xlsx", StorageArea = DocumentStorageAreas.Library
        };
        var attachment = new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = app.Id, VerificationApplication = app,
            FileName = document.OriginalFileName, FilePath = document.RelativePath,
            ContentType = document.ContentType, FileSize = bytes.Length, Sha256 = sha,
            UploadedBy = "owner", CreatedAt = now, DocumentLibraryFileId = document.Id,
            DocumentLibraryFile = document
        };
        app.Files.Add(attachment);
        await File.WriteAllBytesAsync(Path.Combine(root, document.RelativePath), bytes);
        await using var db = await factory.CreateDbContextAsync();
        db.SystemOptions.AddRange(
            new SystemOption
            {
                Id = department, Category = SystemOptionCategories.Department,
                Name = "Department", Value = "D", Sort = 1, IsEnabled = true,
                CreatedAt = now, UpdatedAt = now
            },
            new SystemOption
            {
                Id = team, Category = SystemOptionCategories.Team,
                Name = "Team", Value = "T", Sort = 1, IsEnabled = true,
                CreatedAt = now, UpdatedAt = now
            });
        var module = new ModuleEntity
        {
            Id = Guid.NewGuid(), Code = "verification", Name = "Verification",
            DisplayName = "Verification", RoutePrefix = "verification", IsEnabled = true,
            SortOrder = 1, CreatedAt = now, UpdatedAt = now
        };
        db.Modules.Add(module);
        db.ModuleRecords.Add(new ModuleRecord
        {
            Id = recordId, ModuleId = module.Id, Module = module,
            RecordNo = "PG-V2", Name = "PG V2", CreatedAt = now, UpdatedAt = now
        });
        db.VerificationApplications.Add(app);
        db.DocumentFolders.Add(folder);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVerificationAttachment(
            app, attachment, now));
        await db.SaveChangesAsync();
    }

    private static async Task SeedVendorAsync(Factory factory, string root)
    {
        var now = DateTime.UtcNow;
        var bytes = new byte[] { 5, 6, 7, 8 };
        var id = Guid.NewGuid();
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(), Name = "PG Vendor", NormalizedName = "PG VENDOR",
            StorageFolderName = "pg-vendor", Types = [], CreatedBy = "owner", UpdatedBy = "owner",
            CreatedAt = now, UpdatedAt = now
        };
        var file = new VendorFile
        {
            Id = id, VendorId = vendor.Id, Vendor = vendor, OriginalFileName = "vendor.txt",
            StoredFileName = id.ToString("N") + ".txt", FolderPath = root,
            ContentType = "text/plain", FileSize = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            ArchiveStatus = FileArchiveStatuses.Archived, ArchiveAttemptCount = 1,
            ArchiveUpdatedAt = now, UploadedBy = "owner", UploadedAt = now,
            ApprovalOperationId = id, ApprovedBy = "admin", ApprovedAt = now
        };
        await File.WriteAllBytesAsync(Path.Combine(root, file.StoredFileName), bytes);
        await using var db = await factory.CreateDbContextAsync();
        db.Vendors.Add(vendor);
        db.VendorFiles.Add(file);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVendorFile(file, now));
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(id, "vendors", "vendor",
            ApprovalAuditActions.ApprovalBypassed, "admin", now, null, null));
        await db.SaveChangesAsync();
    }

    private static async Task SeedEquipmentImageAsync(Factory factory, string root)
    {
        var now = DateTime.UtcNow;
        var bytes = new byte[] { 9, 10, 11, 12 };
        var equipment = new Apparatus
        {
            Id = "PG-EQUIPMENT", ModuleCode = "equipment", ProductsId = "PG-ASSET",
            Name = "PG Equipment", Kind = "Instrument", CreatedAt = now, UpdatedAt = now
        };
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = "front.png", OriginalFileName = "front.png", FilePath = root,
            ContentType = "image/png", FileSize = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            ArchiveStatus = FileArchiveStatuses.Archived, ArchiveUpdatedAt = now, CreatedAt = now,
            UploadEmp = "owner"
        };
        equipment.Files.Add(file);
        await File.WriteAllBytesAsync(Path.Combine(root, file.FileName), bytes);
        await using var db = await factory.CreateDbContextAsync();
        db.Apparatuses.Add(equipment);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedEquipmentDocument(
            equipment, file, now));
        db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(file.Id, "equipment", "equipment",
            ApprovalAuditActions.ApprovalBypassed, "admin", now, null, null));
        await db.SaveChangesAsync();
    }

    private sealed class CaptureClient : IKmIntegrationClient
    {
        public List<KmV2DocumentJobRequest> Requests { get; } = [];

        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(KmDeliveryResult.Failure(false, "UNEXPECTED", "Unexpected v1 request."));

        public Task<KmDeliveryResult> SubmitDocumentJobAsync(KmV2DocumentJobRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(KmDeliveryResult.Success("KM-" + request.CsitJobId.ToString("N")));
        }
    }

    private sealed class Factory(string connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;

        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

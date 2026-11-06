using System.Security.Cryptography;
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
    public async Task PostgreSqlClaimDispatchesAllV2SourcesIncludingEquipmentImage()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_KM_V2_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", info.Host);
        Assert.Equal(55432, info.Port);
        Assert.StartsWith("csit_km_v2_", info.Database, StringComparison.Ordinal);

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

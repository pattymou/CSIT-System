using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace ApprovalFramework.Tests;

public sealed class EquipmentVendorManagementTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlMigration_EnforcesConstraints_AndRunsVendorArchiveRoundTrip()
    {
        var connection = Environment.GetEnvironmentVariable("CSIT_EQUIPMENT_VENDOR_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith("csit_equipment_vendor_", info.Database, StringComparison.Ordinal);
        Assert.True(info.Host is "localhost" or "127.0.0.1");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using var db = new AppDbContext(options);
        Assert.Contains("20261027080940_AddEquipmentVendorManagement", await db.Database.GetAppliedMigrationsAsync());
        Assert.True(await db.MenuItems.AnyAsync(x => x.Code == "vendors" && x.RoutePath == "/vendors"));

        var root = NewRoot();
        try
        {
            await SeedRootAsync(db, root);
            var service = new VendorService(db, new ManagedAssetFileStorage(db));
            var id = await service.CreateAsync(new VendorUpsertRequest
                { Name = "PostgreSQL Vendor", Types = [], IsActive = true }, "pg-test");
            await service.ArchiveApprovedFileAsync(id,
                File("manual.pdf", "application/pdf", "pg-content"), "pg-test",
                Guid.NewGuid(), "pg-approver", DateTime.UtcNow);
            var detail = Assert.IsType<VendorDetailDto>(await service.GetAsync(id));
            Assert.Equal(FileArchiveStatuses.Archived, Assert.Single(detail.Files).ArchiveStatus);

            db.Vendors.Add(new Vendor
            {
                Id = Guid.NewGuid(), Name = "Duplicate physical row", NormalizedName = "POSTGRESQL VENDOR",
                StorageFolderName = "Different folder", Types = [], CreatedBy = "pg-test", UpdatedBy = "pg-test",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.Vendors.Add(new Vendor
            {
                Id = Guid.NewGuid(), Name = "Invalid Type", NormalizedName = "INVALID TYPE",
                StorageFolderName = "Invalid Type", Types = ["NotAllowed"], CreatedBy = "pg-test",
                UpdatedBy = "pg-test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task VendorCrud_NormalizesDuplicate_DisablesOptions_AndProtectsReferences()
    {
        await using var db = NewDb();
        var root = NewRoot();
        try
        {
            await SeedRootAsync(db, root);
            var service = new VendorService(db, new ManagedAssetFileStorage(db));
            var id = await service.CreateAsync(new VendorUpsertRequest
            {
                Name = "  Acme   Instruments  ", Types = [], IsActive = true
            }, "tester");
            var detail = Assert.IsType<VendorDetailDto>(await service.GetAsync(id));
            Assert.Equal("Acme Instruments", detail.Name);
            Assert.Empty(detail.Types);

            var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
                new VendorUpsertRequest { Name = "acme instruments", IsActive = true }, "tester"));
            Assert.Contains("相同名稱", duplicate.Message);

            await service.UpdateAsync(id, new VendorUpsertRequest
            {
                Name = detail.Name, Types = ["Manufacturer", "Supplier"], IsActive = false,
                RowVersion = detail.RowVersion
            }, "editor");
            Assert.DoesNotContain(await service.OptionsAsync(), x => x.Id == id);
            Assert.Contains(await service.OptionsAsync(id), x => x.Id == id && !x.IsActive);

            db.Apparatuses.Add(new Apparatus
            {
                Id = "EQ-REF", ModuleCode = "equipment", Name = "Referenced", Kind = "Tester",
                VendorId = id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            var referenced = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(id));
            Assert.Contains("設備引用", referenced.Message);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task VendorFiles_UseThreeLevelStableFolder_AndVerifiedSha256AcrossRename()
    {
        await using var db = NewDb();
        var root = NewRoot();
        try
        {
            await SeedRootAsync(db, root);
            var service = new VendorService(db, new ManagedAssetFileStorage(db));
            var id = await service.CreateAsync(new VendorUpsertRequest
                { Name = "Keysight", Types = ["Manufacturer"], IsActive = true }, "tester");
            var files = new[]
            {
                File("manual.pdf", "application/pdf", "pdf-content"),
                File("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx-content"),
                File("photo.png", "image/png", "png-content")
            };
            var operations = new List<Guid>();
            foreach (var file in files)
            {
                var operation = Guid.NewGuid();
                operations.Add(operation);
                await service.ArchiveApprovedFileAsync(id, file, "tester",
                    operation, "approver", DateTime.UtcNow);
            }

            var retryId = await service.ArchiveApprovedFileAsync(id,
                File("manual.pdf", "application/pdf", "pdf-content"), "tester",
                operations[0], "approver", DateTime.UtcNow);

            var before = Assert.IsType<VendorDetailDto>(await service.GetAsync(id));
            Assert.Equal(3, before.Files.Count);
            Assert.Equal(operations[0], retryId);
            Assert.Equal(3, await db.KmIntegrationOutbox.CountAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived));
            Assert.All(before.Files, file =>
            {
                Assert.Equal(FileArchiveStatuses.Archived, file.ArchiveStatus);
                Assert.Equal(64, file.Sha256!.Length);
            });
            Assert.True(before.Files.Single(x => x.FileName == "photo.png").IsImage);
            Assert.False(before.Files.Single(x => x.FileName == "manual.pdf").IsImage);
            var vendorFolder = Path.Combine(root, "設備儀器管理", "廠商資訊", "Keysight");
            Assert.True(Directory.Exists(vendorFolder));

            await service.UpdateAsync(id, new VendorUpsertRequest
            {
                Name = "Keysight Technologies", Types = ["Manufacturer"], IsActive = true,
                RowVersion = before.RowVersion
            }, "editor");
            Assert.True(Directory.Exists(vendorFolder));
            Assert.False(Directory.Exists(Path.Combine(root, "設備儀器管理", "廠商資訊", "Keysight Technologies")));
            var downloaded = await service.DownloadAsync(before.Files.Single(x => x.FileName == "manual.pdf").Id);
            Assert.Equal("pdf-content", Encoding.UTF8.GetString(downloaded.Content));
            var blockedDelete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteFileAsync(before.Files[0].Id));
            Assert.Contains("KM", blockedDelete.Message);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task VendorApprovalSource_UsesAuthenticatedAccountTeamAndRequiresUniqueRouting()
    {
        var database = Guid.NewGuid().ToString("N");
        var factory = new TestDbFactory(database);
        var vendorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor
            {
                Id = vendorId, Name = "Routed Vendor", NormalizedName = "ROUTED VENDOR",
                StorageFolderName = "Routed Vendor", Types = [], CreatedBy = "tester",
                UpdatedBy = "tester", CreatedAt = now, UpdatedAt = now
            });
            db.SystemOptions.Add(new SystemOption
            {
                Id = teamId, Category = SystemOptionCategories.Team, Name = "Lab", Value = "LAB",
                IsEnabled = true, CreatedAt = now, UpdatedAt = now
            });
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "leader",
                LeaderDisplayName = "Leader", IsEnabled = true, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("account", "tester")], "test"));
        var resolver = new VendorApprovalSourceResolver(factory, ProvisioningClient());
        var source = await resolver.ResolveAsync(vendorId, user);
        Assert.Equal("vendor", source.ModuleCode);
        Assert.Equal("Vendor", source.ContextType);
        Assert.Equal(teamId, source.TeamOptionId);
        Assert.Equal("LAB", source.TeamCode);

        await using (var db = factory.CreateDbContext())
        {
            db.TeamRoutings.Add(new TeamRouting
            {
                Id = Guid.NewGuid(), TeamOptionId = teamId, LeaderAccount = "other-leader",
                LeaderDisplayName = "Other", IsEnabled = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(vendorId, user));
        Assert.Contains("唯一", error.Message);
    }

    [Fact]
    public async Task Equipment_RequiresActiveVendorForNewRecords_PreservesLegacy_AndArchivesApprovedFormats()
    {
        await using var db = NewDb();
        var root = NewRoot();
        try
        {
            var ownership = await SeedRootAndOwnershipAsync(db, root);
            var vendorService = new VendorService(db, new ManagedAssetFileStorage(db));
            var vendorId = await vendorService.CreateAsync(new VendorUpsertRequest
                { Name = "Anritsu", Types = ["Manufacturer"], IsActive = true }, "tester");
            var raw = Options.Create(new RawDataSettings { Enabled = false });
            var rawExport = new CapturingRawDataExportService();
            var apparatus = new ApparatusService(db, Options.Create(new ApparatusSettings()), raw,
                rawExport, ProvisioningClient(), new ManagedAssetFileStorage(db));
            var request = Request(vendorId, ownership.DepartmentId, ownership.TeamId);
            request.Brand = "untrusted typed brand";
            var equipmentId = await apparatus.CreateAsync("equipment", request);
            var stored = await db.Apparatuses.SingleAsync(x => x.Id == equipmentId);
            Assert.Equal(vendorId, stored.VendorId);
            Assert.Equal("Anritsu", stored.Manufacturer);
            Assert.Equal("Anritsu", stored.Brand);

            var sameVendorUpdate = Request(vendorId, ownership.DepartmentId, ownership.TeamId);
            sameVendorUpdate.Name = "Updated without changing vendor";
            sameVendorUpdate.Brand = "must not overwrite the stored brand";
            Assert.True(await apparatus.UpdateAsync("equipment", equipmentId, sameVendorUpdate));
            Assert.Equal("Anritsu", (await db.Apparatuses.SingleAsync(x => x.Id == equipmentId)).Brand);

            var approvedFiles = new[]
            {
                File("manual.pdf", "application/pdf", "manual"),
                File("datasheet.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "word"),
                File("sop.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "ppt"),
                File("front.jpg", "image/jpeg", "image")
            };
            foreach (var file in approvedFiles)
                await apparatus.UploadFilesAsync("equipment", equipmentId, [file], "approver", Guid.NewGuid());

            var legacyId = Guid.NewGuid();
            var equipmentFolder = (await db.ApparatusFiles.FirstAsync()).FilePath;
            var legacyName = legacyId.ToString("N") + ".jpg";
            var legacyBytes = Encoding.UTF8.GetBytes("legacy-image");
            await System.IO.File.WriteAllBytesAsync(Path.Combine(equipmentFolder, legacyName), legacyBytes);
            db.ApparatusFiles.Add(new ApparatusFile
            {
                Id = legacyId, ApparatusId = equipmentId, FileName = legacyName,
                OriginalFileName = "legacy-photo.jpeg", FilePath = equipmentFolder,
                ContentType = null, FileSize = legacyBytes.Length, ArchiveStatus = null,
                CreatedAt = DateTime.UtcNow
            });

            var now = DateTime.UtcNow;
            var reservation = new Reservation
            {
                Id = Guid.NewGuid(), ReservationNo = "RSV-EQUIPMENT-001",
                ApplicantAccount = "tester", ApplicantName = "Tester",
                ApplicantDepartment = "TW", ApplicantExtension = "1234",
                ApplicantAgentName = "Agent", ApplicantAgentExtension = "5678",
                ApplicantAgentEmail = "agent@example.test", Purpose = "Regression",
                ProductModelName = "DUT", Customer = "Internal", ProjectSubPu = "CSIT",
                StartTime = now.AddHours(-2), EndTime = now.AddHours(2),
                CreatedAt = now, UpdatedAt = now,
                Items = [new ReservationItem
                {
                    Id = Guid.NewGuid(), ApparatusId = equipmentId,
                    ApparatusName = stored.Name, ProductsId = stored.ProductsId,
                    Kind = stored.Kind
                }]
            };
            reservation.Submit(now);
            reservation.Approve("leader", now.AddMinutes(1));
            reservation.Checkout("leader", now.AddMinutes(2));
            reservation.Return("leader", now.AddMinutes(3));
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync();

            var detail = Assert.IsType<ApparatusDetailDto>(await apparatus.GetByIdAsync("equipment", equipmentId));
            Assert.Equal(5, detail.Files.Count);
            Assert.All(detail.Files.Where(x => x.Id != legacyId),
                file => Assert.Equal(FileArchiveStatuses.Archived, file.ArchiveStatus));
            Assert.Equal(3, await db.KmIntegrationOutbox.CountAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile));
            Assert.All(detail.Files.Where(x => !x.IsImage), file =>
            {
                Assert.True(file.IsKmEligibleDocument);
                Assert.True(file.HasKmSchedule);
                Assert.Equal(1, file.KmVersion);
                Assert.Equal(KmIntegrationStatus.Pending.ToString(), file.KmStatus);
                Assert.False(file.KmEnabled);
                Assert.True(file.CanDelete);
            });
            Assert.Equal(2, detail.Files.Count(x => x.IsImage));
            Assert.All(detail.Files.Where(x => x.IsImage), photo =>
            {
                Assert.False(photo.IsKmEligibleDocument);
                Assert.False(photo.HasKmSchedule);
                Assert.True(photo.CanDelete);
                Assert.True(photo.IsFormalArchiveVerified);
            });
            var legacyPhoto = Assert.Single(detail.Files, x => x.Id == legacyId);
            Assert.Null(legacyPhoto.ArchiveStatus);
            Assert.EndsWith($"/{legacyId}/image", legacyPhoto.ImageUrl);
            Assert.EndsWith($"/{legacyId}/content", legacyPhoto.DownloadUrl);
            var legacyContent = await apparatus.GetFileContentAsync(legacyId);
            Assert.Equal("image/jpeg", legacyContent.ContentType);
            var lifecycle = Assert.Single(detail.LifecycleRecords);
            Assert.Equal("RSV-EQUIPMENT-001", lifecycle.ReservationNo);
            Assert.Equal(ReservationStatus.Returned, lifecycle.Status);
            Assert.NotNull(lifecycle.BorrowedAt);
            Assert.NotNull(lifecycle.ReturnedAt);
            Assert.NotEqual(default, detail.CreatedAt);
            Assert.NotEqual(default, detail.UpdatedAt);
            Assert.NotNull(rawExport.LastLatestPackage);
            Assert.Empty(rawExport.LastLatestPackage.Files);
            Assert.Contains(Path.Combine("設備儀器管理", "設備資訊", "Wi-Fi 7 Tester_ASSET-1234"),
                (await db.ApparatusFiles.FirstAsync()).FilePath);

            var vendor = await db.Vendors.SingleAsync(x => x.Id == vendorId);
            vendor.IsActive = false;
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => apparatus.CreateAsync("equipment",
                Request(vendorId, ownership.DepartmentId, ownership.TeamId, "ASSET-5678")));

            db.Apparatuses.Add(new Apparatus
            {
                Id = "LEGACY-EQ", ModuleCode = "equipment", Name = "Legacy", Kind = "Tester",
                Manufacturer = "Legacy Text Vendor", Brand = "Legacy Brand", DepartmentOptionId = ownership.DepartmentId,
                OwnerTeamOptionId = ownership.TeamId, CustodianAccount = "tester",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            var legacyRequest = Request(null, ownership.DepartmentId, ownership.TeamId, null);
            legacyRequest.Id = "LEGACY-EQ";
            legacyRequest.Name = "Legacy Updated";
            legacyRequest.Manufacturer = "Legacy Text Vendor";
            legacyRequest.Brand = "must not overwrite legacy brand";
            Assert.True(await apparatus.UpdateAsync("equipment", "LEGACY-EQ", legacyRequest));
            Assert.Equal("Legacy Text Vendor", (await db.Apparatuses.SingleAsync(x => x.Id == "LEGACY-EQ")).Manufacturer);
            Assert.Equal("Legacy Brand", (await db.Apparatuses.SingleAsync(x => x.Id == "LEGACY-EQ")).Brand);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Equipment_OwnershipTransfer_RequiresExplicitServerValidatedConfirmation()
    {
        await using var db = NewDb();
        var root = NewRoot();
        try
        {
            var ownership = await SeedRootAndOwnershipAsync(db, root);
            var alternateTeamId = Guid.NewGuid();
            db.SystemOptions.Add(new SystemOption
            {
                Id = alternateTeamId, Category = SystemOptionCategories.Team,
                Name = "Lab Alternate", Value = "LAB", IsEnabled = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            var vendorId = Guid.NewGuid();
            db.Vendors.Add(new Vendor
            {
                Id = vendorId, Name = "Vendor", StorageFolderName = "Vendor", Types = [], IsActive = true,
                CreatedBy = "tester", UpdatedBy = "tester", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var apparatus = new ApparatusService(db, Options.Create(new ApparatusSettings()),
                Options.Create(new RawDataSettings { Enabled = false }), new CapturingRawDataExportService(),
                ProvisioningClient(), new ManagedAssetFileStorage(db));
            var id = await apparatus.CreateAsync("equipment",
                Request(vendorId, ownership.DepartmentId, ownership.TeamId));
            var transfer = Request(vendorId, ownership.DepartmentId, alternateTeamId);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                apparatus.UpdateAsync("equipment", id, transfer));
            Assert.Contains("必須明確確認", error.Message);

            transfer.ConfirmOwnershipChange = true;
            Assert.True(await apparatus.UpdateAsync("equipment", id, transfer));
            Assert.Equal(alternateTeamId,
                (await db.Apparatuses.SingleAsync(x => x.Id == id)).OwnerTeamOptionId);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ManagedStorage_RejectsTraversal_UnsupportedFiles_AndConflictingRetry()
    {
        await using var db = NewDb();
        var root = NewRoot();
        try
        {
            await SeedRootAsync(db, root);
            var storage = new ManagedAssetFileStorage(db);
            Assert.Throws<InvalidOperationException>(() => ManagedAssetFileStorage.ValidateOriginalFileName("../secret.pdf", 1));
            Assert.Throws<InvalidOperationException>(() => ManagedAssetFileStorage.ValidateOriginalFileName("tool.exe", 1));
            var folder = await storage.VendorFolderAsync("Safe");
            await using var first = new MemoryStream(Encoding.UTF8.GetBytes("one"));
            var hash = await storage.ArchiveAsync(first, folder, "fixed.pdf");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("one"))), hash);
            await using var conflict = new MemoryStream(Encoding.UTF8.GetBytes("two"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ArchiveAsync(conflict, folder, "fixed.pdf"));
        }
        finally { DeleteRoot(root); }
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "csit-equipment-vendor-tests", Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static async Task SeedRootAsync(AppDbContext db, string root)
    {
        Directory.CreateDirectory(root);
        db.SystemOptions.Add(new SystemOption
        {
            Id = Guid.NewGuid(), Category = "AssetRootPath", Name = "equipment", Value = root,
            IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid DepartmentId, Guid TeamId)> SeedRootAndOwnershipAsync(AppDbContext db, string root)
    {
        await SeedRootAsync(db, root);
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.SystemOptions.AddRange(
            new SystemOption { Id = departmentId, Category = SystemOptionCategories.Department, Name = "台北", Value = "TW", IsEnabled = true, CreatedAt = now, UpdatedAt = now },
            new SystemOption { Id = teamId, Category = SystemOptionCategories.Team, Name = "Lab", Value = "LAB", IsEnabled = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (departmentId, teamId);
    }

    private static ApparatusUpsertRequest Request(Guid? vendorId, Guid departmentId, Guid teamId,
        string? productsId = "ASSET-1234") => new()
    {
        ProductsId = productsId, Name = "Wi-Fi 7 Tester", Kind = "Tester", VendorId = vendorId,
        Manufacturer = "untrusted legacy input", DepartmentOptionId = departmentId,
        OwnerTeamOptionId = teamId, CustodianAccount = "tester", ReservationStatus = "可借用"
    };

    private static FormFile File(string name, string contentType, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
            { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    private static SharedIdentityProvisioningClient ProvisioningClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SharedIdentity:ProvisioningClientId"] = "test-client",
            ["SharedIdentity:ProvisioningClientSecret"] = "test-secret"
        }).Build();
        return new SharedIdentityProvisioningClient(new TestHttpClientFactory(), configuration,
            NullLogger<SharedIdentityProvisioningClient>.Instance);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new IdentityHandler())
            { BaseAddress = new Uri("http://identity.test/") };
    }

    private sealed class TestDbFactory(string database) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            object body = request.RequestUri!.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal)
                ? new { access_token = "test-token" }
                : new[]
                {
                    new
                    {
                        identityUserId = Guid.NewGuid(), employeeNo = "T001", account = "tester",
                        displayName = "Tester", department = "TW", teamCode = "LAB", position = "Engineer",
                        extension = (string?)null, email = "tester@askey.com", csitRole = "CsitStaff",
                        dataAccessLevel = "Editor", accountStatus = "Active", systemAccessStatus = "Active",
                        createdAt = DateTimeOffset.UtcNow, createdByIdentityUserId = (Guid?)null,
                        createdByAccount = (string?)null, canReissueTemporaryPassword = false
                    }
                };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingRawDataExportService : IRawDataExportService
    {
        public RawDataLatestPackageRequest? LastLatestPackage { get; private set; }
        public Task<RawDataExportResult> ExportAsync(RawDataExportRequest request) =>
            Task.FromResult(new RawDataExportResult { Success = true });
        public Task<RawDataExportResult> ExportLatestPackageAsync(RawDataLatestPackageRequest request)
        {
            LastLatestPackage = request;
            return Task.FromResult(new RawDataExportResult { Success = true });
        }
        public Task<bool> DeleteLatestPackageAsync(string sourceType, string moduleCode, string entityId) =>
            Task.FromResult(true);
    }
}

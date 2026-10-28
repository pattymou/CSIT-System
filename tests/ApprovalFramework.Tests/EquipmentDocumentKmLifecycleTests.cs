using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentDocumentKmLifecycleTests
{
    [Fact]
    public async Task EquipmentOutbox_WaitsWhenDisabled_ThenDispatchesAndStoresCallbackSuggestions()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = SeedDocument(factory);
        var mock = new EquipmentMockKmClient();

        Assert.Equal(0, await Worker(factory, mock, enabled: false).ProcessBatchAsync());
        Assert.Null(mock.LastEquipmentRequest);
        await using (var waiting = factory.CreateDbContext())
            Assert.Equal(KmIntegrationStatus.Pending,
                (await waiting.KmIntegrationOutbox.SingleAsync()).Status);

        Assert.Equal(1, await Worker(factory, mock, enabled: true).ProcessBatchAsync());
        var request = Assert.IsType<EquipmentDocumentIngestionRequest>(mock.LastEquipmentRequest);
        Assert.Equal(file.Id, request.EventId);
        Assert.Equal(file.Id, request.DocumentId);
        Assert.Equal("EQ-KM-001", request.Equipment.Id);
        Assert.Equal("ASSET-KM-001", request.Equipment.ProductsId);
        Assert.Equal("Manual", request.File.DocumentKind);
        Assert.Equal(1, request.Version);

        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        var processing = new EquipmentDocumentStatusCallback(
            "1.0", "equipment-job-1", "CSIT", file.Id, 1, "Processing",
            new KmCallbackTimestamps(DateTime.UtcNow, null, null), null, null);
        Assert.IsType<NoContentResult>(await controller.EquipmentDocumentStatus(processing, default));

        var result = new EquipmentMetadataSuggestion(
            "1.0",
            [new EquipmentTechnicalSpecificationSuggestion("WiFiStandard", "Wi-Fi 7",
                new EquipmentMetadataEvidence("manual.pdf", 12, "Specifications", "802.11be"))],
            "Wireless protocol validation equipment",
            ["Requires external RF accessories"],
            [new EquipmentMetadataEvidence("manual.pdf", 12, "Specifications", "802.11be")]);
        var completed = processing with
        {
            Status = "Completed",
            Timestamps = new KmCallbackTimestamps(null, DateTime.UtcNow, null),
            Result = result
        };
        Assert.IsType<NoContentResult>(await controller.EquipmentDocumentStatus(completed, default));

        await using var check = factory.CreateDbContext();
        var outbox = await check.KmIntegrationOutbox.SingleAsync();
        var stored = await check.ApparatusFiles.Include(x => x.Apparatus).SingleAsync();
        Assert.Equal(KmIntegrationStatus.Completed, outbox.Status);
        Assert.Equal(MetadataVerificationStatuses.PendingVerification,
            stored.MetadataVerificationStatus);
        Assert.Contains("WiFiStandard", stored.MetadataJson);
        Assert.Equal("AndersonKM:EquipmentDocument", stored.MetadataSource);
        Assert.Equal("1.0", stored.MetadataVersion);
        Assert.Equal("Original equipment name", stored.Apparatus.Name);
        Assert.Equal("ASSET-KM-001", stored.Apparatus.ProductsId);
    }

    [Fact]
    public async Task EquipmentCallback_RejectsMismatch_AndPhotoCannotEnterOutbox()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = SeedDocument(factory);
        var mock = new EquipmentMockKmClient();
        Assert.Equal(1, await Worker(factory, mock, enabled: true).ProcessBatchAsync());

        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        var mismatch = new EquipmentDocumentStatusCallback(
            "1.0", "equipment-job-1", "CSIT", Guid.NewGuid(), 1, "Processing",
            new KmCallbackTimestamps(DateTime.UtcNow, null, null), null, null);
        Assert.IsType<ConflictObjectResult>(
            await controller.EquipmentDocumentStatus(mismatch, default));

        var equipment = new Apparatus
        {
            Id = "EQ-PHOTO", ModuleCode = "equipment", Name = "Photo Equipment",
            Kind = "Tester", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var photo = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = "front.jpg", OriginalFileName = "front.jpg",
            FilePath = Path.GetTempPath(), ContentType = "image/jpeg", FileSize = 10,
            Sha256 = new string('A', 64), ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        Assert.Throws<InvalidOperationException>(() =>
            KmIntegrationOutbox.ForArchivedEquipmentDocument(equipment, photo, DateTime.UtcNow));

        await using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Sent,
            (await check.KmIntegrationOutbox.SingleAsync()).Status);
        Assert.Null((await check.ApparatusFiles.SingleAsync()).MetadataJson);
    }

    private static ApparatusFile SeedDocument(TestDbFactory factory)
    {
        using var db = factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var equipment = new Apparatus
        {
            Id = "EQ-KM-001", ModuleCode = "equipment", ProductsId = "ASSET-KM-001",
            Name = "Original equipment name", Kind = "RF Tester", Brand = "Legacy Brand",
            Model = "MODEL-1", CreatedAt = now, UpdatedAt = now
        };
        var file = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = "stored-manual.pdf", OriginalFileName = "manual.pdf",
            FilePath = Path.GetTempPath(), ContentType = "application/pdf", FileSize = 12,
            Sha256 = new string('A', 64), ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = now, CreatedAt = now,
            MetadataVerificationStatus = MetadataVerificationStatuses.NotApplicable
        };
        db.Apparatuses.Add(equipment);
        db.ApparatusFiles.Add(file);
        db.KmIntegrationOutbox.Add(
            KmIntegrationOutbox.ForArchivedEquipmentDocument(equipment, file, now));
        db.SaveChanges();
        return file;
    }

    private static KmSyncWorker Worker(TestDbFactory factory, IKmIntegrationClient client,
        bool enabled) => new(factory, client, Options.Create(new KmIntegrationOptions
        {
            Enabled = true,
            EquipmentEnabled = enabled,
            BaseUrl = "https://km.invalid",
            ServiceBaseUrl = "https://csit.invalid/",
            TimeoutSeconds = 2,
            LeaseSeconds = 30,
            MaxRetryCount = 3,
            RetryScheduleSeconds = [1],
            PollSeconds = 1
        }), NullLogger<KmSyncWorker>.Instance);

    private sealed class EquipmentMockKmClient : IKmIntegrationClient
    {
        public EquipmentDocumentIngestionRequest? LastEquipmentRequest { get; private set; }

        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(KmDeliveryResult.Failure(false, "UNEXPECTED", "Unexpected request."));

        public Task<KmDeliveryResult> SubmitEquipmentDocumentAsync(
            EquipmentDocumentIngestionRequest request, CancellationToken cancellationToken)
        {
            LastEquipmentRequest = request;
            return Task.FromResult(KmDeliveryResult.Success("equipment-job-1"));
        }
    }

    private sealed class TestDbFactory(string database) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(database).Options;

        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

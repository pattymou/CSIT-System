using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class VendorAttachmentLifecycleTests
{
    [Fact]
    public async Task VendorOutboxWaitsWhileVendorContractDisabled_ThenMockDispatchAndCallbackComplete()
    {
        var factory = new TestDbFactory(Guid.NewGuid().ToString("N"));
        var file = Seed(factory);
        var mock = new VendorMockKmClient();
        var disabled = OptionsForVendor(false);
        Assert.Equal(0, await Worker(factory, mock, disabled).ProcessBatchAsync());
        Assert.Null(mock.LastVendorRequest);
        await using (var db = factory.CreateDbContext())
            Assert.Equal(KmIntegrationStatus.Pending, (await db.KmIntegrationOutbox.SingleAsync()).Status);

        var enabled = OptionsForVendor(true);
        Assert.Equal(1, await Worker(factory, mock, enabled).ProcessBatchAsync());
        Assert.NotNull(mock.LastVendorRequest);
        Assert.Equal(file.Id, mock.LastVendorRequest!.DocumentId);
        Assert.Equal(KmIntegrationEventTypes.VendorFileArchived, mock.LastVendorRequest.EventType);
        Assert.Equal("VendorFile", KmIntegrationSourceEntityTypes.VendorFile);

        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        var callback = new KmStatusCallback("vendor-job-1", "CSIT", file.Id, 1,
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
        var mismatch = new KmStatusCallback("vendor-job-1", "CSIT", Guid.NewGuid(), 1,
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

    private static VendorFile Seed(TestDbFactory factory)
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
        var file = new VendorFile
        {
            Id = id, VendorId = vendor.Id, Vendor = vendor, OriginalFileName = "manual.pdf",
            StoredFileName = id.ToString("N") + ".pdf", FolderPath = Path.GetTempPath(),
            ContentType = "application/pdf", FileSize = 12, Sha256 = new string('A', 64),
            ArchiveStatus = FileArchiveStatuses.Archived, ArchiveAttemptCount = 1,
            ArchiveUpdatedAt = now, UploadedBy = "tester", UploadedAt = now,
            ApprovalOperationId = id, ApprovedBy = "leader", ApprovedAt = now,
            MetadataVerificationStatus = MetadataVerificationStatuses.NotApplicable
        };
        db.Vendors.Add(vendor);
        db.VendorFiles.Add(file);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVendorFile(file, now));
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

    private static KmSyncWorker Worker(TestDbFactory factory, IKmIntegrationClient client,
        KmIntegrationOptions options) => new(factory, client, Options.Create(options),
        NullLogger<KmSyncWorker>.Instance);

    private sealed class VendorMockKmClient : IKmIntegrationClient
    {
        public VendorFileIngestionRequest? LastVendorRequest { get; private set; }
        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(KmDeliveryResult.Failure(false, "UNEXPECTED", "Unexpected request."));

        public Task<KmDeliveryResult> SubmitVendorFileAsync(VendorFileIngestionRequest request,
            CancellationToken cancellationToken)
        {
            LastVendorRequest = request;
            return Task.FromResult(KmDeliveryResult.Success("vendor-job-1"));
        }
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

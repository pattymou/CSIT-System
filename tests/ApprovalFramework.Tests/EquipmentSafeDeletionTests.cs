using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class EquipmentSafeDeletionTests
{
    [Fact]
    public async Task PreviewSupportsLegacyEquipmentWithNullableMetadataAndNoDeletionJob()
    {
        var factory = new Factory();
        await using (var db = factory.CreateDbContext())
        {
            var equipment = Equipment("LEGACY-EQUIPMENT", null);
            equipment.ProductsId = null;
            equipment.Brand = null;
            equipment.CustodianAccount = null;
            db.Apparatuses.Add(equipment);
            db.ApparatusFiles.AddRange(
                new ApparatusFile
                {
                    Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
                    FileName = "photo.jpg", OriginalFileName = "photo.jpg",
                    FilePath = "legacy", ContentType = "image/jpeg", FileSize = 1,
                    CreatedAt = DateTime.UtcNow
                },
                new ApparatusFile
                {
                    Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
                    FileName = "manual.pdf", OriginalFileName = "manual.pdf",
                    FilePath = "legacy", ContentType = "application/pdf", FileSize = 1,
                    CreatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var preview = await Service(verify).GetPreviewAsync("LEGACY-EQUIPMENT");

        Assert.NotNull(preview);
        Assert.True(preview.Exists);
        Assert.Equal("LEGACY-EQUIPMENT", preview.EquipmentId);
        Assert.Equal("LEGACY-EQUIPMENT", preview.EquipmentName);
        Assert.Null(preview.ProductsId);
        Assert.Equal(2, preview.AttachmentCount);
        Assert.Empty(preview.BlockingReasons);
        Assert.Null(preview.DeletionJobId);
        Assert.True(preview.CanStart);
    }

    [Fact]
    public async Task PreviewAndRequestReturnBothPermanentReservationAndEnvironmentBlockers()
    {
        var factory = new Factory();
        await using (var db = factory.CreateDbContext())
        {
            var equipment = Equipment("EQ-BLOCKED", null);
            var reservation = new Reservation
            {
                Id = Guid.NewGuid(), ReservationNo = "R-BLOCKED", ApplicantAccount = "user",
                ApplicantName = "User", ApplicantDepartment = "QA", Purpose = "audit",
                StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            db.Add(equipment);
            db.Add(reservation);
            db.Add(new ReservationItem
            {
                Id = Guid.NewGuid(), ReservationId = reservation.Id, Reservation = reservation,
                ApparatusId = equipment.Id, Apparatus = equipment, ApparatusName = equipment.Name
            });
            db.Add(new EquipmentGroupDevice
            {
                Id = Guid.NewGuid(), EquipmentGroupId = Guid.NewGuid(), ApparatusId = equipment.Id,
                Apparatus = equipment, AddedAt = DateTime.UtcNow, AddedBy = "manager"
            });
            await db.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var service = Service(verify);
        var preview = await service.GetPreviewAsync("EQ-BLOCKED");
        Assert.NotNull(preview);
        Assert.True(preview.HasReservationHistory);
        Assert.True(preview.HasEnvironmentGroupMembership);
        Assert.Equal(new[]
        {
            EquipmentDeletionService.ReservationHistoryMessage,
            EquipmentDeletionService.EnvironmentGroupMessage
        }, preview.BlockingReasons);
        var exception = await Assert.ThrowsAsync<EquipmentDeletionBlockedException>(() =>
            service.RequestAsync("EQ-BLOCKED", "writer"));
        Assert.Equal(2, exception.Reasons.Count);
        Assert.False(await verify.EquipmentDeletionJobs.AnyAsync());
    }

    [Fact]
    public async Task LocalPhotosAndNeverDispatchedDocumentsCompleteBeforeMasterDeletion()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            await SeedStorageAsync(factory, root);
            await SeedEquipmentFileAsync(factory, root, "EQ-LOCAL", "photo.jpg", false, false);
            await SeedEquipmentFileAsync(factory, root, "EQ-LOCAL", "manual.pdf", true, false);

            await using (var db = factory.CreateDbContext())
            {
                var result = await Service(db).RequestAsync("EQ-LOCAL", "writer");
                Assert.True(result.Completed);
                Assert.Equal(2, result.AttachmentCount);
            }
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-LOCAL"));
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.ApparatusId == "EQ-LOCAL"));
            Assert.False(await verify.KmIntegrationOutbox.AnyAsync());
            var job = await verify.EquipmentDeletionJobs.SingleAsync();
            Assert.Equal(EquipmentDeletionStatuses.Completed, job.Status);
            Assert.NotNull(job.DbRecordDeletedAt);
            Assert.NotNull(job.CompletedAt);
            Assert.False(Directory.Exists(await EquipmentFolderAsync(verify, "EQ-LOCAL")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MultipleAttachmentsWaitForMockKmCallbackBeforeMasterDeletion()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            await SeedStorageAsync(factory, root);
            await SeedEquipmentFileAsync(factory, root, "EQ-KM", "photo.jpg", false, false);
            var sentId = await SeedEquipmentFileAsync(factory, root, "EQ-KM", "formal.pdf", true, true);

            await using (var db = factory.CreateDbContext())
            {
                var requested = await Service(db).RequestAsync("EQ-KM", "writer");
                Assert.False(requested.Completed);
                Assert.Equal(EquipmentDeletionStatuses.WaitingForFiles, requested.Status);
            }
            await using (var waiting = factory.CreateDbContext())
            {
                Assert.True(await waiting.Apparatuses.AnyAsync(x => x.Id == "EQ-KM"));
                Assert.True(await waiting.ApparatusFiles.AnyAsync(x => x.Id == sentId));
            }

            var adapter = new MockEquipmentDeletionApi();
            Assert.Equal(1, await Dispatcher(factory, adapter).ProcessBatchAsync());
            var request = Assert.Single(adapter.Requests);
            var callback = Completed(request, adapter.JobId(request.DeletionRequestId));
            Assert.Equal(KmDeletionCallbackOutcome.Accepted,
                await new EquipmentFileDeletionCallbackService(factory).ApplyAsync(callback));

            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-KM"));
            Assert.False(await verify.ApparatusFiles.AnyAsync(x => x.ApparatusId == "EQ-KM"));
            Assert.Equal(EquipmentDeletionStatuses.Completed,
                (await verify.EquipmentDeletionJobs.SingleAsync()).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActiveDeletionRejectsNewEquipmentWritesAndDuplicateJobs()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            await SeedStorageAsync(factory, root);
            await SeedEquipmentFileAsync(factory, root, "EQ-FROZEN", "formal.pdf", true, true);
            await using (var db = factory.CreateDbContext())
            {
                var first = await Service(db).RequestAsync("EQ-FROZEN", "writer");
                var second = await Service(db).RequestAsync("EQ-FROZEN", "writer");
                Assert.Equal(first.RequestId, second.RequestId);
            }
            await using (var write = factory.CreateDbContext())
            {
                write.ApparatusFiles.Add(new ApparatusFile
                {
                    Id = Guid.NewGuid(), ApparatusId = "EQ-FROZEN", FileName = "late.pdf",
                    OriginalFileName = "late.pdf", FilePath = root, FileSize = 1,
                    CreatedAt = DateTime.UtcNow
                });
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
                Assert.Contains("正在執行刪除", ex.Message);
            }
            await using var verify = factory.CreateDbContext();
            Assert.Single(await verify.EquipmentDeletionJobs.ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedLocalFileCleanupIsRetriedBeforeMasterDeletion()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            await SeedStorageAsync(factory, root);
            await SeedEquipmentFileAsync(factory, root, "EQ-RETRY", "locked.pdf", false, false);
            string path;
            await using (var db = factory.CreateDbContext())
                path = await db.ApparatusFiles.Where(x => x.ApparatusId == "EQ-RETRY")
                    .Select(x => Path.Combine(x.FilePath, x.FileName)).SingleAsync();
            EquipmentDeletionRequestResult requested;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await using var db = factory.CreateDbContext();
                requested = await Service(db).RequestAsync("EQ-RETRY", "writer");
                Assert.False(requested.Completed);
                Assert.Equal(EquipmentFileDeletionStatuses.CleanupFailed,
                    (await db.EquipmentFileDeletionJobs.SingleAsync()).Status);
                Assert.Equal("FILE_IO_FAILED",
                    (await db.EquipmentFileDeletionJobs.SingleAsync()).LastErrorCode);
                Assert.True(await db.Apparatuses.AnyAsync(x => x.Id == "EQ-RETRY"));
            }

            await using (var retry = factory.CreateDbContext())
                Assert.Equal(1, await Service(retry).ProcessPendingAsync());
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-RETRY"));
            Assert.Equal(EquipmentDeletionStatuses.Completed,
                (await verify.EquipmentDeletionJobs.SingleAsync()).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ReadExecuteOnlyDirectoryIsAccessDeniedAndRetryCompletesAfterAclRestore()
    {
        if (!OperatingSystem.IsWindows()) return;

        var factory = new Factory();
        var root = NewRoot();
        DirectorySecurity? originalAcl = null;
        SecurityIdentifier? currentSid = null;
        string? folder = null;
        string? filePath = null;
        try
        {
            await SeedStorageAsync(factory, root);
            var fileId = await SeedEquipmentFileAsync(factory, root, "EQ-ACL", "restricted.pdf", false, false);
            await using (var db = factory.CreateDbContext())
                folder = await db.ApparatusFiles.Where(x => x.Id == fileId)
                    .Select(x => x.FilePath).SingleAsync();

            var directory = new DirectoryInfo(folder);
            originalAcl = directory.GetAccessControl(AccessControlSections.Access);
            filePath = Path.Combine(folder, "restricted.pdf");
            currentSid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Windows test identity has no SID.");
            var restrictedAcl = new DirectorySecurity();
            restrictedAcl.SetOwner(currentSid);
            restrictedAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            restrictedAcl.AddAccessRule(new FileSystemAccessRule(currentSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(restrictedAcl);

            await using (var denied = factory.CreateDbContext())
            {
                var requested = await Service(denied).RequestAsync("EQ-ACL", "writer");
                Assert.False(requested.Completed);
                var job = await denied.EquipmentFileDeletionJobs.SingleAsync();
                Assert.Equal(EquipmentFileDeletionStatuses.CleanupFailed, job.Status);
                Assert.Equal("FILE_ACCESS_DENIED", job.LastErrorCode);
                Assert.Equal("檔案存取遭拒。", job.LastErrorMessage);
                Assert.True(await denied.Apparatuses.AnyAsync(x => x.Id == "EQ-ACL"));
                Assert.True(await denied.ApparatusFiles.AnyAsync(x => x.Id == fileId));
                Assert.True(File.Exists(Path.Combine(folder, "restricted.pdf")));
            }

            directory.SetAccessControl(originalAcl);
            originalAcl = null;
            GrantDirectoryCleanupRights(folder, currentSid);
            GrantFileCleanupRights(filePath, currentSid);

            await using (var retry = factory.CreateDbContext())
                Assert.Equal(1, await Service(retry).ProcessPendingAsync());
            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-ACL"));
            Assert.Single(await verify.EquipmentFileDeletionJobs.ToListAsync());
            Assert.Equal(EquipmentDeletionStatuses.Completed,
                (await verify.EquipmentDeletionJobs.SingleAsync()).Status);
        }
        finally
        {
            if (originalAcl is not null && folder is not null && Directory.Exists(folder))
                new DirectoryInfo(folder).SetAccessControl(originalAcl);
            if (currentSid is not null && folder is not null && Directory.Exists(folder))
                GrantDirectoryCleanupRights(folder, currentSid);
            if (currentSid is not null && filePath is not null && File.Exists(filePath))
                GrantFileCleanupRights(filePath, currentSid);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantDirectoryCleanupRights(string path, SecurityIdentifier sid)
    {
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantFileCleanupRights(string path, SecurityIdentifier sid)
    {
        var acl = new FileSecurity();
        acl.SetOwner(sid);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    [Fact]
    public async Task PartialLocalCleanupFailureRetriesWithoutDeletingAnotherEquipment()
    {
        var factory = new Factory();
        var root = NewRoot();
        try
        {
            await SeedStorageAsync(factory, root);
            var blockedId = await SeedEquipmentFileAsync(factory, root, "EQ-PARTIAL", "blocked.pdf", false, false);
            var completedId = await SeedEquipmentFileAsync(factory, root, "EQ-PARTIAL", "completed.pdf", false, false);
            var otherId = await SeedEquipmentFileAsync(factory, root, "EQ-OTHER", "keep.pdf", false, false);
            string blockedPath;
            await using (var db = factory.CreateDbContext())
                blockedPath = await db.ApparatusFiles.Where(x => x.Id == blockedId)
                    .Select(x => Path.Combine(x.FilePath, x.FileName)).SingleAsync();

            using (File.Open(blockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await using var db = factory.CreateDbContext();
                var requested = await Service(db).RequestAsync("EQ-PARTIAL", "writer");
                Assert.False(requested.Completed);
            }

            await using (var waiting = factory.CreateDbContext())
            {
                Assert.True(await waiting.ApparatusFiles.AnyAsync(x => x.Id == blockedId));
                Assert.False(await waiting.ApparatusFiles.AnyAsync(x => x.Id == completedId));
                Assert.True(await waiting.ApparatusFiles.AnyAsync(x => x.Id == otherId));
                Assert.Equal(EquipmentFileDeletionStatuses.CleanupFailed,
                    (await waiting.EquipmentFileDeletionJobs.SingleAsync(x =>
                        x.ApparatusFileId == blockedId)).Status);
                Assert.Equal(EquipmentFileDeletionStatuses.Completed,
                    (await waiting.EquipmentFileDeletionJobs.SingleAsync(x =>
                        x.ApparatusFileId == completedId)).Status);
            }

            await using (var retry = factory.CreateDbContext())
                Assert.Equal(1, await Service(retry).ProcessPendingAsync());

            await using var verify = factory.CreateDbContext();
            Assert.False(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-PARTIAL"));
            Assert.True(await verify.Apparatuses.AnyAsync(x => x.Id == "EQ-OTHER"));
            Assert.True(await verify.ApparatusFiles.AnyAsync(x => x.Id == otherId));
            Assert.True(File.Exists(await verify.ApparatusFiles.Where(x => x.Id == otherId)
                .Select(x => Path.Combine(x.FilePath, x.FileName)).SingleAsync()));
        }
        finally { Directory.Delete(root, true); }
    }

    private static EquipmentDeletionService Service(AppDbContext db) =>
        new(db, new EquipmentFileDeletionService(db), new ManagedAssetFileStorage(db));

    private static KmDeletionDispatchService Dispatcher(Factory factory,
        IEquipmentDocumentDeletionAdapter adapter) => new(factory, new NoopCaseDeletionClient(),
        Options.Create(new KmDeletionOptions
        { Enabled = true, EquipmentEnabled = true, Scope = "mock.equipment.delete" }),
        Options.Create(new KmIntegrationOptions
        {
            BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 4
        }), adapter);

    private static async Task SeedStorageAsync(Factory factory, string root)
    {
        await using var db = factory.CreateDbContext();
        db.SystemOptions.Add(new SystemOption
        {
            Id = Guid.NewGuid(), Category = "AssetRootPath", Name = "equipment",
            Value = root, IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedEquipmentFileAsync(Factory factory, string root,
        string equipmentId, string name, bool withOutbox, bool sent)
    {
        await using var db = factory.CreateDbContext();
        var equipment = await db.Apparatuses.SingleOrDefaultAsync(x => x.Id == equipmentId);
        if (equipment is null)
        {
            equipment = Equipment(equipmentId, equipmentId);
            db.Apparatuses.Add(equipment);
            await db.SaveChangesAsync();
        }
        var folder = await EquipmentFolderAsync(db, equipmentId);
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(folder, name), "content-" + id);
        var file = new ApparatusFile
        {
            Id = id, ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = name, OriginalFileName = name, FilePath = folder,
            ContentType = name.EndsWith(".jpg") ? "image/jpeg" : "application/pdf",
            FileSize = new FileInfo(Path.Combine(folder, name)).Length,
            Sha256 = new string('a', 64), ArchiveStatus = FileArchiveStatuses.Archived,
            ArchiveUpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        db.ApparatusFiles.Add(file);
        if (withOutbox)
        {
            var outbox = KmIntegrationOutbox.ForArchivedEquipmentDocument(equipment, file, DateTime.UtcNow);
            db.KmIntegrationOutbox.Add(outbox);
            if (sent)
            {
                var lease = Guid.NewGuid();
                outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(5));
                outbox.Sent(lease, "INGEST-" + id.ToString("N"), DateTime.UtcNow);
            }
        }
        await db.SaveChangesAsync();
        return id;
    }

    private static Apparatus Equipment(string id, string? folder) => new()
    {
        Id = id, ModuleCode = "equipment", Name = id, ProductsId = "P-" + id,
        Kind = "Tester", StorageFolderName = folder,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static Task<string> EquipmentFolderAsync(AppDbContext db, string equipmentId) =>
        new ManagedAssetFileStorage(db).EquipmentFolderAsync(equipmentId);

    private static EquipmentDocumentDeletionCallback Completed(
        EquipmentDocumentDeletionRequest request, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new("CSIT.EquipmentDocumentDeletion", "draft-1", Guid.NewGuid(), "CSIT",
            request.DeletionRequestId, jobId, request.Resource, "Completed",
            new KmDeletionTimestamps(now, now.AddSeconds(2), null),
            new KmDeletionResult(true, now.AddSeconds(1),
                new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
                    "Deleted", "Deleted", "NotPresent")), null);
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "csit-equipment-master-delete-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MockEquipmentDeletionApi : IEquipmentDocumentDeletionAdapter
    {
        private readonly Dictionary<Guid, string> jobs = [];
        public List<EquipmentDocumentDeletionRequest> Requests { get; } = [];
        public string JobId(Guid requestId) => jobs[requestId];
        public Task<KmDeletionDeliveryResult> SubmitAsync(EquipmentDocumentDeletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var jobId = "KM-EQUIPMENT-" + request.DeletionRequestId.ToString("N");
            jobs[request.DeletionRequestId] = jobId;
            return Task.FromResult(KmDeletionDeliveryResult.Success(jobId));
        }
    }

    private sealed class NoopCaseDeletionClient : IKmDeletionClient
    {
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Verification deletion is out of scope.");
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }
}

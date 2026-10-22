using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;

namespace ApprovalFramework.Tests;

public sealed class DocumentLibraryTests
{
    [Fact]
    public void FolderLevel_IsLimitedToThree()
    {
        Assert.Equal(1, DocumentLibraryRules.LevelForParent(null));
        Assert.Equal(2, DocumentLibraryRules.LevelForParent(1));
        Assert.Equal(3, DocumentLibraryRules.LevelForParent(2));
        Assert.Throws<InvalidOperationException>(() => DocumentLibraryRules.LevelForParent(3));
    }

    [Fact]
    public void StandardFileName_IsStableSafeAndPreservesExtension()
    {
        var id = Guid.Parse("9f2d1e1f-cbc9-4423-b592-3dd949cdbf21");
        var result = DocumentLibraryRules.CreateStandardFileName(
            "原始 文件.PDF", new DateTime(2026, 10, 21, 2, 30, 45, 123, DateTimeKind.Utc), id);

        Assert.Equal("DOC-20261021023045123-9f2d1e1fcbc94423b5923dd949cdbf21.pdf", result);
    }

    [Fact]
    public void OriginalFileName_RemovesClientPath()
    {
        Assert.Equal("report.txt", DocumentLibraryRules.NormalizeOriginalFileName("C:\\fakepath\\report.txt"));
    }

    [Fact]
    public async Task Upload_WritesStagingFileAndPersistentPendingJob()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "csit-document-staging-tests", Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(Path.GetTempPath(), "csit-document-library-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new TestFactory();
            var folderId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.DocumentFolders.Add(new DocumentFolder
                {
                    Id = folderId,
                    Level = 3,
                    Name = "L3",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var service = new DocumentLibraryService(
                factory,
                Options.Create(new DocumentStorageOptions { RootPath = libraryRoot }),
                Options.Create(new DocumentStagingOptions { RootPath = stagingRoot }),
                new AllowWrite());
            await using var content = new MemoryStream("queued document"u8.ToArray());
            var file = new FormFile(content, 0, content.Length, "files", "original report.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            };

            var result = await service.UploadAsync(folderId, file, "patty");

            Assert.Equal(DocumentProcessingStatuses.Pending, result.ProcessingStatus);
            Assert.Equal("已接收／待 KM 處理", result.ProcessingStatusDisplay);
            await using var verify = await factory.CreateDbContextAsync();
            var stored = await verify.DocumentLibraryFiles.SingleAsync(x => x.Id == result.Id);
            var job = await verify.DocumentProcessingJobs.SingleAsync(x => x.DocumentFileId == result.Id);
            Assert.Equal(DocumentStorageAreas.Staging, stored.StorageArea);
            Assert.Equal(DocumentProcessingStatuses.Pending, job.Status);
            Assert.True(File.Exists(Path.Combine(stagingRoot, stored.RelativePath)));
            Assert.False(File.Exists(Path.Combine(libraryRoot, stored.RelativePath)));
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void QueueMigration_OnlyExtendsDocumentLibraryAndDisablesRequestedMenus()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "Migrations"),
            "*AddDocumentUploadQueue.cs").Single());
        Assert.Contains("document_processing_jobs", migration);
        Assert.Contains("storage_area", migration);
        Assert.Contains("'verification', 'certification', 'equipment', 'goods'", migration);
        Assert.DoesNotContain("DropTable(\n                name: \"document_folders\"", migration);
        Assert.DoesNotContain("DropTable(\n                name: \"document_files\"", migration);
    }

    [Fact]
    public async Task UpdateFolder_PersistsEditableFields()
    {
        var factory = new TestFactory();
        var folder = Folder(level: 1, name: "Before");
        await AddAsync(factory, folder);
        var service = CreateService(factory);

        var updated = await service.UpdateFolderAsync(folder.Id, new()
        {
            ParentId = null,
            Name = "After",
            Description = "Updated description",
            SortOrder = 42
        });

        Assert.True(updated);
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.DocumentFolders.SingleAsync(x => x.Id == folder.Id);
        Assert.Equal("After", stored.Name);
        Assert.Equal("Updated description", stored.Description);
        Assert.Equal(42, stored.SortOrder);
    }

    [Fact]
    public async Task MoveL2_PreservesDescendantsDocumentsJobsAndIdentifiers()
    {
        var factory = new TestFactory();
        var sourceL1 = Folder(1, "Source");
        var targetL1 = Folder(1, "Target");
        var l2 = Folder(2, "Movable", sourceL1.Id);
        var l3 = Folder(3, "Documents", l2.Id);
        var file = Document(l3.Id);
        var job = PendingJob(file.Id);
        await AddAsync(factory, sourceL1, targetL1, l2, l3, file, job);

        Assert.True(await CreateService(factory).UpdateFolderAsync(l2.Id, new()
        {
            ParentId = targetL1.Id, Name = l2.Name, Description = l2.Description, SortOrder = l2.SortOrder
        }));

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(targetL1.Id, (await db.DocumentFolders.SingleAsync(x => x.Id == l2.Id)).ParentId);
        Assert.Equal(l2.Id, (await db.DocumentFolders.SingleAsync(x => x.Id == l3.Id)).ParentId);
        Assert.Equal(l3.Id, (await db.DocumentLibraryFiles.SingleAsync(x => x.Id == file.Id)).FolderId);
        Assert.Equal(file.Id, (await db.DocumentProcessingJobs.SingleAsync(x => x.Id == job.Id)).DocumentFileId);
    }

    [Fact]
    public async Task MoveL3_PreservesDocumentMetadataAndJob()
    {
        var factory = new TestFactory();
        var l1 = Folder(1, "Root");
        var sourceL2 = Folder(2, "Source", l1.Id);
        var targetL2 = Folder(2, "Target", l1.Id);
        var l3 = Folder(3, "Movable", sourceL2.Id);
        var file = Document(l3.Id);
        var originalSha = file.Sha256;
        var originalPath = file.RelativePath;
        var job = PendingJob(file.Id);
        await AddAsync(factory, l1, sourceL2, targetL2, l3, file, job);

        await CreateService(factory).UpdateFolderAsync(l3.Id, new()
        {
            ParentId = targetL2.Id, Name = l3.Name, SortOrder = l3.SortOrder
        });

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(targetL2.Id, (await db.DocumentFolders.SingleAsync(x => x.Id == l3.Id)).ParentId);
        var storedFile = await db.DocumentLibraryFiles.SingleAsync(x => x.Id == file.Id);
        Assert.Equal(originalSha, storedFile.Sha256);
        Assert.Equal(originalPath, storedFile.RelativePath);
        Assert.Equal(file.Id, (await db.DocumentProcessingJobs.SingleAsync(x => x.Id == job.Id)).DocumentFileId);
    }

    [Fact]
    public async Task MoveFolder_RejectsRootMoveWrongLevelCycleAndDuplicateName()
    {
        var rootFactory = new TestFactory();
        var root = Folder(1, "Root");
        var otherRoot = Folder(1, "Other");
        await AddAsync(rootFactory, root, otherRoot);
        var rootError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(rootFactory).UpdateFolderAsync(root.Id, new() { ParentId = otherRoot.Id, Name = root.Name }));
        Assert.Contains("L1 必須維持根層", rootError.Message);

        var levelFactory = new TestFactory();
        var levelRoot = Folder(1, "Root");
        var l2 = Folder(2, "L2", levelRoot.Id);
        var otherL2 = Folder(2, "Other L2", levelRoot.Id);
        await AddAsync(levelFactory, levelRoot, l2, otherL2);
        var levelError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(levelFactory).UpdateFolderAsync(l2.Id, new() { ParentId = otherL2.Id, Name = l2.Name }));
        Assert.Contains("L2 只能移至 L1", levelError.Message);

        var duplicateFactory = new TestFactory();
        var source = Folder(1, "Source");
        var target = Folder(1, "Target");
        var moving = Folder(2, "Same", source.Id);
        var existing = Folder(2, "Same", target.Id);
        await AddAsync(duplicateFactory, source, target, moving, existing);
        var duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(duplicateFactory).UpdateFolderAsync(moving.Id, new() { ParentId = target.Id, Name = moving.Name }));
        Assert.Contains("相同名稱", duplicateError.Message);

        var cycleFactory = new TestFactory();
        var cycleFolder = Folder(2, "Cycle");
        var malformedDescendant = Folder(1, "Descendant", cycleFolder.Id);
        await AddAsync(cycleFactory, cycleFolder, malformedDescendant);
        var cycleError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(cycleFactory).UpdateFolderAsync(cycleFolder.Id,
                new() { ParentId = malformedDescendant.Id, Name = cycleFolder.Name }));
        Assert.Contains("循環", cycleError.Message);
    }

    [Fact]
    public async Task DeleteFolder_RemovesCompletelyEmptyFolder()
    {
        var factory = new TestFactory();
        var folder = Folder(level: 3);
        await AddAsync(factory, folder);

        Assert.True(await CreateService(factory).DeleteFolderAsync(folder.Id));
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.DocumentFolders.AnyAsync(x => x.Id == folder.Id));
    }

    [Fact]
    public async Task DeleteFolder_RejectsFolderWithChildren()
    {
        var factory = new TestFactory();
        var parent = Folder(level: 1);
        var child = Folder(level: 2, parentId: parent.Id);
        await AddAsync(factory, parent, child);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(factory).DeleteFolderAsync(parent.Id));

        Assert.Contains("仍有子資料夾", error.Message);
    }

    [Fact]
    public async Task DeleteFolder_RejectsFolderWithPendingDocument()
    {
        var factory = new TestFactory();
        var folder = Folder(level: 3);
        var file = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = folder.Id, OriginalFileName = "pending.txt",
            StandardFileName = "DOC-pending.txt", UploadedBy = "patty", UploadedAt = DateTime.UtcNow,
            FileSize = 1, Sha256 = new string('a', 64), RelativePath = "pending.txt",
            StorageArea = DocumentStorageAreas.Staging
        };
        var job = new DocumentProcessingJob
        {
            Id = Guid.NewGuid(), DocumentFileId = file.Id, Status = DocumentProcessingStatuses.Pending,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await AddAsync(factory, folder, file, job);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(factory).DeleteFolderAsync(folder.Id));

        Assert.Contains("包含待 KM 處理的文件", error.Message);
    }

    [Fact]
    public void DeleteFolderApi_RequiresAdministrationAndBusinessWrite()
    {
        var method = typeof(DocumentLibraryController).GetMethod(nameof(DocumentLibraryController.DeleteFolder))!;
        var authorize = method.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>();
        Assert.Contains(authorize, x => x.Policy == SystemAuthorization.Policies.Administration);
        Assert.Single(method.GetCustomAttributes(typeof(BusinessWriteAttribute), false));
    }

    [Fact]
    public async Task SystemDocuments_ReusesOnlyEquipmentAndApprovedFinalTestReportRecords()
    {
        var factory = new TestFactory();
        var equipment = new Apparatus { Id = "EQ-1", ModuleCode = "equipment", Name = "Equipment" };
        var goods = new Apparatus { Id = "GO-1", ModuleCode = "goods", Name = "Goods" };
        var equipmentFile = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = equipment.Id, Apparatus = equipment,
            FileName = "equipment.pdf", FilePath = "formal", FileSize = 123, CreatedAt = DateTime.UtcNow
        };
        var goodsFile = new ApparatusFile
        {
            Id = Guid.NewGuid(), ApparatusId = goods.Id, Apparatus = goods,
            FileName = "goods.pdf", FilePath = "formal", FileSize = 456, CreatedAt = DateTime.UtcNow
        };
        var approvedFinal = CaseFile("approved.pdf", TestReportReviewStatuses.Approved, isFinal: true);
        var pending = CaseFile("pending.pdf", TestReportReviewStatuses.Pending, isFinal: false);
        var oldApprovedVersion = CaseFile("old.pdf", TestReportReviewStatuses.Approved, isFinal: false);
        await AddAsync(factory, equipment, goods, equipmentFile, goodsFile, approvedFinal, pending, oldApprovedVersion);

        var catalog = await CreateService(factory).GetSystemDocumentsAsync();

        var verification = Assert.Single(catalog.Categories, x => x.Code == "verification-applications");
        Assert.Empty(verification.Files);
        Assert.Contains("尚無", verification.EmptyMessage);

        var equipmentCategory = Assert.Single(catalog.Categories, x => x.Code == "equipment");
        var listedEquipment = Assert.Single(equipmentCategory.Files);
        Assert.Equal("equipment.pdf", listedEquipment.FileName);
        Assert.Equal($"/api/assets/files/{equipmentFile.Id}/content", listedEquipment.DownloadUrl);

        var reports = Assert.Single(catalog.Categories, x => x.Code == "test-reports");
        var listedReport = Assert.Single(reports.Files);
        Assert.Equal("approved.pdf", listedReport.FileName);
        Assert.Equal($"/api/files/{approvedFinal.Id}/download", listedReport.DownloadUrl);
    }

    [Fact]
    public void SystemDocumentsApi_UsesExistingCsitStaffAuthorization()
    {
        var controllerPolicy = typeof(DocumentLibraryController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>();
        Assert.Contains(controllerPolicy, x => x.Policy == SystemAuthorization.Policies.CsitStaff);

        var method = typeof(DocumentLibraryController)
            .GetMethod(nameof(DocumentLibraryController.GetSystemDocuments))!;
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), false));
    }

    private static DocumentLibraryService CreateService(TestFactory factory) => new(
        factory,
        Options.Create(new DocumentStorageOptions { RootPath = Path.GetTempPath() }),
        Options.Create(new DocumentStagingOptions { RootPath = Path.GetTempPath() }),
        new AllowWrite());

    private static DocumentFolder Folder(int level, string name = "Folder", Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(), ParentId = parentId, Level = level, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static DocumentLibraryFile Document(Guid folderId) => new()
    {
        Id = Guid.NewGuid(), FolderId = folderId, OriginalFileName = "document.txt",
        StandardFileName = $"DOC-{Guid.NewGuid():N}.txt", UploadedBy = "patty", UploadedAt = DateTime.UtcNow,
        FileSize = 8, Sha256 = new string('b', 64), RelativePath = $"2026/10/{Guid.NewGuid():N}.txt",
        StorageArea = DocumentStorageAreas.Staging
    };

    private static DocumentProcessingJob PendingJob(Guid fileId) => new()
    {
        Id = Guid.NewGuid(), DocumentFileId = fileId, Status = DocumentProcessingStatuses.Pending,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static ModuleCaseFile CaseFile(string name, string reviewStatus, bool isFinal) => new()
    {
        Id = Guid.NewGuid(), RecordId = Guid.NewGuid(), CaseNo = "CASE-1", FileName = name,
        FilePath = "formal", FileSize = 100, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        DocumentId = Guid.NewGuid(), VersionNo = 1, FileKind = ModuleCaseFileKinds.TestReport,
        ReviewStatus = reviewStatus, IsFinal = isFinal
    };

    private static async Task AddAsync(TestFactory factory, params object[] entities)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static string FindRepositoryRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SIT.DepartmentSystem.Web.csproj")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}

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

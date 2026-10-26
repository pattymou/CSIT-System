using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace ApprovalFramework.Tests;

public sealed class VerificationApplicationAttachmentTests
{
    [Fact]
    public async Task DraftUpload_PersistsMultipleFiles_AndEnforcesApplicationAccess()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ATTACH-1", "MODEL-SHARED", "applicant");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            var service = Service(db, root);

            var first = await service.UploadAsync(application.Id, FormFile("prd.pdf", "first"), User("applicant", rd: true));
            var second = await service.UploadAsync(application.Id, FormFile("diagram.png", "second"), User("applicant", rd: true));

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, (await service.GetAsync(application.Id, User("applicant", rd: true))).Count);
            Assert.Equal(2, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.GetAsync(application.Id, User("other-rd", rd: true)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.GetAsync(application.Id, User("csit", csit: true)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Submit_KeepsAttachmentsInStaging_WithoutFormalFoldersOrLinks()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var firstApplication = Draft("VA-ATTACH-2", "MODEL-SHARED", "first");
            var secondApplication = Draft("VA-ATTACH-3", "MODEL-SHARED", "second");
            db.VerificationApplications.AddRange(firstApplication, secondApplication);
            await db.SaveChangesAsync();
            var service = Service(db, root);
            await service.UploadAsync(firstApplication.Id, FormFile("spec.docx", "spec"), User("first", rd: true));
            await service.UploadAsync(firstApplication.Id, FormFile("pre-test.xlsx", "report"), User("first", rd: true));
            await service.UploadAsync(secondApplication.Id, FormFile("same-name.docx", "other"), User("second", rd: true));

            await SubmitWithoutLinkAsync(db, firstApplication.Id);
            await SubmitWithoutLinkAsync(db, secondApplication.Id);

            var folders = await db.DocumentFolders.AsNoTracking().ToListAsync();
            Assert.Empty(folders);

            var documents = await db.DocumentLibraryFiles.AsNoTracking().ToListAsync();
            var links = await db.VerificationApplicationFiles.AsNoTracking().ToListAsync();
            Assert.Empty(documents);
            Assert.Empty(db.DocumentProcessingJobs);
            Assert.All(links, x => Assert.Null(x.DocumentLibraryFileId));
            Assert.Equal(2, links.Count(x => x.VerificationApplicationId == firstApplication.Id));
            Assert.Single(links, x => x.VerificationApplicationId == secondApplication.Id);
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);

            Assert.Equal(2, (await service.GetAsync(firstApplication.Id, User("csit", csit: true))).Count);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.OpenDownloadAsync(links.First().Id, User("unrelated", rd: true)));
            await using var download = (await service.OpenDownloadAsync(
                links.First().Id, User("csit", csit: true)))!.Stream;
            Assert.True(download.Length > 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReturnedApplicant_CanAddAndRemove_ButSubmittedApplicantCannot()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ATTACH-4", "MODEL-RETURN", "owner");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            var service = Service(db, root);
            var original = await service.UploadAsync(
                application.Id, FormFile("before.txt", "before"), User("owner", rd: true));
            await SubmitWithoutLinkAsync(db, application.Id);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
                application.Id, FormFile("blocked.txt", "blocked"), User("owner", rd: true)));

            db.ChangeTracker.Clear();
            var submitted = await db.VerificationApplications.SingleAsync(x => x.Id == application.Id);
            submitted.Return("leader", "needs more data", DateTime.UtcNow);
            await db.SaveChangesAsync();
            var added = await service.UploadAsync(
                application.Id, FormFile("after.txt", "after"), User("owner", rd: true));
            Assert.True(await service.DeleteAsync(application.Id, original.Id, User("owner", rd: true)));

            var remaining = await service.GetAsync(application.Id, User("owner", rd: true));
            Assert.Single(remaining);
            Assert.Equal(added.Id, remaining[0].Id);
            await SubmitWithoutLinkAsync(db, application.Id);
            Assert.Empty(await db.DocumentLibraryFiles.AsNoTracking().ToListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Upload_RejectsSameNormalizedName_WithoutRejectingSameContentOrOtherApplication()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var firstApplication = Draft("VA-DUPLICATE-1", "MODEL-SHARED", "first");
            var secondApplication = Draft("VA-DUPLICATE-2", "MODEL-SHARED", "second");
            db.VerificationApplications.AddRange(firstApplication, secondApplication);
            await db.SaveChangesAsync();
            var service = Service(db, root);

            await service.UploadAsync(
                firstApplication.Id, FormFile(" C:\\fakepath\\SPEC.pdf ", "same"), User("first", rd: true));

            var sameContent = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
                firstApplication.Id, FormFile("spec.PDF", "same"), User("first", rd: true)));
            Assert.Equal("檔案已存在：spec.PDF，請刪除舊檔或更名後再上傳。", sameContent.Message);

            var differentContent = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
                firstApplication.Id, FormFile("Spec.pdf", "different"), User("first", rd: true)));
            Assert.Equal("檔案已存在：Spec.pdf，請刪除舊檔或更名後再上傳。", differentContent.Message);

            await service.UploadAsync(
                firstApplication.Id, FormFile("renamed.pdf", "same"), User("first", rd: true));
            await service.UploadAsync(
                secondApplication.Id, FormFile("SPEC.pdf", "same"), User("second", rd: true));

            Assert.Equal(2, await db.VerificationApplicationFiles.CountAsync(
                x => x.VerificationApplicationId == firstApplication.Id));
            Assert.Single(await db.VerificationApplicationFiles.Where(
                x => x.VerificationApplicationId == secondApplication.Id).ToListAsync());
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DuplicateNameMigration_UsesApplicationScopedCaseInsensitiveUniqueIndex()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(
            root, "Migrations", "20261024090000_PreventDuplicateVerificationApplicationFileNames.cs"));

        Assert.Contains("CREATE UNIQUE INDEX", migration);
        Assert.Contains("verification_application_id, lower(file_name)", migration);
        Assert.DoesNotContain("sha256", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchUpload_ReportsDuplicateAndContinuesWithRemainingFiles()
    {
        var files = new FormFileCollection
        {
            FormFile("first.txt", "first"),
            FormFile("SPEC.pdf", "duplicate"),
            FormFile("last.txt", "last")
        };
        var controller = new VerificationApplicationFilesController(new BatchUploadService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Form = new FormCollection([], files);

        var response = Assert.IsType<OkObjectResult>(
            await controller.Upload(Guid.NewGuid(), CancellationToken.None));
        var batch = Assert.IsType<VerificationApplicationFileUploadBatchResultDto>(response.Value);

        Assert.Equal(3, batch.Results.Count);
        Assert.True(batch.Results[0].Success);
        Assert.False(batch.Results[1].Success);
        Assert.Equal("SPEC.pdf", batch.Results[1].FileName);
        Assert.Equal("檔案已存在：SPEC.pdf，請刪除舊檔或更名後再上傳。", batch.Results[1].Message);
        Assert.True(batch.Results[2].Success);
    }

    [Fact]
    public void DynamicAttachmentUi_UsesPersistentApplicationEndpoints()
    {
        var root = FindRepositoryRoot();
        var field = File.ReadAllText(Path.Combine(root, "Components", "Shared", "ValidationRequestFieldPreview.razor"));
        var component = File.ReadAllText(Path.Combine(root, "Components", "Shared", "ValidationApplicationAttachments.razor"));
        Assert.Contains("OTHER_PRODUCT_INFO", field);
        Assert.Contains("ValidationApplicationAttachments", field);
        Assert.DoesNotContain("附件上傳功能將於下一階段啟用", field);
        Assert.Contains("/api/verification-applications/", component);
        Assert.Contains("/api/verification-application-files/", component);
    }

    [Fact]
    public async Task DeleteDraft_OwnerRemovesDraftDatabaseRowsAndExclusivePhysicalFile()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ABANDON-1", "MODEL-ABANDON", "owner");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            await Service(db, root).UploadAsync(
                application.Id, FormFile("draft.txt", "draft"), User("owner", rd: true));
            var physicalPath = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Single();

            await ApplicationService(db, root).DeleteDraftAsync(application.Id, User("owner", rd: true));

            db.ChangeTracker.Clear();
            Assert.False(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.False(await db.VerificationApplicationFiles.AnyAsync(
                x => x.VerificationApplicationId == application.Id));
            Assert.False(File.Exists(physicalPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteDraft_RejectsNonOwnerWithoutChangingDraftOrAttachment()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ABANDON-2", "MODEL-ABANDON", "owner");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            await Service(db, root).UploadAsync(
                application.Id, FormFile("draft.txt", "draft"), User("owner", rd: true));
            var physicalPath = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Single();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ApplicationService(db, root).DeleteDraftAsync(application.Id, User("other", rd: true)));

            Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.True(await db.VerificationApplicationFiles.AnyAsync(
                x => x.VerificationApplicationId == application.Id));
            Assert.True(File.Exists(physicalPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteDraft_RejectsSubmittedApplicationWithoutChangingAttachment()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ABANDON-3", "MODEL-ABANDON", "owner");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            await Service(db, root).UploadAsync(
                application.Id, FormFile("draft.txt", "draft"), User("owner", rd: true));
            application.Submit(new VerificationApplicationRouting(
                application.RoutingDepartmentOptionId!.Value, "DA40", "DA40",
                application.TeamOptionId!.Value, "TEAM", "Team",
                "verification", "leader", "Leader"), DateTime.UtcNow);
            await db.SaveChangesAsync();
            var physicalPath = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Single();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ApplicationService(db, root).DeleteDraftAsync(application.Id, User("owner", rd: true)));

            Assert.Equal("只有草稿可以放棄。", error.Message);
            Assert.True(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.True(File.Exists(physicalPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteDraft_KeepsPhysicalFileReferencedByDocumentLibrary()
    {
        var root = TempRoot();
        try
        {
            await using var db = NewDb();
            var application = Draft("VA-ABANDON-4", "MODEL-ABANDON", "owner");
            db.VerificationApplications.Add(application);
            await db.SaveChangesAsync();
            var uploaded = await Service(db, root).UploadAsync(
                application.Id, FormFile("referenced.txt", "referenced"), User("owner", rd: true));
            var physicalPath = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Single();
            var link = await db.VerificationApplicationFiles.SingleAsync(x => x.Id == uploaded.Id);
            var folder = new DocumentFolder
            {
                Id = Guid.NewGuid(), Level = 3, Name = "Application documents",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            var document = new DocumentLibraryFile
            {
                Id = Guid.NewGuid(), FolderId = folder.Id, OriginalFileName = link.FileName,
                StandardFileName = link.FileName, UploadedBy = "owner", UploadedAt = DateTime.UtcNow,
                FileSize = link.FileSize, Sha256 = link.Sha256 ?? string.Empty,
                RelativePath = link.FilePath, StorageArea = DocumentStorageAreas.Staging
            };
            db.AddRange(folder, document);
            link.DocumentLibraryFileId = document.Id;
            await db.SaveChangesAsync();

            await ApplicationService(db, root).DeleteDraftAsync(application.Id, User("owner", rd: true));

            db.ChangeTracker.Clear();
            Assert.False(await db.VerificationApplications.AnyAsync(x => x.Id == application.Id));
            Assert.True(await db.DocumentLibraryFiles.AnyAsync(x => x.Id == document.Id));
            Assert.True(File.Exists(physicalPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AbandonDraftUi_RequiresConfirmationAndSupportsContinueEditing()
    {
        var page = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Components", "Pages", "ValidationRequests.razor"));

        Assert.Contains("確定放棄此申請單？草稿及其附件將被刪除，此操作無法復原。", page);
        Assert.Contains("CancelText=\"繼續編輯\"", page);
        Assert.Contains("ConfirmText=\"放棄申請\"", page);
        Assert.Contains("OnClose=\"ContinueEditing\"", page);
        Assert.Contains("await Applications.DeleteDraftAsync", page);
    }

    private static async Task SubmitWithoutLinkAsync(AppDbContext db, Guid applicationId)
    {
        db.ChangeTracker.Clear();
        var application = await db.VerificationApplications.Include(x => x.Files)
            .ThenInclude(x => x.DocumentLibraryFile)
            .SingleAsync(x => x.Id == applicationId);
        var now = DateTime.UtcNow;
        application.Submit(new VerificationApplicationRouting(
            application.RoutingDepartmentOptionId!.Value, "DA40", "DA40",
            application.TeamOptionId!.Value, "TEAM", "Team",
            "verification", "leader", "Leader"), now);
        await db.SaveChangesAsync();
    }

    private static VerificationApplication Draft(string applicationNo, string modelName, string applicant) =>
        VerificationApplication.CreateDraft(
            Guid.NewGuid(), applicationNo, Guid.NewGuid(), Guid.NewGuid(), applicant, applicant,
            $"{applicant}@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = modelName,
                ProductModel = modelName,
                Customer = "CUSTOMER",
                RequestedFinishDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                ValidationRequirement = "Requirement"
            }, DateTime.UtcNow);

    private static VerificationApplicationFileService Service(AppDbContext db, string root) => new(
        db,
        Options.Create(new DocumentStorageOptions { RootPath = Path.Combine(root, "library") }),
        Options.Create(new DocumentStagingOptions { RootPath = root }),
        new AllowWrite());

    private static VerificationApplicationService ApplicationService(AppDbContext db, string root) => new(
        db, null!, null!, new AllowWrite(), null!,
        Options.Create(new DocumentStagingOptions { RootPath = root }), null!, null!, null!);

    private static IFormFile FormFile(string name, string content)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "files", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };
    }

    private static ClaimsPrincipal User(string account, bool rd = false, bool csit = false)
    {
        var claims = new List<Claim> { new("account", account), new(ClaimTypes.NameIdentifier, account) };
        if (rd) claims.Add(new(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.RdApplicant));
        if (csit) claims.Add(new(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static string TempRoot() => Path.Combine(
        Path.GetTempPath(), "csit-verification-attachment-tests", Guid.NewGuid().ToString("N"));

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BatchUploadService : IVerificationApplicationFileService
    {
        public Task<VerificationApplicationFileDto> UploadAsync(
            Guid applicationId,
            IFormFile file,
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileName(file.FileName);
            if (name == "SPEC.pdf")
                throw new InvalidOperationException(
                    "檔案已存在：SPEC.pdf，請刪除舊檔或更名後再上傳。");
            return Task.FromResult(new VerificationApplicationFileDto { Id = Guid.NewGuid(), FileName = name });
        }

        public Task<IReadOnlyList<VerificationApplicationFileDto>> GetAsync(
            Guid applicationId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid applicationId, Guid fileId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentDownload?> OpenDownloadAsync(
            Guid fileId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

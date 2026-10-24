using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Implementations;

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
    public async Task Submit_LinksOnePhysicalCopyToFixedFolders_WithoutKmQueueOrCrossApplicationMixing()
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

            await SubmitAndLinkAsync(db, firstApplication.Id);
            await SubmitAndLinkAsync(db, secondApplication.Id);

            var folders = await db.DocumentFolders.AsNoTracking().ToListAsync();
            Assert.Single(folders, x => x.Level == 1 && x.Name == VerificationApplicationAttachmentFolders.Root);
            Assert.Single(folders, x => x.Level == 2 && x.Name == "MODEL-SHARED");
            var applicationFolder = Assert.Single(folders, x =>
                x.Level == 3 && x.Name == VerificationApplicationAttachmentFolders.ApplicationDocuments);
            Assert.DoesNotContain(folders, x => x.Level == 3 && x.Name is "產品資訊" or "測試報告");

            var documents = await db.DocumentLibraryFiles.AsNoTracking().ToListAsync();
            var links = await db.VerificationApplicationFiles.AsNoTracking().ToListAsync();
            Assert.Equal(3, documents.Count);
            Assert.All(documents, x => Assert.Equal(applicationFolder.Id, x.FolderId));
            Assert.All(documents, x => Assert.Equal(DocumentStorageAreas.Staging, x.StorageArea));
            Assert.Empty(db.DocumentProcessingJobs);
            Assert.All(links, x => Assert.NotNull(x.DocumentLibraryFileId));
            Assert.Equal(2, links.Count(x => x.VerificationApplicationId == firstApplication.Id));
            Assert.Single(links, x => x.VerificationApplicationId == secondApplication.Id);
            Assert.All(links, link => Assert.Equal(
                link.FilePath,
                documents.Single(x => x.Id == link.DocumentLibraryFileId).RelativePath));
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
            await SubmitAndLinkAsync(db, application.Id);

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
            await SubmitAndLinkAsync(db, application.Id);
            Assert.Single(await db.DocumentLibraryFiles.AsNoTracking().ToListAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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

    private static async Task SubmitAndLinkAsync(AppDbContext db, Guid applicationId)
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
        var linker = typeof(VerificationApplicationFileService).Assembly.GetType(
            "SIT.DepartmentSystem.Web.Services.VerificationApplicationAttachmentLinker")!;
        var method = linker.GetMethod("LinkAsync", BindingFlags.Public | BindingFlags.Static)!;
        await (Task)method.Invoke(null, [db, application, now, CancellationToken.None])!;
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

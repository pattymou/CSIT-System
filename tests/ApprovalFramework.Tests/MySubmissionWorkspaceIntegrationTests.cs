using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class MySubmissionWorkspaceIntegrationTests
{
    [Fact]
    public async Task PendingView_MergesOwnDocumentsAndSubmittedApplicationsInSubmissionOrder()
    {
        var factory = new InMemoryFactory();
        var now = DateTime.UtcNow;
        var olderApplication = Application("VA-OLDER", "Model Older", "engineer",
            VerificationApplicationStatus.Submitted, now.AddMinutes(-3));
        var document = Document("engineer", now.AddMinutes(-2));
        var newerApplication = Application("VA-NEWER", "Model Newer", "engineer",
            VerificationApplicationStatus.Submitted, now.AddMinutes(-1));
        var otherApplication = Application("VA-OTHER", "Other Model", "other-engineer",
            VerificationApplicationStatus.Submitted, now);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MenuItems.Add(Menu("documents", "文件送審"));
            db.PendingSubmissions.Add(document);
            db.VerificationApplications.AddRange(olderApplication, newerApplication, otherApplication);
            await db.SaveChangesAsync();
        }

        var service = Service(factory);
        var firstPage = await service.ListAsync(User("engineer"), MySubmissionView.Pending,
            page: 1, pageSize: 2);
        var secondPage = await service.ListAsync(User("engineer"), MySubmissionView.Pending,
            page: 2, pageSize: 2);

        Assert.Equal(3, firstPage.Counts.Pending);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal([newerApplication.Id, document.Id], firstPage.Items.Select(x => x.Id));
        Assert.Equal([olderApplication.Id], secondPage.Items.Select(x => x.Id));
        Assert.DoesNotContain(firstPage.Items.Concat(secondPage.Items), x => x.Id == otherApplication.Id);

        var documentItem = Assert.Single(firstPage.Items, x => x.Id == document.Id);
        Assert.Equal(MySubmissionSourceKind.ApprovalSubmission, documentItem.SourceKind);
        Assert.Equal("文件送審", documentItem.SourceTitle);
        Assert.Equal($"/api/supervisor-submissions/{document.Id}/file", documentItem.FileDownloadRoute);

        var applicationItems = firstPage.Items.Concat(secondPage.Items)
            .Where(x => x.SourceKind == MySubmissionSourceKind.VerificationApplication).ToList();
        Assert.Equal(2, applicationItems.Count);
        Assert.All(applicationItems, item =>
        {
            Assert.Equal("驗證申請", item.SourceTitle);
            Assert.Equal("驗證申請單", item.FileLabel);
            Assert.Equal("查看", item.PrimaryActionLabel);
            Assert.Null(item.FileDownloadRoute);
            Assert.StartsWith("/verification-applications/", item.ItemRoute);
        });
        Assert.Contains(applicationItems, x => x.ItemLabel == "VA-OLDER / Model Older");
        Assert.Contains(applicationItems, x => x.ItemLabel == "VA-NEWER / Model Newer");
    }

    [Fact]
    public async Task ReturnedView_UsesExistingEditRoutesAndExcludesRejectedOrOtherApplicants()
    {
        var factory = new InMemoryFactory();
        var now = DateTime.UtcNow;
        var rejectedDocument = Document("engineer", now.AddMinutes(-5));
        rejectedDocument.Reject("leader", "replace document", now.AddMinutes(-1));
        var dynamicReturned = Application("VA-DYNAMIC", "Dynamic Model", "engineer",
            VerificationApplicationStatus.Returned, now.AddMinutes(-4), dynamic: true,
            note: "補充動態欄位");
        var legacyReturned = Application("VA-LEGACY", "Legacy Model", "engineer",
            VerificationApplicationStatus.Returned, now.AddMinutes(-3), note: "補充舊版資料");
        var rejectedApplication = Application("VA-REJECTED", "Rejected Model", "engineer",
            VerificationApplicationStatus.Rejected, now.AddMinutes(-2), note: "拒絕，不可重送");
        var otherReturned = Application("VA-OTHER", "Other Model", "other-engineer",
            VerificationApplicationStatus.Returned, now.AddMinutes(-1), dynamic: true,
            note: "other");

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MenuItems.Add(Menu("documents", "文件送審"));
            db.PendingSubmissions.Add(rejectedDocument);
            db.VerificationApplications.AddRange(dynamicReturned, legacyReturned,
                rejectedApplication, otherReturned);
            await db.SaveChangesAsync();
        }

        var result = await Service(factory).ListAsync(User("engineer"), MySubmissionView.Returned);

        Assert.Equal(3, result.Counts.Returned);
        Assert.Equal(3, result.TotalCount);
        Assert.DoesNotContain(result.Items, x => x.Id == rejectedApplication.Id);
        Assert.DoesNotContain(result.Items, x => x.Id == otherReturned.Id);

        var dynamicItem = Assert.Single(result.Items, x => x.Id == dynamicReturned.Id);
        Assert.Equal($"/validation-requests/{dynamicReturned.Id}", dynamicItem.ItemRoute);
        Assert.Equal("返回修改", dynamicItem.PrimaryActionLabel);
        Assert.Equal("補充動態欄位", dynamicItem.ReturnReason);

        var legacyItem = Assert.Single(result.Items, x => x.Id == legacyReturned.Id);
        Assert.Equal($"/verification-applications/{legacyReturned.Id}", legacyItem.ItemRoute);
        Assert.Equal("查看", legacyItem.PrimaryActionLabel);
        Assert.Equal("補充舊版資料", legacyItem.ReturnReason);

        var documentItem = Assert.Single(result.Items, x => x.Id == rejectedDocument.Id);
        Assert.Equal(MySubmissionSourceKind.ApprovalSubmission, documentItem.SourceKind);
        Assert.Equal("重新上傳", documentItem.PrimaryActionLabel);
        Assert.Equal("replace document", documentItem.ReturnReason);
    }

    [Fact]
    public void Pages_KeepAttachmentOperationsAndSeparateVerificationFromDocumentMutations()
    {
        var root = FindRepositoryRoot();
        var attachments = File.ReadAllText(Path.Combine(root, "Components", "Shared",
            "ValidationApplicationAttachments.razor"));
        var attachmentCss = File.ReadAllText(Path.Combine(root, "Components", "Shared",
            "ValidationApplicationAttachments.razor.css"));
        var submissions = File.ReadAllText(Path.Combine(root, "Components", "Pages",
            "MySubmissions.razor"));

        Assert.Contains("class=\"attachment-action-row\"", attachments);
        Assert.Contains("/api/verification-application-files/{file.Id}/download", attachments);
        Assert.Contains("@if (CanManage)", attachments);
        Assert.Contains("DeleteAsync(file)", attachments);
        Assert.Contains("display: flex;", attachmentCss);
        Assert.Contains("align-items: center;", attachmentCss);
        Assert.Contains("gap: 7px;", attachmentCss);
        Assert.Contains("overflow-wrap: anywhere;", attachmentCss);
        Assert.Contains("@media (max-width: 760px)", attachmentCss);
        Assert.DoesNotContain("min-width: 980px", attachmentCss);

        Assert.Contains("MySubmissionQueryService Query", submissions);
        Assert.Contains("Query.ListAsync(user, _selectedView, _page, PageSize)", submissions);
        Assert.Contains("NextPageAsync", submissions);
        Assert.Contains("item.SourceKind == MySubmissionSourceKind.ApprovalSubmission", submissions);
        Assert.Contains("if (item.SourceKind != MySubmissionSourceKind.ApprovalSubmission) return;", submissions);
        Assert.Contains("@item.PrimaryActionLabel", submissions);
    }

    private static MySubmissionQueryService Service(IDbContextFactory<AppDbContext> factory) =>
        new(factory, NullLogger<MySubmissionQueryService>.Instance);

    private static PendingSubmission Document(string account, DateTime submittedAt) =>
        PendingSubmission.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), "documents", "document-test", account,
            "RD", Guid.NewGuid(), "TEAM", "Document", Guid.NewGuid().ToString("N"),
            "Document Item", new StagedFileMetadata("report.pdf", "application/pdf", 100,
                new string('A', 64), $"staging/{Guid.NewGuid():N}"), submittedAt);

    private static VerificationApplication Application(
        string number,
        string model,
        string applicant,
        VerificationApplicationStatus status,
        DateTime submittedAt,
        bool dynamic = false,
        string? note = null)
    {
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var application = VerificationApplication.CreateDraft(
            Guid.NewGuid(), number, departmentId, teamId, applicant, applicant,
            $"{applicant}@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = model,
                ProductModel = model,
                Customer = "Customer",
                RequestedFinishDate = DateOnly.FromDateTime(submittedAt.AddDays(7)),
                ValidationRequirement = "Validation"
            }, submittedAt.AddMinutes(-1));
        if (dynamic)
            application.SetValidationRequest(Guid.NewGuid(), 1, "WIFI_AP", "{}",
                submittedAt.AddSeconds(-30));
        application.Submit(new VerificationApplicationRouting(
            departmentId, "RD", "RD", teamId, "TEAM", "Team", "verification",
            "leader", "Leader"), submittedAt);
        if (status == VerificationApplicationStatus.Returned)
            application.Return("leader", note ?? "return", submittedAt.AddMinutes(1));
        else if (status == VerificationApplicationStatus.Rejected)
            application.Reject("leader", note ?? "reject", submittedAt.AddMinutes(1));
        else
            Assert.Equal(VerificationApplicationStatus.Submitted, status);
        return application;
    }

    private static MenuItem Menu(string code, string title) => new()
    {
        Id = Guid.NewGuid(), SectionId = Guid.NewGuid(), Code = code, Title = title,
        RoutePath = "/documents", IsEnabled = true, CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ClaimsPrincipal User(string account) => new(new ClaimsIdentity(
        [new Claim("account", account)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class InMemoryFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

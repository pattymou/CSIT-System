using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmTestReportCorrelationTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CompletedFormalReport_ReturnsExactNoStoreCorrelation()
    {
        var factory = new Factory();
        var seeded = await SeedAsync(factory);
        var controller = Controller(factory);

        var result = await controller.Get(seeded.EventId, seeded.DocumentId,
            seeded.Version, seeded.KmJobId!, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<KmV2TestReportCorrelationResponse>(ok.Value);
        Assert.Equal("2.0", response.SchemaVersion);
        Assert.Equal(seeded.SourceEntityId, response.SourceEntityId);
        Assert.Equal(seeded.EventId, response.EventId);
        Assert.Equal(seeded.Id, response.CsitJobId);
        Assert.Equal(Sha, response.SourceSha256);
        Assert.Equal("Completed", response.Status);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task CorrelationRejectsMismatchesNonCompletedAndDeletionHold()
    {
        var mismatchFactory = new Factory();
        var mismatch = await SeedAsync(mismatchFactory);
        Assert.IsType<NotFoundResult>(await Controller(mismatchFactory).Get(
            mismatch.EventId, mismatch.DocumentId, mismatch.Version, "wrong-job", default));

        var pendingFactory = new Factory();
        var pending = await SeedAsync(pendingFactory, completed: false);
        Assert.IsType<NotFoundResult>(await Controller(pendingFactory).Get(
            pending.EventId, pending.DocumentId, pending.Version, pending.KmJobId!, default));

        var heldFactory = new Factory();
        var held = await SeedAsync(heldFactory);
        await using (var db = heldFactory.CreateDbContext())
        {
            var deletion = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), held.ProjectId!.Value,
                "admin", "Approved deletion request", DateTime.UtcNow);
            deletion.MarkLocalReady(DateTime.UtcNow);
            db.VerificationCaseDeletionJobs.Add(deletion);
            await db.SaveChangesAsync();
        }
        Assert.IsType<NotFoundResult>(await Controller(heldFactory).Get(
            held.EventId, held.DocumentId, held.Version, held.KmJobId!, default));
    }

    [Fact]
    public void EndpointRequiresDocumentReadScopeAndExact7444Allowlist()
    {
        var controller = typeof(KmTestReportCorrelationController);
        Assert.Contains(controller.GetCustomAttributes<AuthorizeAttribute>(), x =>
            x.Policy == KmIntegrationAuthorization.DocumentReadPolicy);
        var cache = Assert.Single(controller.GetCustomAttributes<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);

        var allowed = Context("GET", $"/api/integrations/km/v2/test-report-correlations/{Guid.NewGuid():D}");
        Assert.True(DevKmServiceIngress.IsAllowed(allowed));
        Assert.False(DevKmServiceIngress.IsAllowed(Context("POST", allowed.Request.Path)));
        Assert.False(DevKmServiceIngress.IsAllowed(Context("GET",
            $"/api/integrations/km/v2/test-report-correlations/{Guid.NewGuid():D}/extra")));
    }

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = DevKmServiceIngress.Port;
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private static KmTestReportCorrelationController Controller(Factory factory)
    {
        var controller = new KmTestReportCorrelationController(factory)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static async Task<KmIntegrationOutbox> SeedAsync(Factory factory, bool completed = true)
    {
        var file = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = Guid.NewGuid(), CaseNo = "CASE-1",
            FileName = "report.pdf", FilePath = Path.GetTempPath(),
            ContentType = "application/pdf", FileSize = 10, UploadEmp = "tester",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            DocumentId = Guid.NewGuid(), VersionNo = 2,
            FileKind = ModuleCaseFileKinds.TestReport,
            ReviewStatus = TestReportReviewStatuses.Approved,
            ReviewedAt = DateTime.UtcNow, ReviewedBy = "leader",
            IsRawDataExported = true, Sha256 = Sha
        };
        var outbox = KmIntegrationOutbox.ForCommittedTestReport(file, DateTime.UtcNow);
        var lease = Guid.NewGuid();
        outbox.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
        outbox.Sent(lease, "KM-REPORT-2", DateTime.UtcNow);
        if (completed) outbox.CallbackCompleted(DateTime.UtcNow);
        await using var db = factory.CreateDbContext();
        db.ModuleCaseFiles.Add(file);
        db.KmIntegrationOutbox.Add(outbox);
        await db.SaveChangesAsync();
        return outbox;
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }
}

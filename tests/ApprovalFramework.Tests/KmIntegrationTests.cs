using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.KmIntegration;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class KmIntegrationTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CommittedReportCreatesMetadataOnlyPendingEvent()
    {
        var file = Report();
        var row = KmIntegrationOutbox.ForCommittedTestReport(file, Now);
        Assert.Equal("TestReportCommitted", row.EventType);
        Assert.Equal("CSIT", row.SourceSystem);
        Assert.Equal(file.DocumentId, row.DocumentId);
        Assert.Equal(file.VersionNo, row.Version);
        Assert.Equal(KmIntegrationStatus.Pending, row.Status);
        Assert.Null(row.KmJobId);
        Assert.Equal(file.FileSize, row.SizeBytes);
    }

    [Fact]
    public void ManualRetryReusesIdentityAndPreservesDeliveryHistory()
    {
        var row = KmIntegrationOutbox.ForCommittedTestReport(Report(), Now);
        var id = row.Id; var eventId = row.EventId; var documentId = row.DocumentId; var version = row.Version;
        Assert.False(row.CanManualRetry(Now));
        var lease = Guid.NewGuid(); row.Claim(lease, Now, TimeSpan.FromMinutes(1));
        Assert.False(row.CanManualRetry(Now));
        row.DeliveryFailed(lease, "HTTP_401", "KM rejected the ingestion request.", null, Now);
        Assert.True(row.CanManualRetry(Now));
        var audit = KmIntegrationManualRetryAudit.Create(row, "admin", "Authentication fixed", Now);
        row.RequeueManually(Now);
        Assert.Equal(KmIntegrationStatus.Pending, row.Status);
        Assert.Equal(id, row.Id); Assert.Equal(eventId, row.EventId);
        Assert.Equal(documentId, row.DocumentId); Assert.Equal(version, row.Version);
        Assert.Equal(0, row.RetryCount); Assert.Equal("HTTP_401", row.LastErrorCode);
        Assert.Equal("HTTP_401", audit.PreviousErrorCode);
        Assert.Equal("ManualKmSyncRetry", audit.Action);
        Assert.Throws<InvalidOperationException>(() => row.RequeueManually(Now));
        row.Claim(Guid.NewGuid(), Now.AddSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(1, row.RetryCount);
    }

    [Fact]
    public void AcceptedKmJobCannotBeManuallyRequeued()
    {
        var row = KmIntegrationOutbox.ForCommittedTestReport(Report(), Now);
        var lease = Guid.NewGuid(); row.Claim(lease, Now, TimeSpan.FromMinutes(1));
        row.Sent(lease, "job-1", Now); row.CallbackFailed("ERROR", "Failed at KM", Now);
        Assert.False(row.CanManualRetry(Now));
        Assert.Throws<InvalidOperationException>(() => row.RequeueManually(Now));
    }

    [Fact]
    public async Task ManualRetryServiceRequeuesOriginalAndAuditsWithoutChangingFormalReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "km-retry-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.pdf");
        await File.WriteAllBytesAsync(path, new byte[100]);
        try
        {
            var factory = Factory(); var file = Seed(factory, path);
            Guid id; Guid eventId;
            using (var db = factory.CreateDbContext())
            {
                var row = db.KmIntegrationOutbox.Single(); id = row.Id; eventId = row.EventId;
                var lease = Guid.NewGuid(); row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
                row.DeliveryFailed(lease, "HTTP_401", "KM rejected the ingestion request.", null, DateTime.UtcNow);
                var submission = PendingSubmission.Create(file.Id, "report-approval", "test-reports", "module",
                    "engineer", "CSIT", Guid.NewGuid(), "team", "TestReportVersion",
                    file.Id.ToString("N"), "report.pdf", null, DateTime.UtcNow);
                submission.Approve("supervisor", DateTime.UtcNow);
                var commitLease = Guid.NewGuid();
                submission.StartCommit(commitLease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
                submission.Commit(commitLease, file.Id.ToString("N"), DateTime.UtcNow);
                db.PendingSubmissions.Add(submission);
                db.SaveChanges();
            }
            var service = new KmManualRetryService(factory);
            const string reason = "KM ingestion number 契約已修正並通過 fresh-token safe contract probe，執行最終受控 DEV E2E 重試。";
            await service.RetryKmSyncAsync(file.Id, "admin", "  " + reason + "  ");
            using var check = factory.CreateDbContext();
            Assert.Single(check.KmIntegrationOutbox);
            var result = check.KmIntegrationOutbox.Single();
            Assert.Equal(id, result.Id); Assert.Equal(eventId, result.EventId);
            Assert.Equal(file.DocumentId, result.DocumentId); Assert.Equal(file.VersionNo, result.Version);
            Assert.Equal(KmIntegrationStatus.Pending, result.Status);
            Assert.Equal(TestReportReviewStatuses.Approved, check.ModuleCaseFiles.Single().ReviewStatus);
            Assert.Equal(ApprovalStatus.Committed, check.PendingSubmissions.Single().Status);
            var audit = Assert.Single(check.KmIntegrationManualRetryAudits);
            Assert.Equal("HTTP_401", audit.PreviousErrorCode);
            Assert.Equal(id, audit.OutboxId); Assert.Equal("admin", audit.RequestedBy);
            Assert.Equal(reason, audit.Reason);
            await Assert.ThrowsAsync<KmManualRetryConflictException>(() =>
                service.RetryKmSyncAsync(file.Id, "other-admin", "Retry again"));
            Assert.Single(check.KmIntegrationManualRetryAudits);
            var fakeKm = new StubKmClient(_ => KmDeliveryResult.Success("job-manual"));
            Assert.Equal(1, await Worker(factory, fakeKm).ProcessBatchAsync());
            Assert.Equal(eventId, fakeKm.LastRequest!.EventId);
            using var dispatched = factory.CreateDbContext();
            Assert.Single(dispatched.KmIntegrationOutbox);
            Assert.Equal(id, dispatched.KmIntegrationOutbox.Single().Id);
            Assert.Equal(KmIntegrationStatus.Sent, dispatched.KmIntegrationOutbox.Single().Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad")]
    [InlineData("Authorization: Bearer eyJ...")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.syntheticSignature123456")]
    [InlineData("client_secret=actual-secret-value")]
    [InlineData("password=actual-password-value")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.syntheticSignature123456")]
    [InlineData("Reason with\nnewline")]
    [InlineData("Reason with\u202eunsafe direction")]
    public async Task ManualRetryRequiresSafeReason(string reason)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new KmManualRetryService(Factory()).RetryKmSyncAsync(Guid.NewGuid(), "admin", reason));
    }

    [Fact]
    public async Task ManualRetryRejectsReasonLongerThan500Characters()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new KmManualRetryService(Factory()).RetryKmSyncAsync(Guid.NewGuid(), "admin", new string('x', 501)));
    }

    [Theory]
    [InlineData("KM token validation fixed.")]
    [InlineData("OAuth token issue resolved.")]
    [InlineData("Client credential authentication fixed.")]
    [InlineData("KM ingestion number 契約已修正並通過 fresh-token safe contract probe，執行最終受控 DEV E2E 重試。")]
    [InlineData("Secret rotation completed; retry after authentication fix.")]
    [InlineData("JWT verification fixed.")]
    [InlineData("Password rotation completed.")]
    public async Task ManualRetryAllowsOrdinaryTechnicalReason(string reason)
    {
        await Assert.ThrowsAsync<KmManualRetryDocumentException>(() =>
            new KmManualRetryService(Factory()).RetryKmSyncAsync(Guid.NewGuid(), "admin", reason));
    }

    [Fact]
    public async Task ManualRetryApiReturnsClear400ForCredentialValue()
    {
        var controller = new KmManualRetryController(new KmManualRetryService(Factory()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("account", "admin")], "test"));

        var response = await controller.Retry(Guid.NewGuid(),
            new KmManualRetryController.RetryRequest("client_secret=actual-secret-value"), default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Contains("重試原因不可包含實際密碼、Token 或其他憑證值。",
            badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value)!.ToString());
    }

    [Fact]
    public async Task ManualRetryRejectsMissingFormalFileWithoutRequeueOrAudit()
    {
        var factory = Factory();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.pdf");
        var file = Seed(factory, missing);
        using (var db = factory.CreateDbContext())
        {
            var row = db.KmIntegrationOutbox.Single();
            var lease = Guid.NewGuid(); row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            row.DeliveryFailed(lease, "HTTP_401", "Rejected", null, DateTime.UtcNow);
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<KmManualRetryDocumentException>(() =>
            new KmManualRetryService(factory).RetryKmSyncAsync(file.Id, "admin", "Fixed issue"));
        using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Failed, check.KmIntegrationOutbox.Single().Status);
        Assert.Empty(check.KmIntegrationManualRetryAudits);
    }

    [Fact]
    public async Task ManualRetryRejectsNonFormalReport()
    {
        var factory = Factory(); var file = Seed(factory);
        using (var db = factory.CreateDbContext())
        {
            var row = db.KmIntegrationOutbox.Single();
            var lease = Guid.NewGuid(); row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
            row.DeliveryFailed(lease, "HTTP_401", "Rejected", null, DateTime.UtcNow);
            db.ModuleCaseFiles.Single().ReviewStatus = TestReportReviewStatuses.Rejected;
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<KmManualRetryDocumentException>(() =>
            new KmManualRetryService(factory).RetryKmSyncAsync(file.Id, "admin", "Fixed issue"));
        using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Failed, check.KmIntegrationOutbox.Single().Status);
        Assert.Empty(check.KmIntegrationManualRetryAudits);
    }

    [Fact]
    public void ManualRetryEndpointRequiresAdministrationAndBusinessWrite()
    {
        var controller = typeof(KmManualRetryController);
        Assert.Contains(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>(), x => x.Policy == "Administration");
        var method = controller.GetMethod(nameof(KmManualRetryController.Retry))!;
        Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>(), x => x.Policy == "BusinessWrite");
    }

    [Fact]
    public async Task ManualRetryAuthorizationRequiresBothPolicies()
    {
        var factory = Factory();
        using (var db = factory.CreateDbContext())
        {
            db.TeamRoutings.Add(new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = Guid.NewGuid(),
                LeaderAccount = "leader", IsEnabled = true });
            db.SaveChanges();
        }
        async Task<bool> Allowed(string account, bool adminRole, bool editor)
        {
            var claims = new List<Claim> { new("account", account),
                new(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff) };
            if (adminRole) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            if (editor) claims.Add(new Claim(SystemAuthorization.DataAccessLevelClaim,
                SystemAuthorization.DataAccessLevels.Editor));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
            var context = new AuthorizationHandlerContext(
                [new AdministrationAccessRequirement(), new BusinessWriteRequirement()], principal, null);
            await new AdministrationAccessHandler(factory).HandleAsync(context);
            await new BusinessWriteHandler().HandleAsync(context);
            return context.HasSucceeded;
        }
        Assert.True(await Allowed("admin", true, true));
        Assert.False(await Allowed("leader", false, false));
        Assert.False(await Allowed("engineer", false, true));
        Assert.False(await Allowed("engineer", false, false));
    }

    [Fact]
    public void ManualRetryAuditIsAppendOnly()
    {
        var factory = Factory(); var row = KmIntegrationOutbox.ForCommittedTestReport(Report(), Now);
        var lease = Guid.NewGuid(); row.Claim(lease, Now, TimeSpan.FromMinutes(1));
        row.DeliveryFailed(lease, "HTTP_401", "Rejected", null, Now);
        using (var db = factory.CreateDbContext())
        {
            db.KmIntegrationManualRetryAudits.Add(
                KmIntegrationManualRetryAudit.Create(row, "admin", "Fixed issue", Now));
            db.SaveChanges();
        }
        using var check = factory.CreateDbContext();
        check.KmIntegrationManualRetryAudits.Remove(check.KmIntegrationManualRetryAudits.Single());
        Assert.Throws<InvalidOperationException>(() => check.SaveChanges());
    }

    [Theory]
    [InlineData("Attachment", "Approved", true)]
    [InlineData("TestReport", "Pending", true)]
    [InlineData("TestReport", "Rejected", true)]
    [InlineData("TestReport", "Approved", false)]
    public void UncommittedOrOtherUploadCannotEnqueue(string kind, string review, bool rawExported)
    {
        var file = Report();
        file.FileKind = kind;
        file.ReviewStatus = review;
        file.IsRawDataExported = rawExported;
        Assert.Throws<InvalidOperationException>(() => KmIntegrationOutbox.ForCommittedTestReport(file, Now));
    }

    [Fact]
    public void BusinessIdempotencyIsUniqueInDatabaseModel()
    {
        using var db = Factory().CreateDbContext();
        var index = db.Model.FindEntityType(typeof(KmIntegrationOutbox))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual([
                nameof(KmIntegrationOutbox.SourceSystem), nameof(KmIntegrationOutbox.DocumentId),
                nameof(KmIntegrationOutbox.Version)]));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void ALeaseCannotBeCompletedByAnotherWorker()
    {
        var row = KmIntegrationOutbox.ForCommittedTestReport(Report(), Now);
        var lease = Guid.NewGuid();
        row.Claim(lease, Now, TimeSpan.FromMinutes(10));
        Assert.Throws<InvalidOperationException>(() => row.Sent(Guid.NewGuid(), "job", Now));
        row.Sent(lease, "job", Now);
        Assert.Equal(KmIntegrationStatus.Sent, row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public void RetryableFailureIsClaimableOnlyWhenDue()
    {
        var row = KmIntegrationOutbox.ForCommittedTestReport(Report(), Now);
        var lease = Guid.NewGuid();
        row.Claim(lease, Now, TimeSpan.FromMinutes(10));
        row.DeliveryFailed(lease, "TIMEOUT", "timed out", Now.AddMinutes(1), Now);
        Assert.Throws<InvalidOperationException>(() => row.Claim(Guid.NewGuid(), Now, TimeSpan.FromMinutes(10)));
        row.Claim(Guid.NewGuid(), Now.AddMinutes(1), TimeSpan.FromMinutes(10));
        Assert.Equal(1, row.RetryCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public async Task HttpClientClassifiesResponses(HttpStatusCode status, bool retryable)
    {
        var client = Client(_ => new HttpResponseMessage(status));
        var result = await client.SubmitAsync(Request(), default);
        Assert.False(result.Accepted);
        Assert.Equal(retryable, result.Retryable);
    }

    [Fact]
    public async Task HttpClientAcceptsOnlyCorrelated202()
    {
        var request = Request();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                new KmIngestionResponse("job-1", "CSIT", request.DocumentId, request.Version, "Accepted")),
                Encoding.UTF8, "application/json")
        });
        var result = await client.SubmitAsync(request, default);
        Assert.True(result.Accepted);
        Assert.Equal("job-1", result.JobId);
    }

    [Fact]
    public async Task IngestionPostContainsMetadataAndNoFileOrStoragePath()
    {
        var request = Request();
        string? body = null;
        string? path = null;
        var client = Client(message =>
        {
            path = message.RequestUri?.AbsolutePath;
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    new KmIngestionResponse("job", "CSIT", request.DocumentId, 1, "Accepted")),
                    Encoding.UTF8, "application/json")
            };
        });
        Assert.True((await client.SubmitAsync(request, default)).Accepted);
        Assert.Equal(KmIntegrationClient.IngestionPath, path);
        using var json = System.Text.Json.JsonDocument.Parse(body!);
        Assert.True(json.RootElement.TryGetProperty("document", out var document));
        Assert.Equal("report.pdf", document.GetProperty("fileName").GetString());
        Assert.False(json.RootElement.TryGetProperty("filePath", out _));
        Assert.False(json.RootElement.TryGetProperty("contentBase64", out _));
    }

    [Fact]
    public async Task Invalid202ContractIsNotTreatedAsSent()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var result = await client.SubmitAsync(Request(), default);
        Assert.False(result.Accepted);
        Assert.Equal("INVALID_RESPONSE", result.ErrorCode);
    }

    [Fact]
    public async Task ConnectionFailureIsRetryable()
    {
        var client = Client(_ => throw new HttpRequestException("test connection failure"));
        var result = await client.SubmitAsync(Request(), default);
        Assert.True(result.Retryable);
        Assert.Equal("CONNECTION", result.ErrorCode);
    }

    [Fact]
    public async Task HttpTimeoutIsRetryable()
    {
        var options = Enabled();
        options.TimeoutSeconds = 1;
        var client = new KmIntegrationClient(new HttpClient(new TimeoutHttp()),
            Options.Create(options), new StubToken());
        var result = await client.SubmitAsync(Request(), default);
        Assert.True(result.Retryable);
        Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public async Task DisabledClientDoesNotCallHttp()
    {
        var calls = 0;
        var client = Client(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Accepted); },
            new KmIntegrationOptions { Enabled = false });
        var result = await client.SubmitAsync(Request(), default);
        Assert.Equal(0, calls);
        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task MissingClientCredentialsNeverCallsKm()
    {
        var calls = 0;
        var client = new KmIntegrationClient(new HttpClient(new StubHttp(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        })), Options.Create(Enabled()), new UnavailableKmIntegrationAccessTokenProvider());
        var result = await client.SubmitAsync(Request(), default);
        Assert.Equal("AUTH_NOT_CONFIGURED", result.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Worker202MarksSentAndNeverCompleted()
    {
        var factory = Factory();
        var file = Seed(factory);
        var fake = new StubKmClient(_ => KmDeliveryResult.Success("job-202"));
        var worker = Worker(factory, fake);
        Assert.Equal(1, await worker.ProcessBatchAsync());
        using var db = factory.CreateDbContext();
        var row = db.KmIntegrationOutbox.Single();
        Assert.Equal(KmIntegrationStatus.Sent, row.Status);
        Assert.Equal("job-202", row.KmJobId);
        Assert.NotNull(row.SentAt);
        Assert.Null(row.CompletedAt);
        Assert.Equal(file.DocumentId, fake.LastRequest!.DocumentId);
        Assert.Equal(0, await Worker(factory, fake).ProcessBatchAsync());
    }

    [Fact]
    public async Task WorkerRetryableFailureSchedulesAndPreservesFormalReport()
    {
        var factory = Factory();
        var file = Seed(factory);
        var worker = Worker(factory, new StubKmClient(_ =>
            KmDeliveryResult.Failure(true, "HTTP_500", "temporary")));
        Assert.Equal(1, await worker.ProcessBatchAsync());
        using var db = factory.CreateDbContext();
        var row = db.KmIntegrationOutbox.Single();
        Assert.Equal(KmIntegrationStatus.Failed, row.Status);
        Assert.True(row.NextRetryAt > DateTime.UtcNow);
        Assert.Equal(0, row.RetryCount);
        Assert.True(db.ModuleCaseFiles.Single(x => x.Id == file.Id).IsRawDataExported);
    }

    [Fact]
    public async Task WorkerNonRetryableFailureDoesNotSchedule()
    {
        var factory = Factory();
        Seed(factory);
        await Worker(factory, new StubKmClient(_ =>
            KmDeliveryResult.Failure(false, "HTTP_400", "invalid metadata"))).ProcessBatchAsync();
        using var db = factory.CreateDbContext();
        Assert.Null(db.KmIntegrationOutbox.Single().NextRetryAt);
    }

    [Fact]
    public async Task DiagnosticsExposePendingFailedRetriesAndLastAttempt()
    {
        var factory = Factory();
        Seed(factory);
        var before = await new KmSyncDiagnostics(factory).ReadAsync();
        Assert.Equal(1, before.PendingCount);
        await Worker(factory, new StubKmClient(_ =>
            KmDeliveryResult.Failure(false, "HTTP_400", "invalid"))).ProcessBatchAsync();
        var after = await new KmSyncDiagnostics(factory).ReadAsync();
        Assert.Equal(0, after.PendingCount);
        Assert.Equal(1, after.FailedCount);
        Assert.Equal(0, after.RetryCount);
        Assert.NotNull(after.LastAttemptAt);
        Assert.Equal("HTTP_400", after.LastErrorCode);
    }

    [Fact]
    public async Task DisabledWorkerPreservesPendingAcrossRestart()
    {
        var factory = Factory();
        Seed(factory);
        var disabled = new KmIntegrationOptions { Enabled = false };
        Assert.Equal(0, await Worker(factory, new StubKmClient(_ =>
            throw new InvalidOperationException("Must not be called")), disabled).ProcessBatchAsync());
        using var db = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Pending, db.KmIntegrationOutbox.Single().Status);
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredAfterWorkerRestart()
    {
        var factory = Factory();
        Seed(factory);
        using (var db = factory.CreateDbContext())
        {
            var row = db.KmIntegrationOutbox.Single();
            row.Claim(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(10));
            db.SaveChanges();
        }
        Assert.Equal(1, await Worker(factory, new StubKmClient(_ =>
            KmDeliveryResult.Success("recovered-job"))).ProcessBatchAsync());
        using var check = factory.CreateDbContext();
        Assert.Equal(1, check.KmIntegrationOutbox.Single().RetryCount);
        Assert.Equal(KmIntegrationStatus.Sent, check.KmIntegrationOutbox.Single().Status);
    }

    [Fact]
    public async Task DownloadReturnsOnlyCommittedDocumentVersion()
    {
        var factory = Factory();
        var path = Path.GetTempFileName();
        try
        {
            var file = Seed(factory, path);
            var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
            var result = Assert.IsType<FileStreamResult>(await controller.Download(file.DocumentId, 1, default));
            Assert.Equal(Path.GetFileName(path), result.FileDownloadName);
            await result.FileStream.DisposeAsync();
            Assert.IsType<NotFoundResult>(await controller.Download(Guid.NewGuid(), 1, default));
            Assert.IsType<NotFoundResult>(await controller.Download(file.DocumentId, 2, default));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DownloadHidesUncommittedAndStagingReport()
    {
        var factory = Factory();
        var file = Report();
        using (var db = factory.CreateDbContext())
        {
            db.ModuleCaseFiles.Add(file);
            db.SaveChanges();
        }
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<NotFoundResult>(await controller.Download(file.DocumentId, file.VersionNo, default));
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Withdrawn)]
    [InlineData(ApprovalStatus.ApprovedPendingCommit)]
    [InlineData(ApprovalStatus.CommitFailed)]
    public async Task DownloadRejectsEveryNonCommittedApprovalState(ApprovalStatus status)
    {
        var factory = Factory();
        var path = Path.GetTempFileName();
        try
        {
            var file = Seed(factory, path);
            var pending = PendingSubmission.Create(file.Id, file.Id.ToString("N"), "verification",
                "verification", "engineer", "DEV", Guid.NewGuid(), "TEAM",
                "TestReport", file.TaskId!.Value.ToString("N"), null, null, Now);
            if (status == ApprovalStatus.Rejected) pending.Reject("leader", "reason", Now);
            else if (status == ApprovalStatus.Withdrawn) pending.Withdraw(Now);
            else if (status is ApprovalStatus.ApprovedPendingCommit or ApprovalStatus.CommitFailed)
            {
                pending.Approve("leader", Now);
                if (status == ApprovalStatus.CommitFailed)
                {
                    var lease = Guid.NewGuid();
                    pending.StartCommit(lease, Now, TimeSpan.FromMinutes(10));
                    pending.FailCommit(lease, "safe error", Now);
                }
            }
            using (var db = factory.CreateDbContext())
            {
                db.PendingSubmissions.Add(pending);
                db.SaveChanges();
            }
            var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
            Assert.IsType<NotFoundResult>(await controller.Download(file.DocumentId, file.VersionNo, default));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Processing", KmIntegrationStatus.Processing)]
    [InlineData("Completed", KmIntegrationStatus.Completed)]
    [InlineData("Failed", KmIntegrationStatus.Failed)]
    public async Task CorrelatedCallbacksUpdateOnlyTheirEvent(string status, KmIntegrationStatus expected)
    {
        var factory = Factory();
        var file = Seed(factory);
        using (var db = factory.CreateDbContext())
        {
            var row = db.KmIntegrationOutbox.Single();
            var lease = Guid.NewGuid();
            row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(10));
            row.Sent(lease, "job-callback", DateTime.UtcNow);
            db.SaveChanges();
        }
        var callback = new KmStatusCallback("job-callback", "CSIT", file.DocumentId,
            file.VersionNo, status,
            new KmCallbackTimestamps(DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow),
            status == "Failed" ? new KmCallbackError("INDEX", "index failed") : null);
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<NoContentResult>(await controller.Status(callback, default));
        using var check = factory.CreateDbContext();
        Assert.Equal(expected, check.KmIntegrationOutbox.Single().Status);
        Assert.Equal(TestReportReviewStatuses.Approved,
            check.ModuleCaseFiles.Single(x => x.Id == file.Id).ReviewStatus);
    }

    [Theory]
    [InlineData("wrong-job", false, false)]
    [InlineData("job-callback", true, false)]
    [InlineData("job-callback", false, true)]
    public async Task WrongCallbackCorrelationIsRejected(string jobId, bool wrongDocument, bool wrongVersion)
    {
        var factory = Factory();
        var file = Seed(factory);
        using (var db = factory.CreateDbContext())
        {
            var row = db.KmIntegrationOutbox.Single();
            var lease = Guid.NewGuid();
            row.Claim(lease, DateTime.UtcNow, TimeSpan.FromMinutes(10));
            row.Sent(lease, "job-callback", DateTime.UtcNow);
            db.SaveChanges();
        }
        var callback = new KmStatusCallback(jobId, "CSIT", wrongDocument ? Guid.NewGuid() : file.DocumentId,
            wrongVersion ? 2 : file.VersionNo, "Completed",
            new KmCallbackTimestamps(null, DateTime.UtcNow, null), null);
        var controller = new KmIntegrationController(factory, NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<ConflictObjectResult>(await controller.Status(callback, default));
        using var check = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Sent, check.KmIntegrationOutbox.Single().Status);
    }

    [Fact]
    public async Task WrongSourceSystemCallbackIsRejected()
    {
        var controller = new KmIntegrationController(Factory(), NullLogger<KmIntegrationController>.Instance);
        var callback = new KmStatusCallback("job", "OTHER", Guid.NewGuid(), 1,
            "Completed", new KmCallbackTimestamps(null, DateTime.UtcNow, null), null);
        Assert.IsType<BadRequestObjectResult>(await controller.Status(callback, default));
    }

    [Fact]
    public void CallbackWireFormatContainsNestedTimestamps()
    {
        var callback = new KmStatusCallback("job", "CSIT", Guid.NewGuid(), 1,
            "Completed", new KmCallbackTimestamps(null, DateTime.UtcNow, null), null);
        var json = System.Text.Json.JsonSerializer.Serialize(callback,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("timestamps", out var timestamps));
        Assert.True(timestamps.TryGetProperty("completedAt", out _));
        Assert.False(document.RootElement.TryGetProperty("completedAt", out _));
    }

    [Fact]
    public async Task HumanCookieCannotMeetKmServicePolicy()
    {
        var requirement = new KmServicePrincipalRequirement(KmIntegrationAuthorization.DocumentReadScope);
        var handler = new KmServicePrincipalHandler(Options.Create(new KmIntegrationAuthOptions
            { ClientId = "km-csit-integration-dev", Audience = KmIntegrationAuthorization.Audience }));
        var human = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("client_id", "km-csit-integration-dev"),
            new Claim("scope", KmIntegrationAuthorization.DocumentReadScope)], "Cookies"));
        var context = new AuthorizationHandlerContext([requirement], human, null);
        await handler.HandleAsync(context);
        Assert.False(context.HasSucceeded);
        var service = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("client_id", "km-csit-integration-dev"),
            new Claim("sub", "km-csit-integration-dev"),
            new Claim("aud", KmIntegrationAuthorization.Audience),
            new Claim("principal_type", "service"),
            new Claim("scope", KmIntegrationAuthorization.DocumentReadScope)],
            KmIntegrationAuthorization.Scheme));
        var valid = new AuthorizationHandlerContext([requirement], service, null);
        await handler.HandleAsync(valid);
        Assert.True(valid.HasSucceeded);

        var callback = new AuthorizationHandlerContext(
            [new KmServicePrincipalRequirement(KmIntegrationAuthorization.StatusWriteScope)], service, null);
        await handler.HandleAsync(callback);
        Assert.False(callback.HasSucceeded);
    }

    private static ModuleCaseFile Report() => new()
    {
        Id = Guid.NewGuid(), DocumentId = Guid.NewGuid(), VersionNo = 1,
        RecordId = Guid.NewGuid(), CaseId = Guid.NewGuid(), TaskId = Guid.NewGuid(),
        FileKind = ModuleCaseFileKinds.TestReport, ReviewStatus = TestReportReviewStatuses.Approved,
        FileName = "report.pdf", ContentType = "application/pdf", FileSize = 100,
        IsRawDataExported = true, CreatedAt = Now, UpdatedAt = Now
    };

    private static StubFactory Factory() => new(Guid.NewGuid().ToString("N"));

    private static ModuleCaseFile Seed(StubFactory factory, string? path = null)
    {
        var file = Report();
        if (path is not null)
        {
            file.FilePath = Path.GetDirectoryName(path)!;
            file.FileName = Path.GetFileName(path);
        }
        using var db = factory.CreateDbContext();
        db.ModuleRecords.Add(new ModuleRecord { Id = file.RecordId, RecordNo = "P1", Name = "Project" });
        db.ModuleRecordCases.Add(new ModuleRecordCase { Id = file.CaseId!.Value,
            RecordId = file.RecordId, CaseNo = "C1", Name = "Case" });
        db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = file.TaskId!.Value,
            CaseId = file.CaseId.Value, TaskNo = "T1", Name = "Task" });
        db.ModuleCaseFiles.Add(file);
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(file, DateTime.UtcNow));
        db.SaveChanges();
        return file;
    }

    private static KmIntegrationOptions Enabled() => new()
    {
        Enabled = true, BaseUrl = "https://example.invalid", TimeoutSeconds = 2,
        MaxRetryCount = 3, RetryScheduleSeconds = [60, 300], PollSeconds = 1, LeaseSeconds = 30
    };

    private static KmSyncWorker Worker(StubFactory factory, IKmIntegrationClient client,
        KmIntegrationOptions? options = null) =>
        new(factory, client, Options.Create(options ?? Enabled()), NullLogger<KmSyncWorker>.Instance);

    private static KmIngestionRequest Request() => new(Guid.NewGuid(), "TestReportCommitted", "CSIT",
        Guid.NewGuid(), 1, new KmReference(Guid.NewGuid(), "P1", "Project"),
        new KmReference(Guid.NewGuid(), "C1", "Case"),
        new KmReference(Guid.NewGuid(), "T1", "Task"),
        new KmDocumentMetadata("report.pdf", "application/pdf", 100), DateTime.UtcNow);

    private static KmIntegrationClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler,
        KmIntegrationOptions? options = null) =>
        new(new HttpClient(new StubHttp(handler)), Options.Create(options ?? Enabled()), new StubToken());

    private sealed class StubFactory(string database) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StubKmClient(Func<KmIngestionRequest, KmDeliveryResult> reply) : IKmIntegrationClient
    {
        public KmIngestionRequest? LastRequest { get; private set; }
        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(reply(request));
        }
    }

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    private sealed class StubToken : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-token");
    }

    private sealed class TimeoutHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout test should cancel first.");
        }
    }
}

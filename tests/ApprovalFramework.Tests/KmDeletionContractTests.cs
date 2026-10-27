using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
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
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class KmDeletionContractTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResourceTypesUseAppendixBIdentifiersAndPreserveNullKeys()
    {
        var application = Guid.NewGuid(); var record = Guid.NewGuid();
        var file = Guid.NewGuid(); var document = Guid.NewGuid(); var source = Guid.NewGuid();
        var resources = new[]
        {
            new KmDeletionResource("DocumentLibraryFile", file, null, null, null, null, null),
            new KmDeletionResource("TestReport", null, document, 2, source, application, record),
            new KmDeletionResource("VerificationApplicationAttachment", null, file, 1, source,
                application, record)
        };
        Assert.All(resources, x => Assert.True(x.IsValid()));
        Assert.False((resources[1] with { Version = 0 }).IsValid());
        Assert.False((resources[1] with { ApplicationId = null }).IsValid());
        Assert.False((resources[2] with { Version = 2 }).IsValid());
        Assert.False((resources[0] with { DocumentId = document }).IsValid());
        var request = new KmDeletionRequest("1.0", "CSIT", "DocumentDeletionRequested",
            Guid.NewGuid(), new DateTimeOffset(Now), "VerificationApplicationDeleted", resources[1]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(json.RootElement.GetProperty("resource").TryGetProperty("documentFileId", out var empty));
        Assert.Equal(JsonValueKind.Null, empty.ValueKind);
        Assert.Equal(2, json.RootElement.GetProperty("resource").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task ClientUsesExactPathIdempotencyAndTreats202OnlyAsAccepted()
    {
        var request = Request();
        var handler = new RecordingHandler(message => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new KmDeletionAccepted(request.DeletionRequestId,
                "KM-DELETE-1", "Accepted"))
        });
        var client = Client(handler);
        var result = await client.SubmitAsync(request);
        Assert.True(result.Accepted);
        Assert.Equal("KM-DELETE-1", result.JobId);
        Assert.Equal(KmDeletionClient.Path, handler.Path);
        Assert.Equal(request.DeletionRequestId.ToString("D"), handler.IdempotencyKey);
        Assert.Equal("Bearer token", handler.Authorization);
        Assert.Contains("\"documentFileId\":null", handler.Body);

        var mismatched = Client(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new KmDeletionAccepted(Guid.NewGuid(), "other", "Accepted"))
        }));
        Assert.Equal("INVALID_RESPONSE", (await mismatched.SubmitAsync(request)).ErrorCode);
        var unavailable = Client(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.True((await unavailable.SubmitAsync(request)).Retryable);
        var disabled = Client(new RecordingHandler(_ => throw new Exception("must not send")), enabled: false);
        Assert.Equal("DISABLED", (await disabled.SubmitAsync(request)).ErrorCode);
    }

    [Fact]
    public async Task MockKmUsesFormalEndpointAndPreservesIdempotencyAcrossFailures()
    {
        await using var mock = await MockKmDeletionServer.StartAsync();
        var client = MockClient(mock.Client, timeoutSeconds: 1);
        var request = Request();

        var accepted = await client.SubmitAsync(request);
        var repeated = await client.SubmitAsync(request);
        Assert.True(accepted.Accepted);
        Assert.Equal(accepted.JobId, repeated.JobId);
        Assert.Single(mock.DeletionRequestIds);

        var conflict = await client.SubmitAsync(request with { ReasonCode = "AdministrativeCleanup" });
        Assert.Equal("HTTP_409", conflict.ErrorCode);
        Assert.False(conflict.Retryable);

        mock.Enqueue(MockKmDeliveryBehavior.ServiceUnavailable);
        var unavailable = await client.SubmitAsync(Request());
        Assert.Equal("HTTP_503", unavailable.ErrorCode);
        Assert.True(unavailable.Retryable);

        mock.Enqueue(MockKmDeliveryBehavior.Timeout);
        var timeout = await client.SubmitAsync(Request());
        Assert.Equal("TIMEOUT", timeout.ErrorCode);
        Assert.True(timeout.Retryable);

        mock.Enqueue(MockKmDeliveryBehavior.InvalidAcceptedResponse);
        Assert.Equal("INVALID_RESPONSE", (await client.SubmitAsync(Request())).ErrorCode);
    }

    [Fact]
    public async Task ControlledBatchDispatchRetriesPersistedTargetsAndStaysDisabledByDefault()
    {
        var factory = new Factory();
        var job = Job();
        job.AddTarget(new KmDeletionResource("TestReport", null, Guid.NewGuid(), 3,
            Guid.NewGuid(), job.ApplicationId, job.RecordId), Now);
        var prepared = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), Guid.NewGuid(),
            "leader", "Prepared deletion remains unfrozen", Now);
        prepared.AddTarget(new KmDeletionResource("TestReport", null, Guid.NewGuid(), 1,
            Guid.NewGuid(), prepared.ApplicationId, prepared.RecordId), Now);
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.AddRange(job, prepared); await db.SaveChangesAsync(); }
        await using var mock = await MockKmDeletionServer.StartAsync();
        var integration = Options.Create(new KmIntegrationOptions
        { BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 3 });
        var disabled = new KmDeletionDispatchService(factory, MockClient(mock.Client, 2),
            Options.Create(new KmDeletionOptions()), integration);
        Assert.Equal(0, await disabled.ProcessBatchAsync());
        Assert.Empty(mock.DeletionRequestIds);

        mock.Enqueue(MockKmDeliveryBehavior.ServiceUnavailable);
        var enabled = Options.Create(new KmDeletionOptions
        { Enabled = true, Scope = "csit.document.delete" });
        Assert.Equal(1, await new KmDeletionDispatchService(factory, MockClient(mock.Client, 2),
            enabled, integration).ProcessBatchAsync());
        Assert.Equal(1, await new KmDeletionDispatchService(factory, MockClient(mock.Client, 2),
            enabled, integration).ProcessBatchAsync());
        Assert.Equal(2, mock.DeletionRequestIds.Count);

        await using var verify = factory.CreateDbContext();
        var targets = await verify.VerificationCaseDeletionTargets
            .Where(x => x.JobId == job.Id).OrderBy(x => x.Id).ToListAsync();
        Assert.All(targets, target =>
        {
            Assert.Equal("Accepted", target.Status);
            Assert.NotNull(target.KmDeletionJobId);
        });
        Assert.Contains(targets, target => target.AttemptCount == 2);
        Assert.Contains(targets, target => target.AttemptCount == 1);
        Assert.Equal("Pending", (await verify.VerificationCaseDeletionTargets
            .SingleAsync(x => x.JobId == prepared.Id)).Status);
    }

    [Fact]
    public async Task MockCallbackEndpointRejectsInvalidCorrelationAndAcceptsCompletedBeforeProcessing()
    {
        var factory = new Factory();
        var job = Job();
        var targetId = job.Targets.Single().Id;
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.Add(job); await db.SaveChangesAsync(); }
        await using var mock = await MockKmDeletionServer.StartAsync();
        await using var callbackHost = await CsitDeletionCallbackTestServer.StartAsync(factory);
        var options = Options.Create(new KmIntegrationOptions
        { BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 3 });
        var enabled = Options.Create(new KmDeletionOptions
        { Enabled = true, Scope = "csit.document.delete" });
        Assert.Equal(1, await new KmDeletionDispatchService(factory, MockClient(mock.Client, 2),
            enabled, options).ProcessBatchAsync());

        var completed = mock.CreateCallback(targetId, "Completed");
        foreach (var invalid in new[]
        {
            completed with { Result = completed.Result! with { CleanupVerified = false } },
            completed with { Result = completed.Result! with
                { Cleanup = completed.Result.Cleanup with { VectorIndex = "Unknown" } } }
        })
        {
            using var response = await MockKmDeletionServer.SendCallbackAsync(
                callbackHost.Client, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var mismatch in new[]
        {
            completed with { CallbackId = Guid.NewGuid(), KmDeletionJobId = "forged-job" },
            completed with { CallbackId = Guid.NewGuid(), Resource = completed.Resource with
                { DocumentId = Guid.NewGuid() } }
        })
        {
            using var response = await MockKmDeletionServer.SendCallbackAsync(
                callbackHost.Client, mismatch);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        using (var unknown = await MockKmDeletionServer.SendCallbackAsync(callbackHost.Client,
            completed with { CallbackId = Guid.NewGuid(), DeletionRequestId = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using (var response = await MockKmDeletionServer.SendCallbackAsync(
            callbackHost.Client, completed))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var duplicate = await MockKmDeletionServer.SendCallbackAsync(
            callbackHost.Client, completed))
            Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        using (var late = await MockKmDeletionServer.SendCallbackAsync(callbackHost.Client,
            mock.CreateCallback(targetId, "Processing")))
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        using var unauthorized = await callbackHost.Client.PostAsJsonAsync(
            "/api/integrations/km/v1/document-deletion-status", completed);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task FailedCallbackRetriesSameRequestAcrossCsitAndMockKmRestart()
    {
        var factory = new Factory();
        var job = Job();
        var targetId = job.Targets.Single().Id;
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.Add(job); await db.SaveChangesAsync(); }
        var state = new MockKmDeletionState();
        var firstMock = await MockKmDeletionServer.StartAsync(state);
        await using var callbackHost = await CsitDeletionCallbackTestServer.StartAsync(factory);
        var options = Options.Create(new KmIntegrationOptions
        { BaseUrl = "https://km.mock.test/", TimeoutSeconds = 2, LeaseSeconds = 30,
            MaxRetryCount = 3 });
        var enabled = Options.Create(new KmDeletionOptions
        { Enabled = true, Scope = "csit.document.delete" });
        Assert.Equal(1, await new KmDeletionDispatchService(factory,
            MockClient(firstMock.Client, 2), enabled, options).ProcessBatchAsync());
        var failed = firstMock.CreateCallback(targetId, "Failed", retryable: true);
        using (var response = await MockKmDeletionServer.SendCallbackAsync(
            callbackHost.Client, failed))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        string originalJobId;
        await using (var pending = factory.CreateDbContext())
        {
            var target = await pending.VerificationCaseDeletionTargets.SingleAsync();
            Assert.Equal("Failed", target.Status);
            Assert.True(target.LastErrorRetryable);
            originalJobId = target.KmDeletionJobId!;
        }
        await firstMock.DisposeAsync();

        await using var restartedMock = await MockKmDeletionServer.StartAsync(state);
        Assert.Equal(1, await new KmDeletionDispatchService(factory,
            MockClient(restartedMock.Client, 2), enabled, options).ProcessBatchAsync());
        Assert.Equal(0, await new KmDeletionDispatchService(factory,
            MockClient(restartedMock.Client, 2), enabled, options).ProcessBatchAsync());
        await using var verify = factory.CreateDbContext();
        var retried = await verify.VerificationCaseDeletionTargets.SingleAsync();
        Assert.Equal(targetId, retried.Id);
        Assert.Equal(originalJobId, retried.KmDeletionJobId);
        Assert.Equal("Accepted", retried.Status);
        Assert.Equal(2, retried.AttemptCount);
        Assert.Single(state.Requests);
    }

    [Fact]
    public async Task CallbackRequiresExactIdsAndSevenVerifiedCleanupValuesAndIsIdempotent()
    {
        var factory = new Factory();
        var job = Job();
        var target = job.Targets.Single();
        var lease = target.ClaimDispatch(Now, TimeSpan.FromMinutes(2));
        target.DeliveryAccepted(lease, "KM-DELETE-1", Now);
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.Add(job); await db.SaveChangesAsync(); }
        var callbacks = new KmDeletionCallbackService(factory);
        var completed = Callback(target, "Completed", "KM-DELETE-1");
        Assert.Equal(KmDeletionCallbackOutcome.Invalid,
            await callbacks.ApplyAsync(completed with
            {
                Result = completed.Result! with
                { Cleanup = completed.Result.Cleanup with { VectorIndex = "Unknown" } }
            }));
        Assert.Equal(KmDeletionCallbackOutcome.Conflict,
            await callbacks.ApplyAsync(completed with { KmDeletionJobId = "forged" }));
        Assert.Equal(KmDeletionCallbackOutcome.Conflict,
            await callbacks.ApplyAsync(completed with
                { Resource = completed.Resource with { Version = 3 } }));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(completed));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(completed));
        Assert.Equal(KmDeletionCallbackOutcome.Conflict,
            await callbacks.ApplyAsync(completed with { Status = "Processing", Result = null }));
        Assert.Equal(KmDeletionCallbackOutcome.Conflict,
            await callbacks.ApplyAsync(Callback(target, "Processing", "KM-DELETE-1")));
        await using var verify = factory.CreateDbContext();
        Assert.Equal("KmVerified", (await verify.VerificationCaseDeletionJobs.SingleAsync()).Status);
        Assert.Equal("Completed", (await verify.VerificationCaseDeletionTargets.SingleAsync()).Status);
        Assert.Single(await verify.VerificationCaseDeletionCallbacks.ToListAsync());
    }

    [Fact]
    public async Task RetryAfterFailureAndServiceRestartReusesDeletionRequestId()
    {
        var factory = new Factory();
        var job = Job();
        var targetId = job.Targets.Single().Id;
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.Add(job); await db.SaveChangesAsync(); }
        var client = new SequenceClient();
        var options = Options.Create(new KmIntegrationOptions
        { TimeoutSeconds = 10, LeaseSeconds = 60, MaxRetryCount = 3 });
        Assert.False(await new KmDeletionDispatchService(factory, client,
            Options.Create(new KmDeletionOptions { Enabled = true }), options).DispatchAsync(targetId));
        Assert.True(await new KmDeletionDispatchService(factory, client,
            Options.Create(new KmDeletionOptions { Enabled = true }), options).DispatchAsync(targetId));
        Assert.Equal([targetId, targetId], client.RequestIds);
        await using var verify = factory.CreateDbContext();
        var target = await verify.VerificationCaseDeletionTargets.SingleAsync();
        Assert.Equal("Accepted", target.Status);
        Assert.Equal("KM-DELETE-1", target.KmDeletionJobId);
        Assert.Equal(2, target.AttemptCount);
        Assert.Equal("WaitingForKm", (await verify.VerificationCaseDeletionJobs.SingleAsync()).Status);
    }

    [Fact]
    public void FailedCallbackRetriesSameJobAndCannotChangeResource()
    {
        var job = Job();
        var target = job.Targets.Single();
        var lease = target.ClaimDispatch(Now, TimeSpan.FromMinutes(2));
        target.DeliveryAccepted(lease, "KM-DELETE-1", Now);
        var failed = new KmDeletionCallback("1.0", Guid.NewGuid(), "CSIT", target.Id,
            "KM-DELETE-1", target.Resource, "Failed",
            new KmDeletionTimestamps(new DateTimeOffset(Now), null,
                new DateTimeOffset(Now.AddSeconds(1))), null,
            new KmDeletionError("INDEX_TEMPORARY", "Temporary failure", true));
        target.ApplyCallback(failed, Now.AddSeconds(1));
        Assert.Equal("Failed", target.Status);
        lease = target.ClaimDispatch(Now.AddSeconds(2), TimeSpan.FromMinutes(2));
        Assert.Equal(target.Id, target.ToRequest().DeletionRequestId);
        target.DeliveryAccepted(lease, "KM-DELETE-1", Now.AddSeconds(2));
        Assert.Throws<InvalidOperationException>(() => target.ClaimDispatch(Now.AddSeconds(3),
            TimeSpan.FromMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => target.ApplyCallback(
            failed with { CallbackId = Guid.NewGuid(), Resource = failed.Resource with
                { DocumentId = Guid.NewGuid() } }, Now.AddSeconds(4)));
    }

    [Fact]
    public async Task ProcessingAndFailedCallbacksPersistWithoutCompletingCase()
    {
        var factory = new Factory();
        var job = Job();
        var target = job.Targets.Single();
        var lease = target.ClaimDispatch(Now, TimeSpan.FromMinutes(2));
        target.DeliveryAccepted(lease, "KM-DELETE-1", Now);
        await using (var db = factory.CreateDbContext())
        { db.VerificationCaseDeletionJobs.Add(job); await db.SaveChangesAsync(); }
        var callbacks = new KmDeletionCallbackService(factory);
        var processing = Callback(target, "Processing", "KM-DELETE-1");
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(processing));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(processing));
        var failed = new KmDeletionCallback("1.0", Guid.NewGuid(), "CSIT", target.Id,
            "KM-DELETE-1", target.Resource, "Failed",
            new KmDeletionTimestamps(new DateTimeOffset(Now), null,
                new DateTimeOffset(Now.AddSeconds(10))), null,
            new KmDeletionError("GRAPH_RETRY", "Temporary graph failure", true));
        Assert.Equal(KmDeletionCallbackOutcome.Accepted, await callbacks.ApplyAsync(failed));
        await using var verify = factory.CreateDbContext();
        Assert.Equal("Failed", (await verify.VerificationCaseDeletionTargets.SingleAsync()).Status);
        Assert.Equal("WaitingForKm", (await verify.VerificationCaseDeletionJobs.SingleAsync()).Status);
        Assert.Equal(2, await verify.VerificationCaseDeletionCallbacks.CountAsync());
    }

    [Fact]
    public void ResourceFactoryRejectsSameModelDifferentApplication()
    {
        var first = AcceptedApplication("VA-A", Guid.NewGuid());
        var second = AcceptedApplication("VA-B", Guid.NewGuid());
        var report = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = first.ModuleRecordId!.Value,
            FileKind = ModuleCaseFileKinds.TestReport, DocumentId = Guid.NewGuid(),
            VersionNo = 2, FileName = "report.pdf"
        };
        var resource = KmDeletionResourceFactory.ForTestReport(first, report);
        Assert.Equal(first.Id, resource.ApplicationId);
        Assert.Equal(report.Id, resource.SourceEntityId);
        Assert.Equal(report.DocumentId, resource.DocumentId);
        Assert.Equal(2, resource.Version);
        Assert.Throws<InvalidOperationException>(() => KmDeletionResourceFactory.ForTestReport(second,
            report));

        var document = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = Guid.NewGuid(), OriginalFileName = "attachment.pdf",
            StandardFileName = "standard.pdf", RelativePath = "files/standard.pdf",
            StorageArea = DocumentStorageAreas.Library
        };
        var attachment = new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = first.Id,
            FileName = "attachment.pdf", FilePath = "staging/attachment.pdf",
            DocumentLibraryFileId = document.Id, DocumentLibraryFile = document
        };
        var archived = KmDeletionResourceFactory.ForArchivedAttachment(first, attachment);
        Assert.Equal(document.Id, archived.DocumentId);
        Assert.Equal(1, archived.Version);
        Assert.Equal(attachment.Id, archived.SourceEntityId);
        Assert.Throws<InvalidOperationException>(() => KmDeletionResourceFactory.ForArchivedAttachment(
            second, attachment));
        Assert.Equal(document.Id, KmDeletionResourceFactory.ForDocumentLibraryFile(document).DocumentFileId);
    }

    [Fact]
    public async Task DeletionCallbackRequiresSeparateConfiguredServiceScope()
    {
        const string deletionScope = "configured.deletion.callback.scope";
        var handler = new KmServicePrincipalHandler(Options.Create(new KmIntegrationAuthOptions
            { ClientId = "km-csit-integration-dev", Audience = KmIntegrationAuthorization.Audience }));
        ClaimsPrincipal Principal(string scope) => new(new ClaimsIdentity([
            new Claim("client_id", "km-csit-integration-dev"),
            new Claim("sub", "km-csit-integration-dev"),
            new Claim("aud", KmIntegrationAuthorization.Audience),
            new Claim("principal_type", "service"), new Claim("scope", scope)
        ], KmIntegrationAuthorization.Scheme));
        var denied = new AuthorizationHandlerContext(
            [new KmServicePrincipalRequirement(deletionScope)],
            Principal(KmIntegrationAuthorization.StatusWriteScope), null);
        await handler.HandleAsync(denied);
        Assert.False(denied.HasSucceeded);
        var allowed = new AuthorizationHandlerContext(
            [new KmServicePrincipalRequirement(deletionScope)], Principal(deletionScope), null);
        await handler.HandleAsync(allowed);
        Assert.True(allowed.HasSucceeded);
    }

    [Fact]
    public async Task ActiveDeletionStopsOldKmDownloadAndPendingIngestionDispatch()
    {
        var factory = new Factory();
        var record = Guid.NewGuid();
        var file = new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = record, CaseId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), VersionNo = 1,
            FileKind = ModuleCaseFileKinds.TestReport,
            ReviewStatus = TestReportReviewStatuses.Approved,
            FileName = "report.pdf", FilePath = Path.GetTempPath(), FileSize = 12,
            IsRawDataExported = true
        };
        var job = VerificationCaseDeletionJob.Prepare(Guid.NewGuid(), record, "leader",
            "Case deletion under review", Now);
        job.AddTarget(new KmDeletionResource("TestReport", null, file.DocumentId, 1,
            file.Id, job.ApplicationId, record), Now);
        job.MarkWaitingForKm(Now);
        await using (var db = factory.CreateDbContext())
        {
            db.ModuleCaseFiles.Add(file);
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForCommittedTestReport(file, Now));
            db.VerificationCaseDeletionJobs.Add(job);
            await db.SaveChangesAsync();
        }
        var controller = new KmIntegrationController(factory,
            NullLogger<KmIntegrationController>.Instance);
        Assert.IsType<NotFoundResult>(await controller.Download(file.DocumentId, 1, default));
        var worker = new KmSyncWorker(factory, new ThrowIngestionClient(),
            Options.Create(new KmIntegrationOptions
            {
                Enabled = true, TimeoutSeconds = 10, LeaseSeconds = 60,
                PollSeconds = 10, MaxRetryCount = 3
            }), NullLogger<KmSyncWorker>.Instance);
        Assert.Equal(0, await worker.ProcessBatchAsync());
        await using var verify = factory.CreateDbContext();
        Assert.Equal(KmIntegrationStatus.Pending, (await verify.KmIntegrationOutbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReviewPreparationPersistsExactTargetsWithoutActivatingDeletion()
    {
        await using var db = new Factory().CreateDbContext();
        var recordId = Guid.NewGuid();
        var otherRecordId = Guid.NewGuid();
        var application = AcceptedApplication("VA-A", recordId);
        var other = AcceptedApplication("VA-B", otherRecordId);
        db.VerificationApplications.AddRange(application, other);
        db.ModuleRecords.AddRange(
            new ModuleRecord { Id = recordId, ModuleId = Guid.NewGuid(), Name = "Same model",
                RecordNo = "REC-A", Status = "Open" },
            new ModuleRecord { Id = otherRecordId, ModuleId = Guid.NewGuid(), Name = "Same model",
                RecordNo = "REC-B", Status = "Open" });
        var documentId = Guid.NewGuid();
        for (var version = 1; version <= 2; version++)
            db.ModuleCaseFiles.Add(new ModuleCaseFile
            {
                Id = Guid.NewGuid(), RecordId = recordId, DocumentId = documentId,
                VersionNo = version, FileKind = ModuleCaseFileKinds.TestReport,
                ReviewStatus = TestReportReviewStatuses.Approved,
                IsRawDataExported = true, FileName = "report.pdf"
            });
        db.ModuleCaseFiles.Add(new ModuleCaseFile
        {
            Id = Guid.NewGuid(), RecordId = otherRecordId, DocumentId = Guid.NewGuid(),
            VersionNo = 1, FileKind = ModuleCaseFileKinds.TestReport,
            ReviewStatus = TestReportReviewStatuses.Approved,
            IsRawDataExported = true, FileName = "other.pdf"
        });
        var document = new DocumentLibraryFile
        {
            Id = Guid.NewGuid(), FolderId = Guid.NewGuid(), OriginalFileName = "application.pdf",
            StandardFileName = "application-standard.pdf", RelativePath = "library/file.pdf",
            StorageArea = DocumentStorageAreas.Library
        };
        var attachment = new VerificationApplicationFile
        {
            Id = Guid.NewGuid(), VerificationApplicationId = application.Id,
            FileName = "application.pdf", FilePath = "staging/file.pdf",
            DocumentLibraryFileId = document.Id, DocumentLibraryFile = document
        };
        db.VerificationApplicationFiles.Add(attachment);
        await db.SaveChangesAsync();
        var service = new VerificationCaseDeletionPreparationService(db, new AllowWrite());
        var job = await service.PrepareReviewAsync(recordId, Manager(), "Case deletion review");
        Assert.Equal("Prepared", job.Status);
        Assert.Equal(3, job.Targets.Count);
        Assert.Equal([1, 2], job.Targets.Where(x => x.SourceType == "TestReport")
            .Select(x => x.Version!.Value).OrderBy(x => x).ToArray());
        Assert.Contains(job.Targets, x => x.SourceType == "VerificationApplicationAttachment" &&
            x.DocumentId == document.Id && x.SourceEntityId == attachment.Id);
        Assert.All(job.Targets, x => Assert.Equal(application.Id, x.ApplicationId));
        var second = await service.PrepareReviewAsync(recordId, Manager(), "Case deletion review");
        Assert.Equal(job.Id, second.Id);
        Assert.Equal(job.Targets.Select(x => x.Id).OrderBy(x => x),
            second.Targets.Select(x => x.Id).OrderBy(x => x));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareReviewAsync(
            otherRecordId, Manager("staff"), "Case deletion review"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new VerificationCaseDeletionPreparationService(db, new DenyWrite())
                .PrepareReviewAsync(otherRecordId, Manager(), "Case deletion review"));
        Assert.Single(await db.VerificationCaseDeletionJobs.ToListAsync());
    }

    private static KmDeletionRequest Request() => new("1.0", "CSIT", "DocumentDeletionRequested",
        Guid.NewGuid(), new DateTimeOffset(Now), "VerificationApplicationDeleted",
        new KmDeletionResource("TestReport", null, Guid.NewGuid(), 2, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid()));

    private static VerificationCaseDeletionJob Job()
    {
        var application = Guid.NewGuid(); var record = Guid.NewGuid();
        var job = VerificationCaseDeletionJob.Prepare(application, record, "leader",
            "Delete exact accepted application", Now);
        job.AddTarget(new KmDeletionResource("TestReport", null, Guid.NewGuid(), 2,
            Guid.NewGuid(), application, record), Now);
        job.MarkWaitingForKm(Now);
        return job;
    }

    private static VerificationApplication AcceptedApplication(string number, Guid recordId)
    {
        var department = Guid.NewGuid(); var team = Guid.NewGuid();
        var application = VerificationApplication.CreateDraft(Guid.NewGuid(), number, department,
            team, "applicant", "Applicant", "a@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "Same model", ProductModel = "Same model", Customer = "Customer",
                ValidationRequirement = "Radio validation",
                RequestedFinishDate = new DateOnly(2026, 11, 30)
            }, Now);
        application.Submit(new VerificationApplicationRouting(department, "DA40", "DA40",
            team, "TEAM-A", "Team A", "verification", "leader", "Leader"), Now);
        application.Accept(recordId, new DateOnly(2026, 12, 1), "leader", Now);
        return application;
    }

    private static ClaimsPrincipal Manager(string account = "leader") => new(new ClaimsIdentity([
        new Claim("account", account),
        new Claim(SystemAuthorization.AccessScopeClaim, SystemAuthorization.AccessScopes.CsitStaff)
    ], "test"));

    private sealed class AllowWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenyWrite : IBusinessWriteAuthorizationGuard
    {
        public Task DemandAsync(CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("BusinessWrite denied.");
    }

    private static KmDeletionCallback Callback(VerificationCaseDeletionTarget target,
        string status, string jobId)
    {
        var cleanup = new KmDeletionCleanup("Deleted", "Deleted", "Deleted", "Deleted",
            "Deleted", "Deleted", "NotPresent");
        return new("1.0", Guid.NewGuid(), "CSIT", target.Id, jobId, target.Resource,
            status, new KmDeletionTimestamps(new DateTimeOffset(Now),
                status == "Completed" ? new DateTimeOffset(Now.AddSeconds(30)) : null, null),
            status == "Completed" ? new KmDeletionResult(true,
                new DateTimeOffset(Now.AddSeconds(29)), cleanup) : null, null);
    }

    private static KmDeletionClient Client(HttpMessageHandler handler, bool enabled = true) =>
        new(new HttpClient(handler),
            Options.Create(new KmIntegrationOptions { BaseUrl = "https://km.example.test/", TimeoutSeconds = 5 }),
            Options.Create(new KmDeletionOptions { Enabled = enabled, Scope = "configured.delete.scope" }),
            new StaticToken());

    private static KmDeletionClient MockClient(HttpClient http, int timeoutSeconds) =>
        new(http, Options.Create(new KmIntegrationOptions
            { BaseUrl = "https://km.mock.test/", TimeoutSeconds = timeoutSeconds }),
            Options.Create(new KmDeletionOptions
                { Enabled = true, Scope = "csit.document.delete" }),
            new FixedToken("mock-km-submit"));

    private sealed class StaticToken : IKmDeletionAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken _) => Task.FromResult("token");
    }

    private sealed class FixedToken(string token) : IKmDeletionAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken _) => Task.FromResult(token);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class SequenceClient : IKmDeletionClient
    {
        public List<Guid> RequestIds { get; } = [];
        public Task<KmDeletionDeliveryResult> SubmitAsync(KmDeletionRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestIds.Add(request.DeletionRequestId);
            return Task.FromResult(RequestIds.Count == 1
                ? KmDeletionDeliveryResult.Failure(true, "HTTP_503")
                : KmDeletionDeliveryResult.Success("KM-DELETE-1"));
        }
    }

    private sealed class ThrowIngestionClient : IKmIntegrationClient
    {
        public Task<KmDeliveryResult> SubmitAsync(KmIngestionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Ingestion must stay blocked during deletion.");
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(options);
    }
}

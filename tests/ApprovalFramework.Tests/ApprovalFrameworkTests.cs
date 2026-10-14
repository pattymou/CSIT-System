using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SIT.DepartmentSystem.Web.Controllers;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class ApprovalFrameworkTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DisabledFlagDirectsToFormalFlow()
    {
        var env = new TestEnvironment(false);
        Assert.Equal(ApprovalUploadDecisionKind.DirectCommit,
            (await env.Policy.ResolveAsync(Source(env.TeamId), User("engineer", true))).Kind);
    }

    [Fact]
    public async Task EnabledFlagCreatesPendingDecisionForEngineer()
    {
        var env = new TestEnvironment(true);
        Assert.Equal(ApprovalUploadDecisionKind.Pending,
            (await env.Policy.ResolveAsync(Source(env.TeamId), User("engineer", true))).Kind);
    }

    [Fact]
    public async Task AdministrationBypassesApproval()
    {
        var env = new TestEnvironment(true);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed,
            (await env.Policy.ResolveAsync(Source(env.TeamId), User("leader", true, true))).Kind);
    }

    [Fact]
    public async Task AdministrationNeverGrantsBusinessWrite()
    {
        var env = new TestEnvironment(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Policy.ResolveAsync(Source(env.TeamId), User("leader", false, true)));
    }

    [Fact]
    public async Task UnregisteredHandlerFailsClosed()
    {
        var env = new TestEnvironment(true, registerHandler: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Policy.ResolveAsync(Source(env.TeamId), User("engineer", true)));
    }

    [Fact]
    public async Task MissingStagingRootFailsClosed()
    {
        var env = new TestEnvironment(true, rootAvailable: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Policy.ResolveAsync(Source(env.TeamId), User("engineer", true)));
    }

    [Fact]
    public async Task MenuToggleCannotEnableWithoutProductionHandler()
    {
        var guard = new ApprovalEnableGuard(new ApprovalCommitHandlerRegistry([]), new FakeRoot(Path.GetTempPath()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.DemandReadyAsync("verification"));
    }

    [Fact]
    public async Task DuplicateMenuCodeFailsClosed()
    {
        var env = new TestEnvironment(false);
        using (var db = env.Factory.CreateDbContext())
        {
            db.MenuItems.Add(new MenuItem { Id = Guid.NewGuid(), Code = "verification", ModuleCode = "other", IsEnabled = true });
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Policy.ResolveAsync(Source(env.TeamId), User("engineer", true)));
    }

    [Fact]
    public async Task LeaderScopeIncludesAllEnabledRoutedTeams()
    {
        var env = new TestEnvironment(true);
        var second = Guid.NewGuid();
        env.AddTeam(second, "B", "leader");
        var scope = await env.Scope.ResolveForViewAsync(User("leader", true, true));
        Assert.True(scope.Includes(env.TeamId));
        Assert.True(scope.Includes(second));
        Assert.False(scope.Includes(Guid.NewGuid()));
    }

    [Fact]
    public async Task LeaderCannotReviewOtherTeam()
    {
        var env = new TestEnvironment(true);
        var submission = Submission(Guid.NewGuid());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Scope.DemandReviewAsync(User("leader", true, true), submission));
    }

    [Fact]
    public async Task LeaderWithoutBusinessWriteCannotReview()
    {
        var env = new TestEnvironment(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Scope.DemandReviewAsync(User("leader", false, true), Submission(env.TeamId)));
    }

    [Fact]
    public async Task AdminCanReviewAnyTeam()
    {
        var env = new TestEnvironment(true);
        await env.Scope.DemandReviewAsync(User("admin", true, true, admin: true), Submission(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReviewerCannotApproveOwnSubmission()
    {
        var env = new TestEnvironment(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Scope.DemandReviewAsync(User("engineer", true, true, admin: true), Submission(env.TeamId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void RejectReasonMustContainText(string? reason)
    {
        Assert.Throws<ArgumentException>(() => Submission(Guid.NewGuid()).Reject("leader", reason, Now));
    }

    [Fact]
    public void RejectTrimsAndPreservesReviewerAndTime()
    {
        var submission = Submission(Guid.NewGuid());
        submission.Reject("LEADER", "  Needs correction  ", Now);
        Assert.Equal(ApprovalStatus.Rejected, submission.Status);
        Assert.Equal("Needs correction", submission.RejectReason);
        Assert.Equal("leader", submission.ReviewerAccount);
        Assert.Equal(Now, submission.ReviewedAt);
    }

    [Fact]
    public void RejectReasonLengthIsBounded()
    {
        Assert.Throws<ArgumentException>(() => Submission(Guid.NewGuid()).Reject(
            "leader", new string('X', PendingSubmission.MaxRejectReasonLength + 1), Now));
    }

    [Fact]
    public void RejectedCannotBeApprovedOrCommitted()
    {
        var submission = Submission(Guid.NewGuid());
        submission.Reject("leader", "reason", Now);
        Assert.Throws<InvalidOperationException>(() => submission.Approve("leader", Now));
        Assert.Throws<InvalidOperationException>(() => submission.Commit(Guid.NewGuid(), "result", Now));
    }

    [Fact]
    public void WithdrawnCannotLaterBeApprovedOrCommitted()
    {
        var submission = Submission(Guid.NewGuid());
        submission.Withdraw(Now);
        Assert.Equal(ApprovalStatus.Withdrawn, submission.Status);
        Assert.Throws<InvalidOperationException>(() => submission.Approve("leader", Now));
        Assert.Throws<InvalidOperationException>(() => submission.StartCommit(
            Guid.NewGuid(), Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void PendingCannotCommitWithoutApproval()
    {
        Assert.Throws<InvalidOperationException>(() => Submission(Guid.NewGuid())
            .StartCommit(Guid.NewGuid(), Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FailedCommitCanRetryAndComplete()
    {
        var submission = Submission(Guid.NewGuid());
        submission.Approve("leader", Now);
        var firstLease = Guid.NewGuid();
        submission.StartCommit(firstLease, Now, TimeSpan.FromMinutes(1));
        submission.FailCommit(firstLease, "safe error", Now);
        Assert.Equal(ApprovalStatus.CommitFailed, submission.Status);
        submission.Retry(Now.AddMinutes(2));
        var secondLease = Guid.NewGuid();
        submission.StartCommit(secondLease, Now.AddMinutes(2), TimeSpan.FromMinutes(1));
        submission.Commit(secondLease, "result", Now.AddMinutes(2));
        Assert.Equal(ApprovalStatus.Committed, submission.Status);
        Assert.Equal(2, submission.CommitAttemptCount);
    }

    [Fact]
    public async Task FakeHandlerIsIdempotentBySubmissionId()
    {
        var handler = new FakeHandler();
        var submission = Submission(Guid.NewGuid());
        var first = await handler.CommitAsync(submission, default);
        var second = await handler.CommitAsync(submission, default);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.ResultCount);
    }

    [Fact]
    public async Task RejectedQueryOnlyReturnsAuthenticatedUploader()
    {
        var env = new TestEnvironment(true);
        using (var db = env.Factory.CreateDbContext())
        {
            var own = Submission(env.TeamId, "engineer");
            own.Reject("leader", "own reason", Now);
            var other = Submission(env.TeamId, "other");
            other.Reject("leader", "other reason", Now);
            db.PendingSubmissions.AddRange(own, other);
            db.SaveChanges();
        }
        var items = await env.Submissions.ListRejectedMineAsync(User("engineer", true));
        Assert.Single(items);
        Assert.Equal("own reason", items[0].RejectReason);
    }

    [Fact]
    public async Task MySubmissionTabsOnlyContainOwnActiveWork()
    {
        var env = new TestEnvironment(true);
        using (var db = env.Factory.CreateDbContext())
        {
            var ownPending = Submission(env.TeamId);
            var otherPending = Submission(env.TeamId, "other");
            var ownRejected = Submission(env.TeamId);
            ownRejected.Reject("leader", "revise", Now);
            var dismissed = Submission(env.TeamId);
            dismissed.Reject("leader", "old", Now);
            dismissed.Dismiss(Now);
            var resubmitted = Submission(env.TeamId);
            resubmitted.Reject("leader", "old", Now);
            resubmitted.MarkResubmitted(Guid.NewGuid(), Now);
            var withdrawn = Submission(env.TeamId);
            withdrawn.Withdraw(Now);
            db.PendingSubmissions.AddRange(ownPending, otherPending, ownRejected, dismissed, resubmitted, withdrawn);
            db.SaveChanges();
        }
        var user = User("engineer", true);
        var counts = await env.Submissions.CountMineAsync(user);
        Assert.Equal(new MySubmissionCounts(1, 1), counts);
        Assert.Single(await env.Submissions.ListMineAsync(user, ApprovalStatus.Pending));
        Assert.Single(await env.Submissions.ListMineAsync(user, ApprovalStatus.Rejected));
        Assert.Empty(await env.Submissions.ListMineAsync(User("stranger", true), ApprovalStatus.Pending));
    }

    [Fact]
    public async Task PendingCanBeWithdrawnWithoutFormalWriteAndStagingIsCleaned()
    {
        var env = new TestEnvironment(true);
        var id = Guid.NewGuid();
        var staging = new ApprovalStagingStorage(new FakeRoot(Path.GetTempPath()));
        var bytes = new byte[] { 1, 2, 3 };
        var metadata = await staging.StoreAsync(id, new MemoryStream(bytes), "pending.txt", "text/plain", bytes.Length);
        using (var db = env.Factory.CreateDbContext())
        {
            db.PendingSubmissions.Add(PendingSubmission.Create(id, id.ToString("N"),
                "verification", "verification", "engineer", "R&D", env.TeamId, "A",
                "Task", "task", "Task", metadata, Now));
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Submissions.WithdrawMineAsync(id, User("other", true)));
        var ownFile = await env.Submissions.OpenFileAsync(id, User("engineer", true));
        await ownFile.Stream.DisposeAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Submissions.OpenFileAsync(id, User("other", true)));
        await env.Submissions.WithdrawMineAsync(id, User("engineer", true));
        using var check = env.Factory.CreateDbContext();
        var item = check.PendingSubmissions.Single(x => x.Id == id);
        Assert.Equal(ApprovalStatus.Withdrawn, item.Status);
        Assert.Null(item.StagingStorageKey);
        Assert.Contains(check.ApprovalAuditEvents, x => x.SubmissionId == id && x.Action == ApprovalAuditActions.Withdrawn);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), metadata.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(await env.Submissions.ListForReviewAsync(User("leader", true, supervisor: true), "verification"));
        Assert.Empty(check.ModuleCaseFiles);
    }

    [Fact]
    public async Task SupervisorDecisionPreventsWithdraw()
    {
        var env = new TestEnvironment(true);
        var item = Submission(env.TeamId);
        item.Approve("leader", Now);
        using (var db = env.Factory.CreateDbContext())
        {
            db.PendingSubmissions.Add(item);
            db.SaveChanges();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Submissions.WithdrawMineAsync(item.Id, User("engineer", true)));
        Assert.Contains("此提交已由主管處理", error.Message);
    }

    [Fact]
    public async Task ResubmissionCreatesNewPendingAndPreservesOldRejectedAudit()
    {
        var env = new TestEnvironment(true);
        var old = Submission(env.TeamId);
        old.Reject("leader", "revise", Now);
        using (var db = env.Factory.CreateDbContext())
        {
            db.PendingSubmissions.Add(old);
            db.ApprovalAuditEvents.Add(ApprovalAuditEvent.Create(old.Id, "verification", "verification",
                ApprovalAuditActions.Rejected, "leader", Now, ApprovalStatus.Pending,
                ApprovalStatus.Rejected, old.RejectReason));
            db.SaveChanges();
        }
        var id = Guid.NewGuid();
        var staging = new ApprovalStagingStorage(new FakeRoot(Path.GetTempPath()));
        var metadata = await staging.StoreAsync(id, new MemoryStream([4, 5]), "revision.txt", "text/plain", 2);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => env.Submissions.CreatePendingAsync(
                Guid.NewGuid(), new ApprovalUploadDecision(ApprovalUploadDecisionKind.Pending,
                    "verification", "verification"), Source(env.TeamId), Guid.NewGuid().ToString("N"),
                User("other", true), null, resubmissionOfId: old.Id));
            await env.Submissions.CreatePendingAsync(id,
                new ApprovalUploadDecision(ApprovalUploadDecisionKind.Pending, "verification", "verification"),
                Source(env.TeamId), id.ToString("N"), User("engineer", true), metadata,
                resubmissionOfId: old.Id);
            using var db = env.Factory.CreateDbContext();
            var previous = db.PendingSubmissions.Single(x => x.Id == old.Id);
            var current = db.PendingSubmissions.Single(x => x.Id == id);
            Assert.Equal(ApprovalStatus.Rejected, previous.Status);
            Assert.Equal(id, previous.ResubmittedAsSubmissionId);
            Assert.Equal(ApprovalStatus.Pending, current.Status);
            Assert.Equal("revision.txt", current.OriginalFileName);
            Assert.Equal(2, db.ApprovalAuditEvents.Count(x => x.SubmissionId == old.Id));
            Assert.Empty(await env.Submissions.ListMineAsync(User("engineer", true), ApprovalStatus.Rejected));
            Assert.Single(await env.Submissions.ListMineAsync(User("engineer", true), ApprovalStatus.Pending));
        }
        finally { await staging.DeleteAsync(id, metadata.StorageKey); }
    }

    [Fact]
    public async Task RejectedInitialReportCannotResubmitAsSecondRoot()
    {
        var env = new TestEnvironment(true);
        var moduleId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var target = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, taskId);
        var old = PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            "verification", "verification", "engineer", "R&D", env.TeamId, "A",
            target.ContextType, target.ContextId, "Task", null, Now);
        old.Reject("leader", "revise", Now);
        using (var db = env.Factory.CreateDbContext())
        {
            db.Modules.Add(new ModuleEntity { Id = moduleId, Code = "verification" });
            db.ModuleRecords.Add(new ModuleRecord { Id = recordId, ModuleId = moduleId });
            db.ModuleRecordCases.Add(new ModuleRecordCase { Id = caseId, RecordId = recordId, CaseNo = "C1" });
            db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = taskId, CaseId = caseId, TaskNo = "T1" });
            db.ModuleCaseFiles.Add(new ModuleCaseFile { Id = rootId, DocumentId = rootId,
                RecordId = recordId, CaseId = caseId, TaskId = taskId, CaseNo = "C1", TaskNo = "T1",
                FileKind = ModuleCaseFileKinds.TestReport, FileName = "existing.pdf" });
            db.PendingSubmissions.Add(old);
            db.SaveChanges();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Submissions.CreatePendingAsync(Guid.NewGuid(),
                new ApprovalUploadDecision(ApprovalUploadDecisionKind.Pending, "verification", "verification"),
                new ApprovalSourceIdentity("verification", target.ContextType, target.ContextId,
                    "Task", "R&D", env.TeamId, "A"), Guid.NewGuid().ToString("N"),
                User("engineer", true), null, resubmissionOfId: old.Id));
        Assert.Equal(TestReportBusinessRules.ExistingReportMessage, error.Message);
        using var check = env.Factory.CreateDbContext();
        Assert.Null(check.PendingSubmissions.Single(x => x.Id == old.Id).ResubmittedAsSubmissionId);
        Assert.Equal(1, check.ModuleCaseFiles.Count(x => x.TaskId == taskId));
    }

    [Fact]
    public async Task DismissKeepsRejectedHistoryButRemovesActiveItemAndStaging()
    {
        var env = new TestEnvironment(true);
        var id = Guid.NewGuid();
        var staging = new ApprovalStagingStorage(new FakeRoot(Path.GetTempPath()));
        var metadata = await staging.StoreAsync(id, new MemoryStream([8]), "old.txt", "text/plain", 1);
        var old = PendingSubmission.Create(id, id.ToString("N"), "verification", "verification",
            "engineer", "R&D", env.TeamId, "A", "Task", "task", "Task", metadata, Now);
        old.Reject("leader", "revise", Now);
        using (var db = env.Factory.CreateDbContext())
        {
            db.PendingSubmissions.Add(old);
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            env.Submissions.DismissMineAsync(id, User("other", true)));
        await env.Submissions.DismissMineAsync(id, User("engineer", true));
        using var check = env.Factory.CreateDbContext();
        var retained = check.PendingSubmissions.Single(x => x.Id == id);
        Assert.Equal(ApprovalStatus.Rejected, retained.Status);
        Assert.NotNull(retained.SubmitterDismissedAt);
        Assert.Null(retained.StagingStorageKey);
        Assert.Contains(check.ApprovalAuditEvents, x => x.SubmissionId == id
            && x.Action == ApprovalAuditActions.SubmitterDismissed);
        Assert.Empty(await env.Submissions.ListMineAsync(User("engineer", true), ApprovalStatus.Rejected));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), metadata.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task SourceFilterAndRejectedViewKeepResolvedCaseLinkAndTeamScope()
    {
        var env = new TestEnvironment(true);
        var moduleId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        Guid pendingId;
        Guid rejectedId;
        using (var db = env.Factory.CreateDbContext())
        {
            db.Modules.Add(new ModuleEntity { Id = moduleId, Code = "verification" });
            db.ModuleRecords.Add(new ModuleRecord { Id = recordId, ModuleId = moduleId,
                RecordNo = "DEV-P01-A3F2", Name = "DEV-P01-A3F2" });
            db.ModuleRecordCases.Add(new ModuleRecordCase { Id = caseId, RecordId = recordId,
                CaseNo = "C1", Name = "Case-01" });
            var pending = PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"),
                "verification", "verification", "engineer", "R&D", env.TeamId, "A",
                "CaseAttachment", caseId.ToString("N"), "C1", null, Now);
            var rejected = PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"),
                "verification", "verification", "engineer", "R&D", env.TeamId, "A",
                "CaseAttachment", caseId.ToString("N"), "C1", null, Now);
            rejected.Reject("leader", "Revise", Now);
            var otherSource = PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"),
                "other-source", "verification", "engineer", "R&D", env.TeamId, "A",
                "CaseAttachment", caseId.ToString("N"), "C1", null, Now);
            db.PendingSubmissions.AddRange(pending, rejected, otherSource);
            db.SaveChanges();
            pendingId = pending.Id;
            rejectedId = rejected.Id;
        }
        var sources = await env.Submissions.ListActiveSourcesAsync();
        Assert.Single(sources);
        Assert.Equal("verification", sources[0].Code);
        var review = await env.Submissions.ListForReviewAsync(User("leader", true, supervisor: true), "verification");
        var item = Assert.Single(review);
        Assert.Equal(pendingId, item.Id);
        Assert.Equal(env.TeamId, (await env.Scope.ResolveForViewAsync(User("leader", true, supervisor: true))).TeamOptionIds.Single());
        Assert.Equal($"/modules/verification/{recordId}/cases/{caseId}", item.ContextRoute);
        Assert.Equal("DEV-P01-A3F2 / Case-01", item.ContextDisplay);
        var ownRejected = Assert.Single(await env.Submissions.ListRejectedMineAsync(User("engineer", true)));
        Assert.Equal(rejectedId, ownRejected.Id);
        Assert.Equal(item.ContextRoute, ownRejected.ContextRoute);
    }

    [Fact]
    public async Task OtherUserCannotDownloadRejectedStagingFile()
    {
        var env = new TestEnvironment(true);
        using (var db = env.Factory.CreateDbContext())
        {
            var submission = Submission(env.TeamId, "engineer", new StagedFileMetadata(
                "report.pdf", "application/pdf", 1, new string('A', 64), $"{Guid.NewGuid():N}/{Guid.NewGuid():N}"));
            submission.Reject("leader", "reason", Now);
            db.PendingSubmissions.Add(submission);
            db.SaveChanges();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                env.Submissions.OpenFileAsync(submission.Id, User("other", true)));
        }
    }

    [Fact]
    public async Task RejectedUploaderAndScopedReviewerCanDownloadButOtherUserCannot()
    {
        var env = new TestEnvironment(true);
        var id = Guid.NewGuid();
        var folder = Path.Combine(Path.GetTempPath(), id.ToString("N"));
        var key = $"{id:N}/{Guid.NewGuid():N}";
        var filePath = Path.Combine(folder, key.Split('/')[1]);
        Directory.CreateDirectory(folder);
        try
        {
            var content = new byte[] { 1, 2, 3 };
            await File.WriteAllBytesAsync(filePath, content);
            var metadata = new StagedFileMetadata("result-01.txt", "text/plain", content.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), key);
            var submission = PendingSubmission.Create(id, id.ToString("N"), "verification", "verification",
                "engineer", "R&D", env.TeamId, "A", "CaseAttachment", Guid.NewGuid().ToString("N"),
                "DEV-P01 / Case-01", metadata, Now);
            submission.Reject("leader", "Revise file", Now);
            using (var db = env.Factory.CreateDbContext())
            {
                db.PendingSubmissions.Add(submission);
                db.SaveChanges();
            }
            var own = await env.Submissions.OpenFileAsync(id, User("engineer", true));
            await using (own.Stream)
            {
                Assert.Equal("result-01.txt", own.FileName);
                using var memory = new MemoryStream();
                await own.Stream.CopyToAsync(memory);
                Assert.Equal(content, memory.ToArray());
            }
            var reviewer = await env.Submissions.OpenFileAsync(id, User("leader", true, supervisor: true));
            await reviewer.Stream.DisposeAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                env.Submissions.OpenFileAsync(id, User("other", true)));
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }

    [Fact]
    public void RejectedAuditRetainsReasonAndTransition()
    {
        var submission = Submission(Guid.NewGuid());
        submission.Reject("leader", "reason", Now);
        var audit = ApprovalAuditEvent.Create(submission.Id, "verification", "verification",
            ApprovalAuditActions.Rejected, "LEADER", Now, ApprovalStatus.Pending,
            ApprovalStatus.Rejected, submission.RejectReason);
        Assert.Equal("reason", audit.Reason);
        Assert.Equal("leader", audit.ActorAccount);
        Assert.Equal(ApprovalStatus.Rejected, audit.ToStatus);
    }

    [Fact]
    public void RejectedAuditCannotOmitReason()
    {
        Assert.Throws<ArgumentException>(() => ApprovalAuditEvent.Create(Guid.NewGuid(),
            "verification", "verification", ApprovalAuditActions.Rejected, "leader", Now,
            ApprovalStatus.Pending, ApprovalStatus.Rejected, "  "));
    }

    [Fact]
    public void AuditEventsAreAppendOnlyThroughDbContext()
    {
        var env = new TestEnvironment(false);
        using var db = env.Factory.CreateDbContext();
        var audit = ApprovalAuditEvent.Create(Guid.NewGuid(), "verification", "verification",
            ApprovalAuditActions.Submitted, "engineer", Now, null, ApprovalStatus.Pending);
        db.ApprovalAuditEvents.Add(audit);
        db.SaveChanges();
        db.ApprovalAuditEvents.Remove(audit);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void EfModelContainsRejectedReasonConstraints()
    {
        var env = new TestEnvironment(false);
        using var db = env.Factory.CreateDbContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        Assert.Contains(model.FindEntityType(typeof(PendingSubmission))!.GetCheckConstraints(),
            x => x.Name == "ck_pending_submissions_rejected_reason");
        Assert.Contains(model.FindEntityType(typeof(ApprovalAuditEvent))!.GetCheckConstraints(),
            x => x.Name == "ck_approval_audit_rejected_reason");
    }

    [Fact]
    public async Task StagingUsesOpaqueKeyAndVerifiesLengthAndHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "csit-approval-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new ApprovalStagingStorage(new FakeRoot(root));
            var id = Guid.NewGuid();
            await using var input = new MemoryStream([1, 2, 3]);
            var metadata = await storage.StoreAsync(id, input, "report.pdf", "application/pdf", 3);
            Assert.DoesNotContain("report.pdf", metadata.StorageKey);
            Assert.Equal(64, metadata.FileHash.Length);
            await using var result = await storage.OpenReadAsync(id, metadata.StorageKey);
            Assert.Equal(3, result.Length);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.OpenReadAsync(Guid.NewGuid(), metadata.StorageKey));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StagingRejectsPayloadBeyondDeclaredSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "csit-approval-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new ApprovalStagingStorage(new FakeRoot(root));
            await using var input = new MemoryStream([1, 2]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.StoreAsync(Guid.NewGuid(), input, "file.bin", "application/octet-stream", 1));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyTestReportReviewRouteAndPolicyRemainAvailable()
    {
        Assert.Equal("api/test-reports", Attribute.GetCustomAttribute(typeof(TestReportReviewsController),
            typeof(RouteAttribute)) is RouteAttribute route ? route.Template : null);
        Assert.Equal("{fileId:guid}/review", typeof(TestReportReviewsController)
            .GetMethod("Review")!.GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>().Single().Template);
    }

    [Fact]
    public void LegacyCaseUploadRoutesRemainAvailable()
    {
        Assert.Equal("api/cases/{caseId:guid}/files", typeof(CaseFilesController)
            .GetMethod("Upload")!.GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>().Single().Template);
        Assert.Equal("api/records/{recordId:guid}/cases/upload/{caseNo}/attachments", typeof(CaseFilesController)
            .GetMethod("UploadByCaseNo")!.GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>().Single().Template);
    }

    [Fact]
    public void LegacyEquipmentAndGoodsUploadRoutesRemainAvailable()
    {
        Assert.Equal("api/assets/{moduleCode}", typeof(ApparatusController)
            .GetMethod("Create")!.GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>().Single().Template);
        Assert.Equal("api/assets/{moduleCode}/{id}/files", typeof(ApparatusController)
            .GetMethod("UploadFiles")!.GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>().Single().Template);
    }

    [Fact]
    public void MissingTeamSnapshotFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => PendingSubmission.Create(Guid.NewGuid(),
            Guid.NewGuid().ToString("N"), "verification", "verification", "engineer", "R&D",
            Guid.Empty, "", "Task", "1", null, null, Now));
    }

    private static ApprovalSourceIdentity Source(Guid teamId) => new(
        "verification", "Task", "task", "Task", "R&D", teamId, "A");

    private static PendingSubmission Submission(Guid teamId, string account = "engineer", StagedFileMetadata? file = null) =>
        PendingSubmission.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "verification", "verification",
            account, "R&D", teamId, "A", "Task", "task", "Task", file, Now);

    private static ClaimsPrincipal User(string account, bool canWrite, bool supervisor = false, bool admin = false)
    {
        var claims = new List<Claim> { new("account", account) };
        if (canWrite) claims.Add(new("test_can_write", "true"));
        if (supervisor) claims.Add(new("test_supervisor", "true"));
        if (admin) claims.Add(new(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class TestEnvironment
    {
        public readonly Guid TeamId = Guid.NewGuid();
        public readonly TestDbFactory Factory = new();
        public readonly ApprovalPolicyResolver Policy;
        public readonly ApprovalReviewerScopeResolver Scope;
        public readonly ApprovalSubmissionService Submissions;

        public TestEnvironment(bool enabled, bool registerHandler = true, bool rootAvailable = true)
        {
            using (var db = Factory.CreateDbContext())
            {
                db.MenuItems.Add(new MenuItem { Id = Guid.NewGuid(), Code = "verification", Title = "Source",
                    ModuleCode = "verification", IsEnabled = true, RequiresSupervisorApproval = enabled });
                db.SystemOptions.Add(new SystemOption { Id = TeamId, Category = SystemOptionCategories.Team,
                    Name = "A", Value = "A", IsEnabled = true });
                db.TeamRoutings.Add(new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = TeamId,
                    LeaderAccount = "leader", IsEnabled = true });
                db.SaveChanges();
            }
            var handlers = new ApprovalCommitHandlerRegistry(registerHandler ? [new FakeHandler()] : []);
            var root = new FakeRoot(rootAvailable ? Path.GetTempPath() : null);
            var auth = new FakeAuthorization();
            Policy = new ApprovalPolicyResolver(new ApprovalMenuResolver(Factory), handlers, root, auth);
            Scope = new ApprovalReviewerScopeResolver(Factory, auth);
            Submissions = new ApprovalSubmissionService(Factory, Scope, handlers, root, new ApprovalStagingStorage(root));
        }

        public void AddTeam(Guid id, string code, string leader)
        {
            using var db = Factory.CreateDbContext();
            db.SystemOptions.Add(new SystemOption { Id = id, Category = SystemOptionCategories.Team,
                Name = code, Value = code, IsEnabled = true });
            db.TeamRoutings.Add(new TeamRouting { Id = Guid.NewGuid(), TeamOptionId = id,
                LeaderAccount = leader, IsEnabled = true });
            db.SaveChanges();
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    private sealed class FakeAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult((policyName == SystemAuthorization.Policies.BusinessWrite && user.HasClaim("test_can_write", "true"))
                || (policyName == SystemAuthorization.Policies.Administration && user.HasClaim("test_supervisor", "true"))
                ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private sealed class FakeRoot(string? root) : IApprovalStagingRootResolver
    {
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) =>
            root is null ? throw new InvalidOperationException("No staging root") : Task.FromResult(root);
    }

    private sealed class FakeHandler : IApprovalCommitHandler
    {
        private readonly Dictionary<Guid, ApprovalCommitResult> _results = [];
        public string ModuleCode => "verification";
        public int ResultCount => _results.Count;
        public Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission, CancellationToken cancellationToken)
        {
            if (!_results.TryGetValue(submission.Id, out var result))
                _results[submission.Id] = result = new ApprovalCommitResult(submission.Id.ToString("N"));
            return Task.FromResult(result);
        }
    }
}

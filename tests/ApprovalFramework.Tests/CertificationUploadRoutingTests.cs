using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.Approval;

namespace ApprovalFramework.Tests;

public sealed class CertificationUploadRoutingTests
{
    [Theory]
    [InlineData(ThreeLevelUploadKind.CaseAttachment)]
    [InlineData(ThreeLevelUploadKind.CaseAttachmentByNo)]
    [InlineData(ThreeLevelUploadKind.TaskAttachment)]
    [InlineData(ThreeLevelUploadKind.TaskAttachmentByNo)]
    [InlineData(ThreeLevelUploadKind.TestReport)]
    [InlineData(ThreeLevelUploadKind.TestReportByTaskNo)]
    [InlineData(ThreeLevelUploadKind.TestReportVersion)]
    public void EveryUploadContextRoundTripsWithoutMenuTitle(ThreeLevelUploadKind kind)
    {
        var id = Guid.NewGuid();
        var numbered = kind is ThreeLevelUploadKind.CaseAttachmentByNo
            or ThreeLevelUploadKind.TaskAttachmentByNo or ThreeLevelUploadKind.TestReportByTaskNo;
        var target = new ThreeLevelUploadTarget(kind, id, numbered ? "DEV-測試/1" : null);
        Assert.Equal(target, ThreeLevelUploadTarget.FromContext(target.ContextType, target.ContextId));
        Assert.DoesNotContain("認證管理", target.ContextId);
    }

    [Fact]
    public async Task CertificationFlagFalseUsesDirectFormalPath()
    {
        using var env = new EnvironmentFixture(false);
        var result = await env.Upload.UploadAsync(env.CaseTarget, [File()], User("engineer"), "engineer", false);
        Assert.Equal(ApprovalUploadDecisionKind.DirectCommit, result.Decision);
        Assert.Single(env.Formal.Calls);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
    }

    [Fact]
    public async Task CertificationEngineerStagesWithoutFormalCall()
    {
        using var env = new EnvironmentFixture(true);
        var result = await env.Upload.UploadAsync(env.CaseTarget, [File()], User("engineer"), "engineer", false);
        Assert.Equal(ApprovalUploadDecisionKind.Pending, result.Decision);
        Assert.Empty(env.Formal.Calls);
        using var db = env.Factory.CreateDbContext();
        var pending = Assert.Single(db.PendingSubmissions);
        Assert.Equal(ApprovalStatus.Pending, pending.Status);
        Assert.Equal("certification", pending.SourceMenuCode);
        Assert.Equal(env.TeamId, pending.SubmittedTeamOptionId);
        Assert.Single(Directory.GetFiles(env.StagingRoot, "*", SearchOption.AllDirectories));
        Assert.Single(db.ApprovalAuditEvents);
    }

    [Fact]
    public async Task MissingProjectTeamRejectsBeforeStagingOrSubmission()
    {
        using var env = new EnvironmentFixture(true, hasTeam: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Upload.UploadAsync(env.CaseTarget, [File()], User("engineer"), "engineer", false));
        Assert.Contains("Team", error.Message);
        Assert.Empty(Directory.GetFiles(env.StagingRoot, "*", SearchOption.AllDirectories));
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
    }

    [Fact]
    public async Task SupervisorBypassesAndWritesAuditWithoutPending()
    {
        using var env = new EnvironmentFixture(true);
        var result = await env.Upload.UploadAsync(env.CaseTarget, [File()], User("leader", supervisor: true), "leader", false);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, result.Decision);
        Assert.Single(env.Formal.Calls);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
        Assert.Equal(ApprovalAuditActions.ApprovalBypassed, Assert.Single(db.ApprovalAuditEvents).Action);
    }

    [Fact]
    public async Task SupervisorReportUploadBypassesLegacyPendingReview()
    {
        using var env = new EnvironmentFixture(true);
        var result = await env.Upload.UploadAsync(env.ReportTarget, [File()],
            User("leader", supervisor: true), "leader", false);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, result.Decision);
        Assert.True(env.Formal.LastAutoApprove);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
    }

    [Fact]
    public async Task AdministrationWithoutBusinessWriteCannotUpload()
    {
        using var env = new EnvironmentFixture(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => env.Upload.UploadAsync(
            env.CaseTarget, [File()], User("leader", write: false, supervisor: true), "leader", false));
        Assert.Empty(env.Formal.Calls);
    }

    [Fact]
    public async Task VerificationFlagFalseUsesDirectPathWhenCertificationApprovalEnabled()
    {
        using var env = new EnvironmentFixture(true);
        var result = await env.Upload.UploadAsync(env.VerificationTarget, [File()],
            User("engineer"), "engineer", false);
        Assert.Equal(ApprovalUploadDecisionKind.DirectCommit, result.Decision);
        Assert.Single(env.Formal.Calls);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
    }

    [Fact]
    public async Task VerificationFlagTrueStagesWithoutFormalWrite()
    {
        using var env = new EnvironmentFixture(true, verificationEnabled: true);
        var result = await env.Upload.UploadAsync(env.VerificationTarget, [File()],
            User("engineer"), "engineer", false);
        Assert.Equal(ApprovalUploadDecisionKind.Pending, result.Decision);
        Assert.Empty(env.Formal.Calls);
        using var db = env.Factory.CreateDbContext();
        var pending = Assert.Single(db.PendingSubmissions);
        Assert.Equal("verification", pending.SourceMenuCode);
        Assert.Equal("verification", pending.SourceModuleCode);
        Assert.Equal(env.TeamId, pending.SubmittedTeamOptionId);
    }

    [Fact]
    public async Task VerificationMissingProjectTeamStopsBeforePending()
    {
        using var env = new EnvironmentFixture(true, hasTeam: false, verificationEnabled: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Upload.UploadAsync(
            env.VerificationTarget, [File()], User("engineer"), "engineer", false));
        Assert.Equal("此專案尚未設定 Team，無法送主管審核，請先完成 Team 設定。", error.Message);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
        Assert.Empty(env.Formal.Calls);
    }

    [Fact]
    public async Task VerificationSupervisorBypassesLegacyReview()
    {
        using var env = new EnvironmentFixture(true, verificationEnabled: true);
        var result = await env.Upload.UploadAsync(env.VerificationReportTarget, [File()],
            User("leader", supervisor: true), "leader", false);
        Assert.Equal(ApprovalUploadDecisionKind.ApprovalBypassed, result.Decision);
        Assert.True(env.Formal.LastAutoApprove);
        using var db = env.Factory.CreateDbContext();
        Assert.Empty(db.PendingSubmissions);
        Assert.Equal(ApprovalAuditActions.ApprovalBypassed, Assert.Single(db.ApprovalAuditEvents).Action);
    }

    [Theory]
    [InlineData(false, "certification")]
    [InlineData(true, "verification")]
    public async Task CasePendingAndRejectedAreVisibleOnlyToUploaderInOriginalCase(
        bool verification, string moduleCode)
    {
        using var env = new EnvironmentFixture(true, verificationEnabled: verification);
        var target = verification ? env.VerificationTarget : env.CaseTarget;
        await env.Upload.UploadAsync(target, [File()], User("engineer"), "engineer", false);
        using var db = env.Factory.CreateDbContext();
        var parent = db.ModuleRecordCases.Single(x => x.Id == target.Id);
        var service = new ApprovalContextUploads(env.Factory);
        var mine = await service.ForCaseAsync(User("engineer"), moduleCode, parent.RecordId, target.Id);
        Assert.Equal(ApprovalStatus.Pending, Assert.Single(mine).Status);
        Assert.Empty(await service.ForCaseAsync(User("other"), moduleCode, parent.RecordId, target.Id));
        Assert.Empty(db.ModuleCaseFiles.Where(x => x.CaseId == target.Id));
        Assert.NotNull(db.ModuleRecords.SingleOrDefault(x => x.Id == parent.RecordId));
        Assert.NotNull(db.ModuleRecordCases.SingleOrDefault(x => x.Id == target.Id));

        var submission = db.PendingSubmissions.Single();
        submission.Reject("leader", "DEV TEST 退回原因", DateTime.UtcNow);
        db.SaveChanges();
        var rejected = Assert.Single(await service.ForCaseAsync(User("engineer"), moduleCode,
            parent.RecordId, target.Id));
        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
        Assert.Equal("DEV TEST 退回原因", rejected.RejectReason);
        Assert.Empty(await service.ForCaseAsync(User("other"), moduleCode, parent.RecordId, target.Id));
    }

    [Fact]
    public async Task CommittedCaseUploadLeavesOwnQueueAndHasOneFormalFile()
    {
        using var env = new EnvironmentFixture(true, verificationEnabled: true);
        await env.Upload.UploadAsync(env.VerificationTarget, [File()], User("engineer"), "engineer", false);
        using var db = env.Factory.CreateDbContext();
        var parent = db.ModuleRecordCases.Single(x => x.Id == env.VerificationTarget.Id);
        var submission = db.PendingSubmissions.Single();
        submission.Approve("leader", DateTime.UtcNow);
        var lease = Guid.NewGuid();
        submission.StartCommit(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
        submission.Commit(lease, submission.Id.ToString("N"), DateTime.UtcNow);
        db.ModuleCaseFiles.Add(new ModuleCaseFile { Id = submission.Id, RecordId = parent.RecordId,
            CaseId = parent.Id, CaseNo = parent.CaseNo, FileName = "DEV-APPROVAL-TEST.txt",
            FilePath = "DEV TEST", FileKind = ModuleCaseFileKinds.Attachment });
        db.SaveChanges();

        var service = new ApprovalContextUploads(env.Factory);
        Assert.Empty(await service.ForCaseAsync(User("engineer"), "verification", parent.RecordId, parent.Id));
        Assert.Single(db.ModuleCaseFiles.Where(x => x.CaseId == parent.Id && x.Id == submission.Id));
    }

    [Fact]
    public async Task TaskReportProcessingAndFailureRemainVisibleToUploader()
    {
        using var env = new EnvironmentFixture(true);
        await env.Upload.UploadAsync(env.ReportTarget, [File()], User("engineer"), "engineer", false);
        using var db = env.Factory.CreateDbContext();
        var task = db.ModuleRecordTasks.Single(x => x.Id == env.ReportTarget.Id);
        var parent = db.ModuleRecordCases.Single(x => x.Id == task.CaseId);
        var service = new ApprovalContextUploads(env.Factory);
        var submission = db.PendingSubmissions.Single();
        submission.Approve("leader", DateTime.UtcNow);
        db.SaveChanges();
        var processing = Assert.Single(await service.ForTaskAsync(User("engineer"), "certification",
            parent.RecordId, parent.Id, task.Id));
        Assert.True(processing.IsTestReport);
        Assert.Equal(ApprovalStatus.ApprovedPendingCommit, processing.Status);

        var lease = Guid.NewGuid();
        submission.StartCommit(lease, DateTime.UtcNow, TimeSpan.FromMinutes(1));
        submission.FailCommit(lease, "DEV TEST failure", DateTime.UtcNow);
        db.SaveChanges();
        Assert.Equal(ApprovalStatus.CommitFailed, Assert.Single(await service.ForTaskAsync(
            User("engineer"), "certification", parent.RecordId, parent.Id, task.Id)).Status);
        Assert.Empty(await service.ForTaskAsync(User("other"), "certification",
            parent.RecordId, parent.Id, task.Id));
        Assert.NotNull(db.ModuleRecordTasks.SingleOrDefault(x => x.Id == task.Id));
    }

    private static IFormFile File()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "DEV-APPROVAL-TEST.txt")
        { Headers = new HeaderDictionary(), ContentType = "text/plain" };
    }

    private static ClaimsPrincipal User(string account, bool write = true, bool supervisor = false)
    {
        var claims = new List<Claim> { new("account", account) };
        if (write) claims.Add(new("can_write", "true"));
        if (supervisor) claims.Add(new("supervisor", "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class EnvironmentFixture : IDisposable
    {
        public readonly TestDbFactory Factory = new();
        public readonly FakeFormal Formal = new();
        public readonly Guid TeamId = Guid.NewGuid();
        public readonly ThreeLevelApprovalUploadService Upload;
        public readonly ThreeLevelUploadTarget CaseTarget;
        public readonly ThreeLevelUploadTarget ReportTarget;
        public readonly ThreeLevelUploadTarget VerificationTarget;
        public readonly ThreeLevelUploadTarget VerificationReportTarget;
        public readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "csit-cert-test-" + Guid.NewGuid().ToString("N"));

        public EnvironmentFixture(bool enabled, bool hasTeam = true, bool verificationEnabled = false)
        {
            Directory.CreateDirectory(StagingRoot);
            var certModule = Guid.NewGuid();
            var verificationModule = Guid.NewGuid();
            var certRecord = Guid.NewGuid();
            var verificationRecord = Guid.NewGuid();
            var caseId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var verificationCaseId = Guid.NewGuid();
            var verificationTaskId = Guid.NewGuid();
            using (var db = Factory.CreateDbContext())
            {
                db.Modules.AddRange(new ModuleEntity { Id = certModule, Code = "certification" },
                    new ModuleEntity { Id = verificationModule, Code = "verification" });
                db.ModuleRecords.AddRange(
                    new ModuleRecord { Id = certRecord, ModuleId = certModule, Name = "DEV-APPROVAL-TEST",
                        Department = "DEV", Team = hasTeam ? "DEV-TEAM" : null },
                    new ModuleRecord { Id = verificationRecord, ModuleId = verificationModule, Name = "DEV-VERIFICATION",
                        Department = "DEV", Team = hasTeam ? "DEV-TEAM" : null });
                db.ModuleRecordCases.AddRange(
                    new ModuleRecordCase { Id = caseId, RecordId = certRecord, CaseNo = "C1", Name = "Case" },
                    new ModuleRecordCase { Id = verificationCaseId, RecordId = verificationRecord, CaseNo = "V1", Name = "Case" });
                db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = taskId, CaseId = caseId,
                    TaskNo = "T1", Name = "Task" });
                db.ModuleRecordTasks.Add(new ModuleRecordTask { Id = verificationTaskId, CaseId = verificationCaseId,
                    TaskNo = "VT1", Name = "Task" });
                db.SystemOptions.Add(new SystemOption { Id = TeamId, Category = SystemOptionCategories.Team,
                    Name = "DEV-TEAM", Value = "DEV-TEAM", IsEnabled = true });
                db.MenuItems.Add(new MenuItem { Id = Guid.NewGuid(), Code = "certification", ModuleCode = "certification",
                    Title = "認證管理", IsEnabled = true, RequiresSupervisorApproval = enabled });
                db.MenuItems.Add(new MenuItem { Id = Guid.NewGuid(), Code = "verification", ModuleCode = "verification",
                    Title = "專案管理", IsEnabled = true, RequiresSupervisorApproval = verificationEnabled });
                db.SaveChanges();
            }
            CaseTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachment, caseId);
            ReportTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, taskId);
            VerificationTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.CaseAttachment, verificationCaseId);
            VerificationReportTarget = new ThreeLevelUploadTarget(ThreeLevelUploadKind.TestReport, verificationTaskId);
            var source = new ApprovalSourceResolver(Factory);
            var root = new TestRoot(StagingRoot);
            var staging = new ApprovalStagingStorage(root);
            var handler = new TestHandler();
            var registry = new ApprovalCommitHandlerRegistry([handler, new TestHandler("verification")]);
            var auth = new TestAuthorization();
            var policy = new ApprovalPolicyResolver(new ApprovalMenuResolver(Factory), registry, root, auth);
            var scope = new ApprovalReviewerScopeResolver(Factory, auth);
            var submissions = new ApprovalSubmissionService(Factory, scope, registry, root, staging);
            Upload = new ThreeLevelApprovalUploadService(source, policy, submissions, staging, Formal);
        }

        public void Dispose()
        {
            var root = Path.GetFullPath(StagingRoot);
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test staging path.");
            Directory.Delete(root, true);
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    private sealed class FakeFormal : IThreeLevelFormalUploadService
    {
        public List<ThreeLevelUploadTarget> Calls { get; } = [];
        public bool LastAutoApprove { get; private set; }
        public Task ExecuteFormalAsync(ThreeLevelUploadTarget target, IReadOnlyList<IFormFile> files,
            string? uploadEmp, bool autoApprove, Guid? approvalFileId = null, string? approvedBy = null)
        { Calls.Add(target); LastAutoApprove = autoApprove; return Task.CompletedTask; }
    }

    private sealed class TestRoot(string root) : IApprovalStagingRootResolver
    {
        public Task<string> GetRootAsync(CancellationToken cancellationToken = default) => Task.FromResult(root);
    }

    private sealed class TestHandler(string moduleCode = "certification") : IApprovalCommitHandler
    {
        public string ModuleCode => moduleCode;
        public Task ValidateAsync(PendingSubmission submission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ApprovalCommitResult> CommitAsync(PendingSubmission submission, CancellationToken cancellationToken) =>
            Task.FromResult(new ApprovalCommitResult(submission.Id.ToString("N")));
    }

    private sealed class TestAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == SystemAuthorization.Policies.BusinessWrite && user.HasClaim("can_write", "true")
                || policyName == SystemAuthorization.Policies.Administration && user.HasClaim("supervisor", "true")
                ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}

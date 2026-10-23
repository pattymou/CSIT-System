using System.Security.Claims;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class VerificationSupervisorReviewTests
{
    [Fact]
    public void AssignedLeaderCanReview()
    {
        var application = SubmittedApplication("applicant", "leader");

        VerificationApplicationSecurity.EnsureReviewerAccess(application, User("leader"));
    }

    [Fact]
    public void AdminCanReviewAnotherLeadersApplication()
    {
        var application = SubmittedApplication("applicant", "leader");

        VerificationApplicationSecurity.EnsureReviewerAccess(application, User("admin", admin: true));
    }

    [Fact]
    public void NobodyCanReviewTheirOwnApplication()
    {
        var application = SubmittedApplication("admin", "leader");

        Assert.Throws<UnauthorizedAccessException>(() =>
            VerificationApplicationSecurity.EnsureReviewerAccess(application, User("admin", admin: true)));
    }

    [Fact]
    public void UnassignedCsitUserCannotReview()
    {
        var application = SubmittedApplication("applicant", "leader");

        Assert.Throws<UnauthorizedAccessException>(() =>
            VerificationApplicationSecurity.EnsureReviewerAccess(application, User("engineer")));
    }

    [Fact]
    public void AdminAndActiveTeamLeaderBypassReviewButOrdinaryApplicantDoesNot()
    {
        Assert.True(VerificationApplicationSecurity.CanBypassReview(User("admin", admin: true), false));
        Assert.True(VerificationApplicationSecurity.CanBypassReview(User("leader"), true));
        Assert.False(VerificationApplicationSecurity.CanBypassReview(User("applicant"), false));
    }

    [Fact]
    public void DirectApprovalRequiresExpectedEndDateOnServer()
    {
        Assert.Throws<InvalidOperationException>(() => VerificationApplicationSecurity.RequireDirectApprovalDate(null));
        Assert.Throws<InvalidOperationException>(() => VerificationApplicationSecurity.RequireDirectApprovalDate(default(DateOnly)));
        Assert.Equal(new DateOnly(2026, 12, 31),
            VerificationApplicationSecurity.RequireDirectApprovalDate(new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void DirectApprovalStoresActorTimeReasonAndExpectedDate()
    {
        var application = SubmittedApplication("admin", "leader");
        var acceptedAt = DateTime.UtcNow.AddMinutes(1);
        var expectedEndDate = new DateOnly(2026, 12, 31);
        var recordId = Guid.NewGuid();

        application.Accept(recordId, expectedEndDate, "admin", acceptedAt,
            "Admin self-submission: supervisor review bypassed.");

        Assert.Equal(VerificationApplicationStatus.Accepted, application.Status);
        Assert.Equal(recordId, application.ModuleRecordId);
        Assert.Equal(expectedEndDate, application.ExpectedEndDate);
        Assert.Equal("admin", application.ProcessedBy);
        Assert.Equal(acceptedAt, application.ProcessedAt);
        Assert.Equal(acceptedAt, application.AcceptedAt);
        Assert.Contains("self-submission", application.ProcessingNote);
    }

    [Fact]
    public void ReviewerEditPreservesApplicantIdentityAndSubmissionMetadataWhileRerouting()
    {
        var application = SubmittedApplication("applicant", "leader");
        var id = application.Id;
        var applicationNo = application.ApplicationNo;
        var submittedAt = application.SubmittedAt;
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        application.UpdateSubmittedContent(
            new VerificationApplicationRouting(departmentId, "DA40", "DA40", teamId, "CELL", "Cellular",
                "verification", "new-leader", "New Leader"),
            new VerificationApplicationContent { ProjectName = "Edited Model", ProductModel = "Edited Model", Customer = "Edited Customer" },
            versionId, 2, "CELLULAR_UE", "{\"edited\":true}", DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(id, application.Id);
        Assert.Equal(applicationNo, application.ApplicationNo);
        Assert.Equal("applicant", application.ApplicantAccount);
        Assert.Equal(submittedAt, application.SubmittedAt);
        Assert.Equal(VerificationApplicationStatus.Submitted, application.Status);
        Assert.Equal("new-leader", application.AssignedLeaderAccount);
        Assert.Equal(teamId, application.TeamOptionId);
        Assert.Equal("Edited Model", application.ProjectName);
    }

    [Fact]
    public void SupervisorCenterKeepsVerificationAsIndependentSource()
    {
        var source = Read("Components", "Pages", "SupervisorReviewCenter.razor");

        Assert.Contains("_sources.Add(new ApprovalSourceDto(VerificationSourceCode, \"驗證申請\"", source);
        Assert.Contains("VerificationApplications.ListForLeaderReviewAsync", source);
        Assert.Contains("/verification-reviews/{item.Id}", source);
        Assert.Contains("item.ValidationRequest?.FormSnapshot?.ProductTypeLabel", source);
        Assert.DoesNotContain("VerificationApplications.AcceptAsync", source);
    }

    [Fact]
    public void ServiceAppliesTheSameReviewScopeToListsDetailsAndActions()
    {
        var source = Read("Services", "Implementations", "VerificationApplicationService.cs");

        Assert.Contains("x.ApplicantAccount != account && (isAdmin || x.AssignedLeaderAccount == account)", source);
        Assert.Contains("x.ApplicantAccount != account &&", source);
        Assert.Equal(3, Count(source, "VerificationApplicationSecurity.EnsureReviewerAccess(entity, user);"));
        Assert.Contains("await _businessWrite.DemandAsync(cancellationToken);", source);
    }

    [Fact]
    public void DirectSubmitAndManualAcceptShareRecordCreationAndAuditIsDurable()
    {
        var source = Read("Services", "Implementations", "VerificationApplicationService.cs");

        Assert.Equal(2, Count(source, "await CreateRecordAndAcceptAsync("));
        Assert.Contains("ApprovalAuditActions.ApprovalBypassed", source);
        Assert.Contains("ApprovalAuditActions.VerificationContentEdited", source);
        Assert.Contains("FromSqlInterpolated", source);
        Assert.DoesNotContain("PendingSubmissions.Add", source);
    }

    [Fact]
    public void ReviewDetailUsesOriginalSnapshotForViewingAndEditing()
    {
        var review = Read("Components", "Pages", "VerificationReviewDetail.razor");
        var service = Read("Services", "Implementations", "VerificationApplicationService.cs");

        Assert.Contains("<ValidationRequestSubmissionView", review);
        Assert.Contains("<ValidationRequestPreview", review);
        Assert.Contains("UpdateForLeaderReviewAsync", review);
        Assert.Contains("original.FormSnapshot", service);
        Assert.Contains("GetPreviewAsync(original.TemplateVersionId", service);
        Assert.Contains("ResolveRoutingAsync(selection.DepartmentOptionId", service);
    }

    [Fact]
    public void ApplicantUiCollectsExpectedDateOnlyForDirectApproval()
    {
        var form = Read("Components", "Pages", "ValidationRequests.razor");
        var detail = Read("Components", "Pages", "VerificationApplicationDetail.razor");

        Assert.Contains("IsDirectApprovalApplicantAsync", form);
        Assert.Contains("_expectedEndDate", form);
        Assert.Contains("IsDirectApprovalApplicantAsync", detail);
        Assert.Contains("_expectedEndDate", detail);
    }

    private static VerificationApplication SubmittedApplication(string applicant, string leader)
    {
        var now = DateTime.UtcNow;
        var departmentId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var application = VerificationApplication.CreateDraft(
            Guid.NewGuid(), "VA-TEST", departmentId, teamId, applicant, "Applicant",
            "applicant@example.test", "RD", null,
            new VerificationApplicationContent
            {
                ProjectName = "Model A",
                Customer = "Customer",
                ProductModel = "Model A",
                RequestedFinishDate = DateOnly.FromDateTime(now.AddDays(7)),
                ValidationRequirement = "Validation"
            }, now);
        application.Submit(new VerificationApplicationRouting(
            departmentId, "RD", "RD", teamId, "TEAM", "Team", "verification", leader, "Leader"), now);
        return application;
    }

    private static ClaimsPrincipal User(string account, bool admin = false)
    {
        var claims = new List<Claim> { new("account", account) };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

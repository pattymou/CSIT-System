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
        Assert.Equal(2, Count(source, "VerificationApplicationSecurity.EnsureReviewerAccess(entity, user);"));
        Assert.Contains("await _businessWrite.DemandAsync(cancellationToken);", source);
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

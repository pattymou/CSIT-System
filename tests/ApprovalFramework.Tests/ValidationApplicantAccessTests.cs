namespace ApprovalFramework.Tests;

public sealed class ValidationApplicantAccessTests
{
    [Theory]
    [InlineData("ValidationRequests.razor")]
    [InlineData("VerificationApplications.razor")]
    [InlineData("VerificationApplicationDetail.razor")]
    [InlineData("VerificationApplicationEdit.razor")]
    public void ApplicantPagesAllowRdAndCsitWhileUsingIdentityAwareLayout(string file)
    {
        var source = Read("Components", "Pages", file);
        Assert.Contains("SystemAuthorization.Policies.ReservationUser", source);
        Assert.Contains("@layout ApplicantLayout", source);
        Assert.DoesNotContain("@attribute [Authorize(Policy = SystemAuthorization.Policies.RdApplicant)]", source);
    }

    [Fact]
    public void ApplicantLayoutKeepsInternalAndExternalNavigationSeparate()
    {
        var source = Read("Components", "Layout", "ApplicantLayout.razor");
        Assert.Contains("SystemAuthorization.Policies.CsitStaff", source);
        Assert.Contains("<MainLayout Body=\"@Body\" />", source);
        Assert.Contains("<RdLayout Body=\"@Body\" />", source);
    }

    [Fact]
    public void ExistingWriteAndOwnershipGuardsRemainInTheSharedWorkflow()
    {
        var service = Read("Services", "Implementations", "VerificationApplicationService.cs");
        Assert.Contains("await _businessWrite.DemandAsync(cancellationToken);", service);
        Assert.Contains("VerificationApplicationSecurity.EnsureApplicantOwnership(entity, account);", service);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

using System.Diagnostics;

namespace ApprovalFramework.Tests;

public sealed class DevOidcReleaseGateTests
{
    [Fact]
    public async Task StructuralChallengeGate_AcceptsValidFlowAndRejectsRegressions()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tools", "dev-deploy", "Test-DevOidcStructuralSmoke.ps1");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
        foreach (var required in new[]
        {
            "PASS valid_challenge", "PASS wrong_client", "PASS wrong_redirect",
            "PASS no_pkce_s256", "PASS missing_correlation", "PASS insecure_cookies",
            "PASS status_only"
        }) Assert.Contains(required, output);
    }

    [Fact]
    public void Deployer_RequiresStructuralGateBeforeReportingSuccess()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy", "Deploy-DevReady.ps1"));
        var gate = script.IndexOf("Assert-DevOidcStructuralSmoke (", StringComparison.Ordinal);
        var pass = script.IndexOf("DEV_SMOKE_PASS $ExpectedSha", StringComparison.Ordinal);
        Assert.True(gate >= 0 && pass > gate);
        Assert.Contains("DEV_PREVIOUS_RUNTIME_HEALTHY", script);
        Assert.Contains("DEPLOY_PASS $Sha", script);
        Assert.Contains("DevOidcStructuralSmoke.ps1", script);
        Assert.Contains("Write-DevOidcAcceptance $Sha $candidatePid", script);
        Assert.Contains("RealLogin='NotVerified'", script);
        Assert.Contains("Assert-DevLogAcl", script);
        Assert.Contains("AddDays(-180)", script);
        Assert.Contains("AddDays(-365)", script);
        var release = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy", "Release-DevReady.ps1"));
        Assert.Contains("DevOidcStructuralSmoke.ps1", release);
        Assert.Contains("protected DEV watcher automation does not match", release);
        Assert.True(release.IndexOf("protected DEV watcher automation does not match",
            StringComparison.Ordinal) < release.IndexOf("git tag $tag", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}

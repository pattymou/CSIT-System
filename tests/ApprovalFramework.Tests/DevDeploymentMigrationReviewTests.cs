using System.Diagnostics;

namespace ApprovalFramework.Tests;

public sealed class DevDeploymentMigrationReviewTests
{
    [Fact]
    public async Task DbaReviewMarker_GatesMigrationRetryAndExactShaRedeploy()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tools", "dev-deploy", "Test-DevMigrationReview.ps1");
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, $"{output}{Environment.NewLine}{error}");
        Assert.Contains("PASS valid marker", output);
        Assert.Contains("PASS missing marker", output);
        Assert.Contains("PASS SHA mismatch", output);
        Assert.Contains("PASS incomplete migration history", output);
        Assert.Contains("PASS exact-SHA marker repeat use", output);
        Assert.Contains("PASS controlled retry and redeploy", output);

        var deployScript = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy", "Deploy-DevReady.ps1"));
        Assert.Contains("Assert-Smoke $Sha", deployScript);
        Assert.Contains("DEV_ALREADY_CURRENT", deployScript);
        Assert.Contains("DEV_MIGRATION_REVIEW_VALID", deployScript);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

using System.Diagnostics;

namespace ApprovalFramework.Tests;

public sealed class DevDeploymentMigrationReviewTests
{
    [Fact]
    public async Task RuntimeRecovery_FailsClosedAndDoesNotRequireANewReadyTag()
    {
        var root = FindRepositoryRoot();
        var output = await RunPowerShellAsync(root,
            Path.Combine(root, "tools", "dev-deploy", "Test-DevRuntimeRecovery.ps1"));

        Assert.Contains("PASS missing runtime recovers current release", output);
        Assert.Contains("PASS healthy runtime is a no-op", output);
        Assert.Contains("PASS database or storage unavailable blocks recovery", output);
        Assert.Contains("PASS unexpected existing runtime blocks a second process", output);
        Assert.Contains("PASS multiple listeners fail closed", output);
        Assert.Contains("PASS recent attempt is rate limited", output);
        Assert.Contains("PASS restart loop is capped", output);
        Assert.Contains("PASS invalid current manifest fails closed", output);

        var recovery = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy",
            "Restart-DevCurrent.ps1"));
        Assert.DoesNotContain("ls-remote", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Service", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Service", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current.json", recovery);
        Assert.Contains("Production service is not running", recovery);
        Assert.Contains("Production 5010 PID changed", recovery);
        Assert.Contains("https://localhost:7296/signin", recovery);
        Assert.Contains("csit-dev-da40.duckdns.org:7443", recovery);
        Assert.Contains("csit-dev-da40.duckdns.org:7444", recovery);
        Assert.Contains("csit-identity-dev", recovery);
        Assert.Contains("connect/authorize", recovery);

        var watcher = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy",
            "Watch-DevReady.ps1"));
        var installer = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy",
            "Install-DevWatcher.ps1"));
        var updater = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy",
            "Update-DevWatcherAutomation.ps1"));
        Assert.Contains("Restart-DevCurrent.ps1", watcher);
        Assert.Contains("New-ScheduledTaskTrigger -AtStartup", installer);
        Assert.Contains("New-ScheduledTaskTrigger -AtStartup", updater);
    }

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

    private static async Task<string> RunPowerShellAsync(string root, string script)
    {
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

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("PowerShell did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"{output}{Environment.NewLine}{error}");
        return output;
    }
}

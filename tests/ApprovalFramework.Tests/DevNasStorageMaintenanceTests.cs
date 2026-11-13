namespace ApprovalFramework.Tests;

public sealed class DevNasStorageMaintenanceTests
{
    [Fact]
    public void CredentialBootstrapUsesLocalServiceCredentialManagerAndMemoryOnlyTransport()
    {
        var script = Read("Set-DevNasCredential.ps1");
        Assert.Contains("Read-Host 'NAS password", script);
        Assert.Contains("non-admin NAS account limited to the DEV root", script);
        Assert.Contains("-AsSecureString", script);
        Assert.Contains("NamedPipeServerStream", script);
        Assert.Contains("PipeSecurity", script);
        Assert.Contains("S-1-5-19", script);
        Assert.Contains("CRED_TYPE_DOMAIN_PASSWORD", script);
        Assert.Contains("CRED_PERSIST_LOCAL_MACHINE", script);
        Assert.Contains("CredWrite", script);
        Assert.Contains("CredDelete", script);
        Assert.Contains("NT AUTHORITY\\LOCAL SERVICE", script);
        Assert.DoesNotContain("cmdkey", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/pass:", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Export-Clixml", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Content", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessProbeUsesLocalServiceAndCleansUpWithoutChangingAcl()
    {
        var script = Read("Test-DevNasAccess.ps1");
        Assert.Contains(@"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV", script);
        Assert.Contains("NT AUTHORITY\\LOCAL SERVICE", script);
        Assert.Contains("New-ScheduledTaskPrincipal", script);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("Remove-Item -LiteralPath '$target'", script);
        Assert.Contains("Production 5010 PID changed", script);
        Assert.DoesNotContain("Set-Acl", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("icacls", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationIsAuditFirstHashVerifiedAndLeavesOriginalsAndCompletedKmUntouched()
    {
        var script = Read("Migrate-DevFormalStorageToNas.ps1");
        Assert.Contains("DefaultParameterSetName = 'Audit'", script);
        Assert.Contains("ApprovedSourceSha", script);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("Existing NAS target differs and will not be overwritten", script);
        Assert.Contains("OriginalsRetained = $true", script);
        Assert.Contains("CompletedKmJobsTouched = $false", script);
        Assert.Contains("DEV_NAS_ACCESS_PASS", script);
        Assert.Contains("runtime.before.json", script);
        Assert.Contains("Production 5010 PID changed", script);
        Assert.DoesNotContain("DELETE FROM km_integration_outbox", script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE km_integration_outbox", script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $item.SourcePath", script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Acl -LiteralPath $nasRoot", script,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeRecoveryAndDeploymentRequireApprovedNasRoots()
    {
        foreach (var name in new[] { "Deploy-DevReady.ps1", "Restart-DevCurrent.ps1" })
        {
            var script = Read(name);
            Assert.Contains(@"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV", script);
            Assert.Contains("DocumentStorage:RootPath", script);
            Assert.Contains("RawData:NasRootPath", script);
            Assert.Contains("TestReportRootPath", script);
        }
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "tools", "dev-deploy", name));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

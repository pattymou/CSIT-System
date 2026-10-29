using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ApprovalFramework.Tests;

public sealed class DevManagedAssetStorageAclTests
{
    [Fact]
    public void InitializerIsDevScopedAuditedAndApprovalGated()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "dev-deploy",
            "Initialize-DevManagedAssetStorage.ps1"));
        Assert.Contains("D:\\CSIT_Dev_Data\\Assets\\equipment", script);
        Assert.Contains("sit_department_system_dev", script);
        Assert.Contains("csit_runtime_dev", script);
        Assert.Contains("ApprovedEquipmentIds must exactly match every active deletion job", script);
        Assert.Contains("Protected ACL descendants require separate review", script);
        Assert.Contains("DEV_MANAGED_STORAGE_ACL_AUDIT_ONLY", script);
        Assert.Contains("DEV_MANAGED_STORAGE_ACL_APPLIED", script);
        Assert.Contains("RootSddl", script);
        Assert.Contains("RestoreBackup", script);
        Assert.DoesNotContain("Everyone", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" /T", script, StringComparison.OrdinalIgnoreCase);

        foreach (var installer in new[] { "Install-DevWatcher.ps1", "Update-DevWatcherAutomation.ps1" })
            Assert.Contains("Initialize-DevManagedAssetStorage.ps1",
                File.ReadAllText(Path.Combine(root, "tools", "dev-deploy", installer)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void InheritableModifyCoversExistingAndFutureManagedFilesWithoutChangingSibling()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sandbox = Path.Combine(Path.GetTempPath(), "csit-managed-storage-acl-" + Guid.NewGuid().ToString("N"));
        var managed = Path.Combine(sandbox, "equipment");
        var sibling = Path.Combine(sandbox, "approval-staging");
        var legacy = Path.Combine(managed, "legacy-device", "legacy-file");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(sibling);
        var legacyFile = Path.Combine(legacy, "legacy.txt");
        File.WriteAllText(legacyFile, "legacy");
        var siblingSddl = new DirectoryInfo(sibling).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Test identity has no SID.");
        try
        {
            var acl = new DirectoryInfo(managed).GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(managed).SetAccessControl(acl);

            AssertModifyInherited(legacyFile, sid);
            var legacyQuarantine = legacyFile + ".csit-delete-test";
            File.Move(legacyFile, legacyQuarantine);
            File.Delete(legacyQuarantine);

            var modern = Path.Combine(managed, "managed", "equipment", "future-device");
            Directory.CreateDirectory(modern);
            var modernFile = Path.Combine(modern, "future.txt");
            File.WriteAllText(modernFile, "future");
            AssertModifyInherited(modernFile, sid);
            var modernQuarantine = modernFile + ".csit-delete-test";
            File.Move(modernFile, modernQuarantine);
            File.Delete(modernQuarantine);

            Assert.Equal(siblingSddl, new DirectoryInfo(sibling)
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertModifyInherited(string path, SecurityIdentifier sid)
    {
        var rules = new FileInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.Contains(rules, rule => rule.IdentityReference == sid && rule.IsInherited &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

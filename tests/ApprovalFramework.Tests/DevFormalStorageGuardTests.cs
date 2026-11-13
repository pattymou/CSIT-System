using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class DevFormalStorageGuardTests
{
    [Fact]
    public void ConfigurationRequiresApprovedUncRootsAndLocalStaging()
    {
        var valid = Configuration(new Dictionary<string, string?>
        {
            ["DocumentStorage:RootPath"] = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\DocumentLibrary",
            ["RawData:NasRootPath"] = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\RawData",
            ["DocumentStaging:RootPath"] = @"C:\ProgramData\CSIT-DEV\document-staging"
        });
        DevFormalStorageGuard.ValidateConfiguration(valid, requireAccessible: false);

        var business = Configuration(new Dictionary<string, string?>
        {
            [DevFormalStorageGuard.BusinessHierarchyKey] = "true",
            ["DocumentStorage:RootPath"] = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV",
            ["RawData:NasRootPath"] = @"\\AI-NAS\Workspace_B\CSIT_TECHNICAL_DEV\RawData",
            ["DocumentStaging:RootPath"] = @"C:\ProgramData\CSIT-DEV\document-staging"
        });
        DevFormalStorageGuard.ValidateConfiguration(business, requireAccessible: false);

        var localFormal = Configuration(new Dictionary<string, string?>
        {
            ["DocumentStorage:RootPath"] = @"C:\ProgramData\CSIT-DEV\documents",
            ["RawData:NasRootPath"] = @"D:\CSIT_Dev_Data\RawData",
            ["DocumentStaging:RootPath"] = @"C:\ProgramData\CSIT-DEV\document-staging"
        });
        Assert.Throws<InvalidOperationException>(() =>
            DevFormalStorageGuard.ValidateConfiguration(localFormal, requireAccessible: false));

        var mixedStaging = Configuration(new Dictionary<string, string?>
        {
            [DevFormalStorageGuard.BusinessHierarchyKey] = "true",
            ["DocumentStorage:RootPath"] = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV",
            ["RawData:NasRootPath"] = @"\\AI-NAS\Workspace_B\CSIT_TECHNICAL_DEV\RawData",
            ["DocumentStaging:RootPath"] = @"\\AI-NAS\Workspace_B\CSIT_RAW_DATA_DEV\Staging"
        });
        Assert.Throws<InvalidOperationException>(() =>
            DevFormalStorageGuard.ValidateConfiguration(mixedStaging, requireAccessible: false));
    }

    [Fact]
    public async Task DatabaseRequiresEveryApprovedFormalRootExactlyOnce()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("dev-formal-storage-" + Guid.NewGuid().ToString("N")).Options;
        await using var db = new AppDbContext(options);
        var now = DateTime.UtcNow;
        foreach (var expected in DevFormalStorageGuard.DatabaseRoots)
            db.SystemOptions.Add(new SystemOption
            {
                Id = Guid.NewGuid(), Category = expected.Key.Category, Name = expected.Key.Name,
                Value = expected.Value, IsEnabled = true, CreatedAt = now, UpdatedAt = now
            });
        await db.SaveChangesAsync();

        await DevFormalStorageGuard.ValidateDatabaseAsync(db, businessHierarchyEnabled: true,
            requireAccessible: false);

        var equipment = await db.SystemOptions.SingleAsync(x =>
            x.Category == "AssetRootPath" && x.Name == "equipment");
        equipment.Value = @"D:\CSIT_Dev_Data\Assets\equipment";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevFormalStorageGuard.ValidateDatabaseAsync(db, businessHierarchyEnabled: true,
                requireAccessible: false));
    }

    [Fact]
    public void DevelopmentSettingsDoNotUseLocalFormalRoots()
    {
        var root = FindRepositoryRoot();
        var settings = new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile("appsettings.Development.json")
            .Build();
        Assert.Equal(DevFormalStorageGuard.ConfigurationRoots["DocumentStorage:RootPath"],
            settings["DocumentStorage:RootPath"]);
        Assert.Equal(DevFormalStorageGuard.ConfigurationRoots["RawData:NasRootPath"],
            settings["RawData:NasRootPath"]);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

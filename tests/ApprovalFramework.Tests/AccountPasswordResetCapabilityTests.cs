using System.Text.Json;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class AccountPasswordResetCapabilityTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OldAccountListPayloadFailsClosedWhenCapabilityIsMissing()
    {
        const string json = """
            {
              "identityUserId": "11111111-1111-1111-1111-111111111111",
              "employeeNo": "E001",
              "account": "legacy",
              "displayName": "Legacy Account",
              "department": "Engineering",
              "teamCode": "Wireless",
              "position": "Engineer",
              "extension": null,
              "email": "legacy@askey.com",
              "csitRole": "CsitStaff",
              "dataAccessLevel": "Editor",
              "accountStatus": "Active",
              "systemAccessStatus": "Active",
              "createdAt": "2026-10-25T00:00:00Z",
              "createdByIdentityUserId": null,
              "createdByAccount": null
            }
            """;

        var account = JsonSerializer.Deserialize<SharedIdentityAccountListItem>(json, WebJson);

        Assert.NotNull(account);
        Assert.False(account.CanReissueTemporaryPassword);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AccountListPayloadPreservesServerCapability(bool expected)
    {
        var json = $$"""
            {
              "identityUserId": "22222222-2222-2222-2222-222222222222",
              "employeeNo": null,
              "account": "example",
              "displayName": "Example Account",
              "department": "Engineering",
              "teamCode": null,
              "position": null,
              "extension": null,
              "email": "example@askey.com",
              "csitRole": "CsitStaff",
              "dataAccessLevel": "Editor",
              "accountStatus": "Active",
              "systemAccessStatus": "Active",
              "createdAt": "2026-10-25T00:00:00Z",
              "createdByIdentityUserId": null,
              "createdByAccount": null,
              "canReissueTemporaryPassword": {{expected.ToString().ToLowerInvariant()}}
            }
            """;

        var account = JsonSerializer.Deserialize<SharedIdentityAccountListItem>(json, WebJson);

        Assert.NotNull(account);
        Assert.Equal(expected, account.CanReissueTemporaryPassword);
    }

    [Fact]
    public void UiAndServiceKeepCapabilityAndSelfChecksAsDefenseInDepth()
    {
        var page = Read("Components", "Pages", "System", "CreateInternalAccount.razor");
        var service = Read("Services", "InternalAccountCreationService.cs");

        Assert.Contains("disabled=\"@(!CanReissueTemporaryPassword(account))\"", page);
        Assert.Contains("account.IdentityUserId != _options.Actor.IdentityUserId", page);
        Assert.Contains("account.CanReissueTemporaryPassword", page);
        Assert.Contains("InternalAccountCreationService.TemporaryPasswordReissueUnsupportedMessage", page);
        Assert.Contains(InternalAccountCreationService.TemporaryPasswordReissueUnsupportedMessage, service);
        Assert.Contains("if (!target.CanReissueTemporaryPassword)", service);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SIT.DepartmentSystem.Web.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

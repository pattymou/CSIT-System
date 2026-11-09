using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.LoadTesting;

public static class LoadTestAuthenticationDefaults
{
    public const string Scheme = "CSIT.LoadTest";
    public const string Header = "X-CSIT-Load-Identity";
}

/// <summary>
/// Ephemeral authentication for the isolated LoadTest environment. The scheme is never
/// registered in Development or Production and every identity is bound to one run by HMAC.
/// </summary>
public sealed class LoadTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var runId = configuration["LoadTest:RunId"];
        var secret = configuration["LoadTest:IdentitySecret"];
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            return Task.FromResult(AuthenticateResult.Fail("Load-test identity configuration is incomplete."));
        if (!Request.Headers.TryGetValue(LoadTestAuthenticationDefaults.Header, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var parts = values.ToString().Split('|');
        if (parts.Length != 8)
            return Task.FromResult(AuthenticateResult.Fail("Malformed load-test identity."));
        var canonical = string.Join('|', parts[..7]);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[7]); }
        catch (FormatException) { return Task.FromResult(AuthenticateResult.Fail("Malformed load-test signature.")); }
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return Task.FromResult(AuthenticateResult.Fail("Invalid load-test signature."));

        var account = parts[0].Trim().ToLowerInvariant();
        var role = parts[1].Trim();
        var scope = parts[2].Trim();
        var access = parts[3].Trim();
        var department = parts[4].Trim();
        var displayName = parts[5].Trim();
        if (!string.Equals(parts[6], runId, StringComparison.Ordinal) ||
            !account.StartsWith($"load-{runId.ToLowerInvariant()}-", StringComparison.Ordinal) ||
            role is not ("Employee" or "TeamLeader" or "Admin" or "ReadOnly") ||
            scope is not (SystemAuthorization.AccessScopes.CsitStaff or SystemAuthorization.AccessScopes.RdApplicant) ||
            access is not (SystemAuthorization.DataAccessLevels.Editor or SystemAuthorization.DataAccessLevels.ReadOnly) ||
            string.IsNullOrWhiteSpace(department) || string.IsNullOrWhiteSpace(displayName))
            return Task.FromResult(AuthenticateResult.Fail("Load-test identity is outside the allowed synthetic profile."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account),
            new(ClaimTypes.Name, displayName),
            new("account", account),
            new("display_name", displayName),
            new("department", department),
            new("email", $"{account}@load.invalid"),
            new("identity_sub", SyntheticIdentityId(account).ToString()),
            new("team_code", "LOADTEAM"),
            new(SystemAuthorization.AccessScopeClaim, scope),
            new(SystemAuthorization.DataAccessLevelClaim, access),
            new("load_test_run_id", runId)
        };
        if (role == "Admin") claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    private static Guid SyntheticIdentityId(string account)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(account));
        return new Guid(bytes);
    }
}

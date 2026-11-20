using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SIT.DepartmentSystem.Web.Services;

public sealed record SharedIdentitySessionState(string? Account, long SecurityVersion, long AuthorizationVersion);
public sealed record SharedIdentitySessionResult(bool IsCurrent, string Kind);

public sealed class SharedIdentitySessionValidator(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
        => (await CheckAsync(principal, cancellationToken)).IsCurrent;

    public async Task<SharedIdentitySessionResult> CheckAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue("identity_sub"), out var subject) ||
            !long.TryParse(principal.FindFirstValue("identity_security_version"), out var securityVersion) ||
            !long.TryParse(principal.FindFirstValue("identity_authorization_version"), out var authorizationVersion))
            return new(false, "SessionClaims");
        var clientId = configuration["SharedIdentity:ClientId"];
        var clientSecret = configuration["SharedIdentity:ClientSecret"];
        var authority = configuration["SharedIdentity:Authority"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(authority)) return new(false, "Configuration");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{authority.TrimEnd('/')}/internal/csit/session-status/{subject}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, "IdentityStatus");
            var state = await response.Content.ReadFromJsonAsync<SharedIdentitySessionState>(cancellationToken);
            if (state is null) return new(false, "IdentityResponse");
            if (state.SecurityVersion != securityVersion ||
                state.AuthorizationVersion != authorizationVersion) return new(false, "VersionMismatch");
            if (!string.Equals(state.Account, principal.FindFirstValue("account"), StringComparison.Ordinal))
                return new(false, "AccountMismatch");
            return new(true, "Accepted");
        }
        catch (HttpRequestException) { return new(false, "IdentityTransport"); }
        catch (TaskCanceledException) { return new(false, "IdentityTimeout"); }
        catch (JsonException) { return new(false, "IdentityResponse"); }
    }
}

public sealed class SharedIdentityCookieEvents(SharedIdentitySessionValidator validator) : CookieAuthenticationEvents
{
    public override Task SignedIn(CookieSignedInContext context)
    {
        OidcDiagnostics.Write(context.HttpContext, "CookieIssued", "Accepted",
            OidcDiagnostics.Flow(context.Properties));
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        context.HttpContext.Items[OidcDiagnostics.RequestFlowKey] =
            OidcDiagnostics.Flow(context.Properties);
        var result = context.Principal is null ? new(false, "NoPrincipal") :
            await validator.CheckAsync(context.Principal, context.HttpContext.RequestAborted);
        OidcDiagnostics.Write(context.HttpContext, "SessionValidation", result.Kind,
            OidcDiagnostics.Flow(context.Properties));
        if (!result.IsCurrent)
        {
            context.HttpContext.Items[KmIntegration.KmQueueRequestDiagnostics.SessionFailureItem] = result.Kind;
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}

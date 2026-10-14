using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SIT.DepartmentSystem.Web.Services;

public sealed record SharedIdentitySessionState(string? Account, long SecurityVersion, long AuthorizationVersion);

public sealed class SharedIdentitySessionValidator(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue("identity_sub"), out var subject) ||
            !long.TryParse(principal.FindFirstValue("identity_security_version"), out var securityVersion) ||
            !long.TryParse(principal.FindFirstValue("identity_authorization_version"), out var authorizationVersion))
            return false;
        var clientId = configuration["SharedIdentity:ClientId"];
        var clientSecret = configuration["SharedIdentity:ClientSecret"];
        var authority = configuration["SharedIdentity:Authority"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(authority)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{authority.TrimEnd('/')}/internal/csit/session-status/{subject}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var state = await response.Content.ReadFromJsonAsync<SharedIdentitySessionState>(cancellationToken);
            return state is not null && state.SecurityVersion == securityVersion &&
                   state.AuthorizationVersion == authorizationVersion &&
                   string.Equals(state.Account, principal.FindFirstValue("account"), StringComparison.Ordinal);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}

public sealed class SharedIdentityCookieEvents(SharedIdentitySessionValidator validator) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null ||
            !await validator.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}

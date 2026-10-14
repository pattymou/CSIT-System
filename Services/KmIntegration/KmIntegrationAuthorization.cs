using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class KmIntegrationAuthorization
{
    public const string Scheme = "KmIntegrationBearer";
    public const string Policy = "KmIntegrationServicePrincipal";
    public const string DocumentReadPolicy = "KmIntegrationDocumentRead";
    public const string StatusWritePolicy = "KmIntegrationStatusWrite";
    public const string Audience = "csit.integration.dev";
    public const string DocumentReadScope = "csit.document.read";
    public const string StatusWriteScope = "csit.ingestion.status.write";
}

// The service bearer validator is deliberately unavailable until Client Credentials is delivered.
public sealed class DisabledKmIntegrationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

public sealed record KmServicePrincipalRequirement(string RequiredScope) : IAuthorizationRequirement;

public sealed class KmServicePrincipalHandler(IOptions<KmIntegrationAuthOptions> configured)
    : AuthorizationHandler<KmServicePrincipalRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        KmServicePrincipalRequirement requirement)
    {
        var options = configured.Value;
        var identity = context.User.Identity;
        var client = context.User.FindFirst("client_id")?.Value;
        var subject = context.User.FindFirst("sub")?.Value;
        var audience = context.User.FindAll("aud").Select(x => x.Value);
        var principalType = context.User.FindFirst("principal_type")?.Value;
        var scopes = context.User.FindAll("scope")
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (identity?.IsAuthenticated == true &&
            string.Equals(options.Audience, KmIntegrationAuthorization.Audience, StringComparison.Ordinal) &&
            string.Equals(options.ClientId, "km-csit-integration-dev", StringComparison.Ordinal) &&
            string.Equals(client, options.ClientId, StringComparison.Ordinal) &&
            string.Equals(subject, options.ClientId, StringComparison.Ordinal) &&
            string.Equals(principalType, "service", StringComparison.Ordinal) &&
            audience.Contains(options.Audience, StringComparer.Ordinal) &&
            scopes.Contains(requirement.RequiredScope, StringComparer.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

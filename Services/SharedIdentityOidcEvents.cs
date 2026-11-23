using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services;

public static class SharedIdentityOidcEvents
{
    private const string PhaseKey = "csit_oidc_phase";

    public static OpenIdConnectEvents Create()
    {
        return new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                OidcDiagnostics.Write(context.HttpContext, "Challenge", flow:
                    OidcDiagnostics.NewFlow(context.Properties));
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                OidcDiagnostics.Write(context.HttpContext, "CallbackMessage", state:
                    OidcDiagnostics.HasState(context.Request), correlation:
                    OidcDiagnostics.HasCorrelation(context.Request), nonce:
                    OidcDiagnostics.HasNonce(context.Request));
                return Task.CompletedTask;
            },
            OnAuthorizationCodeReceived = context =>
            {
                context.HttpContext.Items[PhaseKey] = "CodeExchange";
                OidcDiagnostics.Write(context.HttpContext, "CodeExchangeStart", "StateCorrelationAccepted",
                    OidcDiagnostics.Flow(context.Properties));
                return Task.CompletedTask;
            },
            OnTokenResponseReceived = context =>
            {
                context.HttpContext.Items[PhaseKey] = "TokenResponse";
                OidcDiagnostics.Write(context.HttpContext, "TokenResponse", "Received",
                    OidcDiagnostics.Flow(context.Properties));
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                context.HttpContext.Items[PhaseKey] = "IdTokenValidated";
                var flow = OidcDiagnostics.Flow(context.Properties);
                OidcDiagnostics.Write(context.HttpContext, "IdTokenValidated", "NoncePending", flow);
                return Task.CompletedTask;
            },
            OnTicketReceived = async context =>
            {
                context.HttpContext.Items[PhaseKey] = "OidcTicket";
                var flow = OidcDiagnostics.Flow(context.Properties);
                OidcDiagnostics.Write(context.HttpContext, "OidcTicket", "ProtocolValidated", flow);
                context.HttpContext.Items[PhaseKey] = "ClaimsMapping";
                var adapter = context.HttpContext.RequestServices.GetRequiredService<SharedIdentityClaimsAdapter>();
                var mapped = context.Principal is null ? null :
                    await adapter.MapWithResultAsync(context.Principal, context.HttpContext.RequestAborted);
                if (mapped?.Principal is null)
                {
                    OidcDiagnostics.Write(context.HttpContext, "ClaimsMapping", mapped?.Kind ?? "NoPrincipal",
                        flow);
                    context.HandleResponse();
                    context.Response.Redirect("/signin?error=1");
                }
                else
                {
                    OidcDiagnostics.Write(context.HttpContext, "ClaimsMapping", "Accepted", flow);
                    context.Principal = mapped.Principal;
                    context.ReturnUri = ResolveLanding(mapped.Principal,
                        context.ReturnUri ?? context.Properties?.RedirectUri);
                }
            },
            OnRemoteFailure = context =>
            {
                OidcDiagnostics.Write(context.HttpContext, "RemoteFailure",
                    ClassifyOidcFailure(context.Failure, context.HttpContext),
                    OidcDiagnostics.Flow(context.Properties));
                context.HandleResponse();
                context.Response.Redirect("/signin?error=1");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                OidcDiagnostics.Write(context.HttpContext, "AuthenticationFailure",
                    ClassifyOidcFailure(context.Exception, context.HttpContext),
                    OidcDiagnostics.Flow(context.Properties));
                context.HandleResponse();
                context.Response.Redirect("/signin?error=1");
                return Task.CompletedTask;
            }
        };
    }

    public static string ResolveLanding(System.Security.Claims.ClaimsPrincipal principal, string? returnUri)
    {
        var isRd = principal.HasClaim(SystemAuthorization.AccessScopeClaim,
            SystemAuthorization.AccessScopes.RdApplicant);
        var defaultLanding = isRd ? "/rd" : "/home";
        if (string.IsNullOrWhiteSpace(returnUri)) return defaultLanding;
        var path = returnUri.Split('?', '#')[0].TrimEnd('/');
        return path.Length == 0 || (isRd && path.Equals("/home", StringComparison.OrdinalIgnoreCase))
            ? defaultLanding : returnUri;
    }

    private static string ClassifyOidcFailure(Exception? exception, HttpContext context)
    {
        var cause = exception?.GetBaseException();
        var message = cause?.Message ?? string.Empty;
        if (message.Contains("correlation", StringComparison.OrdinalIgnoreCase)) return "Correlation";
        if (message.Contains("nonce", StringComparison.OrdinalIgnoreCase)) return "Nonce";
        if (message.Contains("state", StringComparison.OrdinalIgnoreCase)) return "State";
        if (message.Contains("access_denied", StringComparison.OrdinalIgnoreCase)) return "AuthorizationDenied";
        if (message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)) return "ClientRejected";
        if (message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)) return "CodeRejected";
        var phase = context.Items[PhaseKey] as string;
        if (phase == "CodeExchange") return "TokenEndpoint";
        if (phase == "ClaimsMapping") return "ClaimsMapping";
        if (phase is "TokenResponse" or "IdTokenValidated") return "IdTokenValidation";
        if (cause is HttpRequestException or TaskCanceledException) return "Transport";
        if (cause is SecurityTokenException or OpenIdConnectProtocolException) return "IdTokenValidation";
        return "Other";
    }
}

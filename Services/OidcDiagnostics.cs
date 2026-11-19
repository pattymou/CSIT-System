using Microsoft.AspNetCore.Authentication;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services;

// All fields are fixed classifications or presence flags. Never pass protocol values here.
public static class OidcDiagnostics
{
    public const string FlowKey = "csit_oidc_flow_id";
    public const string RequestFlowKey = "csit_oidc_request_flow_id";

    public static string NewFlow(AuthenticationProperties properties)
    {
        var flowId = Guid.NewGuid().ToString("N");
        properties.Items[FlowKey] = flowId;
        return flowId;
    }

    public static string Flow(AuthenticationProperties? properties)
        => properties is not null && properties.Items.TryGetValue(FlowKey, out var value) &&
           Guid.TryParseExact(value, "N", out _) ? value! : "Unknown";

    public static bool HasState(HttpRequest request)
    {
        if (request.Query.ContainsKey("state")) return true;
        try { return request.HasFormContentType && request.Form.ContainsKey("state"); }
        catch (InvalidDataException) { return false; }
        catch (BadHttpRequestException) { return false; }
    }

    public static bool HasCorrelation(HttpRequest request)
        => request.Cookies.Keys.Any(key => key.StartsWith(".AspNetCore.Correlation.",
            StringComparison.Ordinal));

    public static bool HasNonce(HttpRequest request)
        => request.Cookies.Keys.Any(key => key.StartsWith(".AspNetCore.OpenIdConnect.Nonce.",
            StringComparison.Ordinal));

    public static void Write(HttpContext context, string stage, string kind = "None",
        string flow = "Unknown", bool? state = null, bool? correlation = null,
        bool? nonce = null, int? status = null)
    {
        var configuredSha = context.RequestServices.GetRequiredService<IConfiguration>()["DevDeployment:Sha"];
        var sha = configuredSha is { Length: 40 } && configuredSha.All(Uri.IsHexDigit)
            ? configuredSha : "Unknown";
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CSIT.Oidc");
        logger.LogInformation(
            "OIDC phase Stage={Stage} Kind={Kind} FlowId={FlowId} Utc={Utc:o} RequestId={RequestId} RuntimeSha={RuntimeSha} Method={Method} PublicIngress={PublicIngress} StatePresent={StatePresent} CorrelationPresent={CorrelationPresent} NoncePresent={NoncePresent} HttpStatus={HttpStatus}",
            stage, kind, flow, DateTimeOffset.UtcNow, context.TraceIdentifier, sha,
            context.Request.Method, DevPublicWebIngress.IsPublicRequest(context), state, correlation,
            nonce, status);
    }
}

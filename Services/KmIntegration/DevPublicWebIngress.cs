namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class DevPublicWebIngress
{
    public const int ExistingPublicPort = 7443;

    public static bool IsPublicRequest(HttpContext context) =>
        string.Equals(context.Request.Host.Host, DevKmServiceIngress.Hostname,
            StringComparison.OrdinalIgnoreCase) &&
        context.Connection.LocalPort == 7296 && context.Request.Host.Port == ExistingPublicPort;

    public static bool IsAllowed(HttpContext context)
    {
        if (!IsPublicRequest(context)) return true;

        return !context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/_framework/debug", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/_vs", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/counter", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase);
    }
}

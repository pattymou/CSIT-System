namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class DevPublicWebIngress
{
    public const int Port = 7445;

    public static bool IsAllowed(HttpContext context)
    {
        if (context.Connection.LocalPort != Port) return true;
        if (!string.Equals(context.Request.Host.Host, DevKmServiceIngress.Hostname,
                StringComparison.OrdinalIgnoreCase) || context.Request.Host.Port != Port)
            return false;

        return !context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/_framework/debug", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/_vs", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/counter", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase);
    }
}

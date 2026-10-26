using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static partial class DevKmServiceIngress
{
    public const int Port = 7444;
    public const string Hostname = "csit-dev-da40.duckdns.org";
    public const string CertificateThumbprintVariable = "CSIT_DEV_7444_CERT_THUMBPRINT";

    public static X509Certificate2 LoadCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .SingleOrDefault();
        if (certificate is null || !certificate.HasPrivateKey ||
            DateTimeOffset.UtcNow < certificate.NotBefore ||
            DateTimeOffset.UtcNow >= certificate.NotAfter ||
            !certificate.MatchesHostname(Hostname, allowWildcards: false, allowCommonName: false))
            throw new InvalidOperationException("CSIT DEV service ingress TLS certificate is unavailable or invalid.");

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        if (!chain.Build(certificate))
            throw new InvalidOperationException("CSIT DEV service ingress TLS certificate chain is invalid.");
        return certificate;
    }

    public static bool IsAllowed(HttpContext context)
    {
        if (context.Connection.LocalPort != Port) return true;
        var path = context.Request.Path.Value ?? string.Empty;
        return (HttpMethods.IsPost(context.Request.Method) &&
                (path.Equals("/api/integrations/km/v1/ingestion-status", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("/api/integrations/km/v1/document-deletion-status", StringComparison.OrdinalIgnoreCase))) ||
               (HttpMethods.IsGet(context.Request.Method) && DocumentContentPath().IsMatch(path));
    }

    [GeneratedRegex(@"^/api/integrations/km/v1/documents/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/versions/[1-9][0-9]*/content$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentContentPath();
}

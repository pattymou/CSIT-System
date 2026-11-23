using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Services;

// An allowlist survives application restarts. Missing or unreadable records fail closed.
public sealed class CsitOidcSessionStore(IConfiguration configuration)
{
    public const string ClaimType = "csit_session_id";
    private readonly string _directory = configuration["SharedIdentity:SessionDirectory"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSIT-DEV", "state", "oidc-sessions");

    public string Create(ClaimsPrincipal principal, DateTimeOffset expiresUtc)
    {
        if (expiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Cannot create an expired CSIT session.");
        Directory.CreateDirectory(_directory);
        CleanupExpired();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = new SessionRecord(principal.FindFirstValue("identity_sub"), expiresUtc);
        if (string.IsNullOrWhiteSpace(record.Subject))
            throw new InvalidOperationException("CSIT session has no Shared Identity subject.");
        // CreateNew prevents replacement; the cookie is only sent after the write has completed.
        using (var file = new FileStream(PathFor(id), FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, record);
            file.Flush(flushToDisk: true);
        }
        return id;
    }

    public bool IsActive(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimType);
        if (!IsValidId(id)) return false;
        try
        {
            using var file = new FileStream(PathFor(id!), FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = JsonSerializer.Deserialize<SessionRecord>(file);
            return record is not null && record.ExpiresUtc > DateTimeOffset.UtcNow &&
                   string.Equals(record.Subject, principal.FindFirstValue("identity_sub"), StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimType);
        if (!IsValidId(id)) return;
        try { File.Delete(PathFor(id!)); }
        catch (FileNotFoundException) { }
        // Other I/O errors propagate: logout must not appear successful if revocation failed.
    }

    public void CleanupExpired()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.session"))
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var record = JsonSerializer.Deserialize<SessionRecord>(file);
                    if (record is not null && record.ExpiresUtc > DateTimeOffset.UtcNow) continue;
                }
                File.Delete(path);
            }
            catch (IOException) { } // Concurrent validation or deletion; retry on a later sign-in.
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { } // Invalid records never validate and can be removed later.
        }
    }

    private string PathFor(string id) => Path.Combine(_directory, id + ".session");
    private static bool IsValidId(string? id) => id is { Length: 64 } &&
        id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed record SessionRecord(string? Subject, DateTimeOffset ExpiresUtc);
}

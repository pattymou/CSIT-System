using System.Security.Cryptography;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed record VerificationCaseDeletionPhysicalFile(
    string Kind, Guid SourceId, string Root, string Path, string Sha256);

// The manifest contains individual files only. No Model Name or project directory is ever
// recursively removed. A retry uses this durable manifest after the DB rows are gone.
public static class VerificationCaseDeletionPhysicalFiles
{
    public static async Task<VerificationCaseDeletionPhysicalFile> InspectAsync(
        string kind, Guid sourceId, string root, string path,
        CancellationToken cancellationToken = default)
    {
        var safe = Validate(root, path);
        if (!File.Exists(safe)) throw new FileNotFoundException("Case deletion file is missing.");
        await using var stream = new FileStream(safe, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return new(kind, sourceId, Path.GetFullPath(root), safe, hash);
    }

    public static async Task DeleteAsync(VerificationCaseDeletionPhysicalFile entry,
        CancellationToken cancellationToken = default)
    {
        var safe = Validate(entry.Root, entry.Path);
        if (File.Exists(safe))
        {
            await VerifyAsync(entry, allowMissing: true, cancellationToken: cancellationToken);
            File.Delete(safe);
        }
        var root = Path.GetFullPath(entry.Root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(safe);
        while (parent is not null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) break;
            Directory.Delete(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    public static async Task VerifyAsync(VerificationCaseDeletionPhysicalFile entry,
        bool allowMissing = false, CancellationToken cancellationToken = default)
    {
        var safe = Validate(entry.Root, entry.Path);
        if (!File.Exists(safe))
        {
            if (allowMissing) return; // A prior cleanup attempt may already have removed it.
            throw new FileNotFoundException("A case deletion file disappeared after inventory.");
        }
        await using var stream = new FileStream(safe, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A case deletion file changed after inventory.");
    }

    public static string Validate(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Case deletion file path is not absolute.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Case deletion file escapes its configured root.");
        var cursor = fullPath;
        while (cursor.Length >= fullRoot.Length)
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Case deletion file path contains a reparse point.");
            }
            if (string.Equals(cursor, fullRoot, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor)
                ?? throw new InvalidOperationException("Case deletion path cannot be resolved.");
        }
        return fullPath;
    }
}

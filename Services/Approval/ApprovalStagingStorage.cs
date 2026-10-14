using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Config;

namespace SIT.DepartmentSystem.Web.Services.Approval;

public sealed class ApprovalStagingRootResolver(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<RawDataSettings> rawData,
    IWebHostEnvironment environment) : IApprovalStagingRootResolver
{
    public async Task<string> GetRootAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var root = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == "ApprovalStagingRoot" && x.Name == "default" && x.IsEnabled)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("ApprovalStagingRoot 尚未設定為獨立的絕對路徑。");

        root = Path.GetFullPath(root.Trim());
        if (!Directory.Exists(root))
            throw new InvalidOperationException("ApprovalStagingRoot 不存在或無法讀取。");

        var formalRoots = await db.SystemOptions.AsNoTracking()
            .Where(x => x.IsEnabled && (x.Category == "ThreeLevelRootPath"
                || x.Category == "TestReportRootPath" || x.Category == "AssetRootPath"))
            .Select(x => x.Value).ToListAsync(cancellationToken);
        formalRoots.Add(rawData.Value.NasRootPath);
        formalRoots.Add(environment.WebRootPath);
        foreach (var formal in formalRoots.Where(x => !string.IsNullOrWhiteSpace(x) && Path.IsPathFullyQualified(x)))
        {
            var path = Path.GetFullPath(formal!);
            if (IsWithin(root, path) || IsWithin(path, root))
                throw new InvalidOperationException("ApprovalStagingRoot 必須與正式儲存及 wwwroot 隔離。");
        }
        return root;
    }

    private static bool IsWithin(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative));
    }
}

public sealed class ApprovalStagingStorage(IApprovalStagingRootResolver roots) : IApprovalStagingStorage
{
    public async Task<StagedFileMetadata> StoreAsync(Guid submissionId, Stream content,
        string originalFileName, string contentType, long fileSize, CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty || fileSize is <= 0 or > 200_000_000)
            throw new ArgumentException("Invalid submission ID or file size.");
        var name = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
            throw new ArgumentException("Invalid original file name.", nameof(originalFileName));
        if (!string.IsNullOrWhiteSpace(contentType) && contentType.Length > 200)
            throw new ArgumentException("Content type is too long.", nameof(contentType));
        var root = await roots.GetRootAsync(cancellationToken);
        var key = $"{submissionId:N}/{Guid.NewGuid():N}";
        var folder = Path.Combine(root, submissionId.ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > fileSize)
                    throw new InvalidOperationException("Staged file exceeds its declared size.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            if (destination.Length != fileSize)
                throw new InvalidOperationException("Staged file size does not match the upload.");
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        await using var source = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
        return new StagedFileMetadata(name, string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream" : contentType.Trim(), fileSize, hash, key);
    }

    public async Task<Stream> OpenReadAsync(Guid submissionId, string storageKey,
        CancellationToken cancellationToken = default)
    {
        var pieces = storageKey.Split('/');
        if (pieces.Length != 2 || pieces[0] != submissionId.ToString("N")
            || !Guid.TryParseExact(pieces[1], "N", out _))
            throw new UnauthorizedAccessException("Invalid staging storage key.");
        var root = await roots.GetRootAsync(cancellationToken);
        var path = Path.Combine(root, pieces[0], pieces[1]);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous);
    }

    public async Task DeleteAsync(Guid submissionId, string storageKey,
        CancellationToken cancellationToken = default)
    {
        var pieces = storageKey.Split('/');
        if (pieces.Length != 2 || pieces[0] != submissionId.ToString("N")
            || !Guid.TryParseExact(pieces[1], "N", out _))
            throw new UnauthorizedAccessException("Invalid staging storage key.");
        var root = await roots.GetRootAsync(cancellationToken);
        var path = Path.Combine(root, pieces[0], pieces[1]);
        if (File.Exists(path)) File.Delete(path);
        var folder = Path.Combine(root, pieces[0]);
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }
}

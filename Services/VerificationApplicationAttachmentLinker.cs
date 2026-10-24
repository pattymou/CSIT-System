using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

public static class VerificationApplicationAttachmentFolders
{
    public const string Root = "產品驗證資料";
    public const string ApplicationDocuments = "申請單文件";
}

internal static class VerificationApplicationAttachmentLinker
{
    public static async Task LinkFormalAsync(
        AppDbContext db,
        VerificationApplication application,
        VerificationApplicationFile attachment,
        string standardFileName,
        string relativePath,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var modelName = application.ProductModel?.Trim();
        if (string.IsNullOrWhiteSpace(modelName))
            throw new InvalidOperationException("Model Name is required before attachments can be classified.");

        var root = await GetOrCreateFolderAsync(
            db, null, 1, VerificationApplicationAttachmentFolders.Root,
            "驗證申請與驗證成果文件。", 10, now, cancellationToken);
        var model = await GetOrCreateFolderAsync(
            db, root.Id, 2, modelName,
            $"Model Name：{modelName}", 10, now, cancellationToken);
        var target = await GetOrCreateFolderAsync(
            db, model.Id, 3, VerificationApplicationAttachmentFolders.ApplicationDocuments,
            "從驗證申請單 C. 其他資訊上傳的文件。", 10, now, cancellationToken);

        DocumentLibraryFile document;
        if (attachment.DocumentLibraryFileId.HasValue)
        {
            document = attachment.DocumentLibraryFile ?? await db.DocumentLibraryFiles.SingleOrDefaultAsync(
                x => x.Id == attachment.DocumentLibraryFileId.Value, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Attachment {attachment.Id} has an invalid document library link.");
        }
        else
        {
            document = new DocumentLibraryFile
            {
                Id = Guid.NewGuid(),
            };
            db.DocumentLibraryFiles.Add(document);
            attachment.DocumentLibraryFileId = document.Id;
            attachment.DocumentLibraryFile = document;
        }

        document.FolderId = target.Id;
        document.OriginalFileName = attachment.FileName;
        document.StandardFileName = standardFileName;
        document.ContentType = attachment.ContentType;
        document.UploadedBy = attachment.UploadedBy;
        document.UploadedAt = attachment.CreatedAt;
        document.FileSize = attachment.FileSize;
        document.Sha256 = attachment.Sha256
            ?? throw new InvalidOperationException($"Attachment {attachment.Id} is missing its integrity hash.");
        document.RelativePath = relativePath;
        document.StorageArea = DocumentStorageAreas.Library;
    }

    private static async Task<DocumentFolder> GetOrCreateFolderAsync(
        AppDbContext db,
        Guid? parentId,
        int level,
        string name,
        string description,
        int sortOrder,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var normalizedName = DocumentLibraryRules.NormalizeName(name);
        var tracked = db.ChangeTracker.Entries<DocumentFolder>()
            .Select(x => x.Entity)
            .SingleOrDefault(x => x.ParentId == parentId &&
                string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (tracked is not null) return tracked;

        var lowered = normalizedName.ToLower();
        var existing = await db.DocumentFolders.SingleOrDefaultAsync(
            x => x.ParentId == parentId && x.Name.ToLower() == lowered, cancellationToken);
        if (existing is not null)
        {
            if (existing.Level != level)
                throw new InvalidOperationException($"Document folder '{normalizedName}' has an invalid level.");
            return existing;
        }

        var folder = new DocumentFolder
        {
            Id = DeterministicFolderId(parentId, normalizedName),
            ParentId = parentId,
            Level = level,
            Name = normalizedName,
            Description = description,
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.DocumentFolders.Add(folder);
        return folder;
    }

    private static Guid DeterministicFolderId(Guid? parentId, string name)
    {
        var key = $"verification-application-folder:{parentId?.ToString("N") ?? "root"}:{name.ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}

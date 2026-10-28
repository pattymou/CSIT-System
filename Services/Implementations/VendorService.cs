using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Approval;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class VendorService(
    AppDbContext db,
    ManagedAssetFileStorage storage,
    IOptions<KmIntegrationOptions>? kmOptions = null) : IVendorService, IVendorFormalUploadService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
        { "Manufacturer", "Agent", "Supplier", "MaintenanceProvider" };

    public Task<List<VendorListItemDto>> SearchAsync(string? keyword, bool includeDisabled,
        CancellationToken cancellationToken = default)
    {
        var query = db.Vendors.AsNoTracking().AsQueryable();
        if (!includeDisabled) query = query.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) ||
                (x.Aliases != null && x.Aliases.ToLower().Contains(term)));
        }
        return query.OrderBy(x => x.Name).Select(x => new VendorListItemDto
        {
            Id = x.Id, Name = x.Name, Aliases = x.Aliases, Types = x.Types,
            ContactPerson = x.ContactPerson, Phone = x.Phone, IsActive = x.IsActive,
            EquipmentCount = x.Apparatuses.Count(a => a.ModuleCode == "equipment")
        }).ToListAsync(cancellationToken);
    }

    public Task<List<VendorOptionDto>> OptionsAsync(Guid? includeVendorId = null,
        CancellationToken cancellationToken = default) => db.Vendors.AsNoTracking()
        .Where(x => x.IsActive || (includeVendorId.HasValue && x.Id == includeVendorId.Value))
        .OrderBy(x => x.Name)
        .Select(x => new VendorOptionDto { Id = x.Id, Name = x.Name, IsActive = x.IsActive })
        .ToListAsync(cancellationToken);

    public async Task<VendorDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var vendor = await db.Vendors.AsNoTracking()
            .Include(x => x.Files.Where(file => file.ArchiveStatus == FileArchiveStatuses.Archived))
            .Include(x => x.Apparatuses)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (vendor is null) return null;
        var fileIds = vendor.Files.Select(x => x.Id).ToArray();
        var outboxes = await db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                fileIds.Contains(x.SourceEntityId))
            .ToDictionaryAsync(x => x.SourceEntityId, cancellationToken);
        return ToDetail(vendor, outboxes,
            kmOptions?.Value is { Enabled: true, VendorEnabled: true });
    }

    public async Task<Guid> CreateAsync(VendorUpsertRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        var (name, normalized, types) = Validate(request);
        if (await db.Vendors.AsNoTracking().AnyAsync(x => x.NormalizedName == normalized, cancellationToken))
            throw new InvalidOperationException("已有相同名稱的廠商（名稱比對不分大小寫與多餘空白）。");
        var now = DateTime.UtcNow;
        var entity = new Vendor
        {
            Id = Guid.NewGuid(), Name = name, NormalizedName = normalized,
            StorageFolderName = ManagedAssetFileStorage.SafeFolderName(name), Types = types,
            CreatedBy = Actor(actor), UpdatedBy = Actor(actor), CreatedAt = now, UpdatedAt = now
        };
        Apply(entity, request);
        db.Vendors.Add(entity);
        await SaveDuplicateAwareAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, VendorUpsertRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.Vendors.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        var (name, normalized, types) = Validate(request);
        if (await db.Vendors.AsNoTracking().AnyAsync(x => x.Id != id && x.NormalizedName == normalized,
                cancellationToken))
            throw new InvalidOperationException("已有相同名稱的廠商（名稱比對不分大小寫與多餘空白）。");
        ApplyConcurrency(entity, request.RowVersion);
        entity.Name = name;
        entity.NormalizedName = normalized;
        entity.Types = types;
        // StorageFolderName intentionally remains stable after rename so archived links never break.
        entity.UpdatedBy = Actor(actor);
        entity.UpdatedAt = DateTime.UtcNow;
        Apply(entity, request);
        await SaveDuplicateAwareAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contextId = id.ToString("N");
        if (await db.PendingSubmissions.AsNoTracking().AnyAsync(x =>
                x.SourceModuleCode == VendorApprovalSourceResolver.ModuleCode &&
                x.ContextType == VendorApprovalSourceResolver.ContextType &&
                x.ContextId == contextId, cancellationToken))
            throw new InvalidOperationException("廠商已有附件送審歷史，不可刪除；請改為停用。");
        var entity = await db.Vendors.Include(x => x.Apparatuses).Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return false;
        if (entity.Apparatuses.Count != 0)
            throw new InvalidOperationException("廠商已被設備引用，不可刪除；請改為停用。");
        if (entity.Files.Count != 0)
            throw new InvalidOperationException("廠商已有歷史附件，不可刪除；請改為停用。");
        db.Vendors.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Guid> ArchiveApprovedFileAsync(Guid id, IFormFile file, string actor,
        Guid approvalOperationId, string approvedBy, DateTime approvedAt,
        CancellationToken cancellationToken = default)
    {
        var vendor = await db.Vendors.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("找不到廠商。");
        if (approvalOperationId == Guid.Empty || string.IsNullOrWhiteSpace(approvedBy))
            throw new InvalidOperationException("附件缺少核准識別或核准者，無法正式歸檔。");
        var originalName = ManagedAssetFileStorage.ValidateOriginalFileName(file.FileName, file.Length);
        var folder = await storage.VendorFolderAsync(vendor.StorageFolderName, cancellationToken);

        var entity = await db.VendorFiles.SingleOrDefaultAsync(
            x => x.ApprovalOperationId == approvalOperationId, cancellationToken);
        if (entity is not null)
        {
            if (entity.VendorId != id || entity.OriginalFileName != originalName ||
                entity.FileSize != file.Length)
                throw new InvalidOperationException("相同核准識別對應的廠商附件資料不一致，已停止重試。");
            if (entity.ArchiveStatus == FileArchiveStatuses.Archived)
            {
                await EnsureVendorOutboxAsync(entity, cancellationToken);
                return entity.Id;
            }
        }
        else
        {
            var now = DateTime.UtcNow;
            entity = new VendorFile
            {
                Id = approvalOperationId,
                VendorId = vendor.Id,
                OriginalFileName = originalName,
                FolderPath = folder,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream" : file.ContentType.Trim(),
                FileSize = file.Length,
                ArchiveStatus = FileArchiveStatuses.Pending,
                ArchiveAttemptCount = 0,
                ArchiveUpdatedAt = now,
                UploadedBy = Actor(actor),
                UploadedAt = now,
                ApprovalOperationId = approvalOperationId,
                ApprovedBy = Actor(approvedBy),
                ApprovedAt = approvedAt,
                MetadataVerificationStatus = MetadataVerificationStatuses.NotApplicable
            };
            entity.StoredFileName = ManagedAssetFileStorage.StoredFileName(entity.Id, originalName);
            db.VendorFiles.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }

        entity.ArchiveAttemptCount++;
        entity.ArchiveStatus = FileArchiveStatuses.Pending;
        entity.ArchiveError = null;
        entity.ArchiveNextRetryAt = null;
        entity.ArchiveUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await using var source = file.OpenReadStream();
            entity.Sha256 = await storage.ArchiveAsync(source, folder, entity.StoredFileName, cancellationToken);
            entity.ArchiveStatus = FileArchiveStatuses.Archived;
            entity.ArchiveError = null;
            entity.ArchiveNextRetryAt = null;
            entity.ArchiveUpdatedAt = DateTime.UtcNow;
            db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVendorFile(entity, DateTime.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return entity.Id;
        }
        catch (Exception ex)
        {
            entity.ArchiveStatus = FileArchiveStatuses.Failed;
            entity.ArchiveError = ex.Message[..Math.Min(ex.Message.Length, 2000)];
            entity.ArchiveNextRetryAt = DateTime.UtcNow.AddMinutes(5);
            entity.ArchiveUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var file = await db.VendorFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken)
            ?? throw new FileNotFoundException("找不到廠商附件。");
        if (file.ArchiveStatus != FileArchiveStatuses.Archived)
            throw new FileNotFoundException("附件尚未完成正式歸檔。");
        var path = ManagedAssetFileStorage.FullPath(file.FolderPath, file.StoredFileName);
        if (!File.Exists(path)) throw new FileNotFoundException("正式歸檔檔案不存在。");
        return (await File.ReadAllBytesAsync(path, cancellationToken), file.OriginalFileName,
            file.ContentType ?? "application/octet-stream");
    }

    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await db.VendorFiles.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken);
        if (file is null) return false;
        if (file.ApprovalOperationId.HasValue || await db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityId == file.Id, cancellationToken))
            throw new InvalidOperationException(
                "此附件已進入主管審核及 KM 追蹤流程；正式 Vendor 刪除契約尚未核准，目前禁止直接刪除。");
        db.VendorFiles.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        var path = ManagedAssetFileStorage.FullPath(file.FolderPath, file.StoredFileName);
        if (File.Exists(path)) File.Delete(path);
        var pendingPath = path + ".pending";
        if (File.Exists(pendingPath)) File.Delete(pendingPath);
        return true;
    }

    public async Task<List<ManufacturerMatchReportItemDto>> ManufacturerMatchReportAsync(
        CancellationToken cancellationToken = default)
    {
        var legacy = await db.Apparatuses.AsNoTracking()
            .Where(x => x.ModuleCode == "equipment" && x.VendorId == null && x.Manufacturer != null && x.Manufacturer != "")
            .GroupBy(x => x.Manufacturer!).Select(x => new { Manufacturer = x.Key, Count = x.Count() })
            .OrderByDescending(x => x.Count).ToListAsync(cancellationToken);
        var vendors = await db.Vendors.AsNoTracking().Select(x => new VendorOptionDto
            { Id = x.Id, Name = x.Name, IsActive = x.IsActive }).ToListAsync(cancellationToken);
        return legacy.Select(x => new ManufacturerMatchReportItemDto
        {
            Manufacturer = x.Manufacturer, EquipmentCount = x.Count,
            CandidateVendors = vendors.Where(v => Normalize(v.Name) == Normalize(x.Manufacturer)).ToList()
        }).ToList();
    }

    private async Task EnsureVendorOutboxAsync(VendorFile file, CancellationToken cancellationToken)
    {
        if (await db.KmIntegrationOutbox.AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.VendorFileArchived &&
                x.SourceEntityId == file.Id, cancellationToken)) return;
        db.KmIntegrationOutbox.Add(KmIntegrationOutbox.ForArchivedVendorFile(file, DateTime.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static VendorDetailDto ToDetail(Vendor x,
        IReadOnlyDictionary<Guid, KmIntegrationOutbox> outboxes, bool vendorKmEnabled) => new()
    {
        Id = x.Id, Name = x.Name, Aliases = x.Aliases, Types = x.Types, Website = x.Website,
        ContactPerson = x.ContactPerson, Phone = x.Phone, Email = x.Email, Address = x.Address,
        Notes = x.Notes, IsActive = x.IsActive, EquipmentCount = x.Apparatuses.Count,
        RowVersion = x.Xmin.ToString(), CreatedBy = x.CreatedBy, CreatedAt = x.CreatedAt,
        UpdatedBy = x.UpdatedBy, UpdatedAt = x.UpdatedAt,
        Files = x.Files.OrderByDescending(f => f.UploadedAt).Select(f =>
        {
            outboxes.TryGetValue(f.Id, out var outbox);
            return new VendorFileDto
            {
                Id = f.Id, VendorId = f.VendorId, FileName = f.OriginalFileName,
                StoredFileName = f.StoredFileName, ContentType = f.ContentType, FileSize = f.FileSize,
                Sha256 = f.Sha256, ArchiveStatus = f.ArchiveStatus, ArchiveError = f.ArchiveError,
                DownloadUrl = $"/api/vendors/files/{f.Id}/content",
                IsImage = ManagedAssetFileStorage.IsImage(f.ContentType, f.OriginalFileName),
                UploadedBy = f.UploadedBy, UploadedAt = f.UploadedAt,
                ApprovedBy = f.ApprovedBy, ApprovedAt = f.ApprovedAt,
                HasKmSchedule = outbox is not null,
                KmEnabled = vendorKmEnabled,
                KmStatus = outbox?.Status.ToString(),
                KmError = outbox?.LastErrorMessage,
                CanDelete = f.ApprovalOperationId is null && outbox is null
            };
        }).ToList()
    };

    private static void Apply(Vendor entity, VendorUpsertRequest request)
    {
        entity.Aliases = Optional(request.Aliases);
        entity.Website = Optional(request.Website);
        entity.ContactPerson = Optional(request.ContactPerson);
        entity.Phone = Optional(request.Phone);
        entity.Email = Optional(request.Email);
        entity.Address = Optional(request.Address);
        entity.Notes = Optional(request.Notes);
        entity.IsActive = request.IsActive;
    }

    private static (string Name, string Normalized, string[] Types) Validate(VendorUpsertRequest request)
    {
        var name = Collapse(request.Name);
        if (name.Length == 0) throw new InvalidOperationException("廠商名稱為必填。");
        if (name.Length > 200) throw new InvalidOperationException("廠商名稱不可超過 200 字元。");
        var types = (request.Types ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (types.Any(x => !AllowedTypes.Contains(x)))
            throw new InvalidOperationException("請選擇有效的廠商類型。");
        return (name, Normalize(name), types);
    }

    public static string Normalize(string value) => Collapse(value).ToUpperInvariant();
    private static string Collapse(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Actor(string? value) => string.IsNullOrWhiteSpace(value) ? "SYSTEM" : value.Trim();

    private void ApplyConcurrency(Vendor entity, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) return;
        if (!uint.TryParse(rowVersion, out var expected)) throw new InvalidOperationException("資料版本格式錯誤。");
        db.Entry(entity).Property(x => x.Xmin).OriginalValue = expected;
    }

    private async Task SaveDuplicateAwareAsync(CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("normalized_name", StringComparison.OrdinalIgnoreCase) == true)
        { throw new InvalidOperationException("已有相同名稱的廠商（名稱比對不分大小寫與多餘空白）。", ex); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("storage_folder_name", StringComparison.OrdinalIgnoreCase) == true)
        { throw new InvalidOperationException("廠商名稱轉換成安全歸檔路徑後與既有廠商重複，請調整名稱。", ex); }
        catch (DbUpdateConcurrencyException ex)
        { throw new InvalidOperationException("廠商資料已被其他使用者更新，請重新整理後再試。", ex); }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ApparatusService : IApparatusService
{
    private readonly AppDbContext _db;
    private readonly ApparatusSettings _settings;
    private readonly RawDataSettings _rawDataSettings;
    private readonly IRawDataExportService _rawDataExportService;
    private readonly SharedIdentityProvisioningClient _provisioningClient;
    private readonly ManagedAssetFileStorage _managedStorage;
    private readonly KmIntegrationOptions _kmOptions;

    public ApparatusService(
        AppDbContext db,
        IOptions<ApparatusSettings> options,
        IOptions<RawDataSettings> rawDataOptions,
        IRawDataExportService rawDataExportService,
        SharedIdentityProvisioningClient provisioningClient,
        ManagedAssetFileStorage? managedStorage = null,
        IOptions<KmIntegrationOptions>? kmOptions = null)
    {
        _db = db;
        _settings = options.Value;
        _rawDataSettings = rawDataOptions.Value;
        _rawDataExportService = rawDataExportService;
        _provisioningClient = provisioningClient;
        _managedStorage = managedStorage ?? new ManagedAssetFileStorage(db);
        _kmOptions = kmOptions?.Value ?? new KmIntegrationOptions();
    }

    public async Task<string> GenerateNewIdAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            var id = "A" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + RandomNumberGenerator.GetInt32(1000, 9999);
            var exists = await _db.Apparatuses.AnyAsync(x => x.Id == id);

            if (!exists)
            {
                Console.WriteLine($"[ApparatusService] Generate new id: {id}");
                return id;
            }
        }

        var fallbackId = "A" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        Console.WriteLine($"[ApparatusService] Generate fallback id: {fallbackId}");
        return fallbackId;
    }

    public async Task<ApparatusOwnershipOptionsDto> GetOwnershipOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var teams = await _db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .Select(x => new ApparatusOwnerTeamOptionDto
            {
                Id = x.Id,
                Value = x.Value,
                Name = x.Name
            })
            .ToListAsync(cancellationToken);

        IReadOnlyList<SharedIdentityAccountListItem> accounts;
        try
        {
            accounts = await _provisioningClient.ListAccountsAsync(cancellationToken);
        }
        catch (SharedIdentityProvisioningException exception)
        {
            throw new InvalidOperationException("目前無法取得可用的保管人名單，請稍後再試。", exception);
        }

        var users = accounts
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Account) &&
                string.Equals(x.AccountStatus?.Trim(), "Active", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.SystemAccessStatus?.Trim(), "Active", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.Account : x.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ApparatusCustodianOptionDto
            {
                Account = x.Account.Trim(),
                DisplayName = x.DisplayName?.Trim() ?? string.Empty,
                Department = x.Department?.Trim() ?? string.Empty,
                TeamCode = x.TeamCode?.Trim() ?? string.Empty
            })
            .ToList();

        var departments = await _db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Department && x.IsEnabled)
            .OrderBy(x => x.Sort).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        return new ApparatusOwnershipOptionsDto
        {
            Departments = departments
                .Select(x => new ApparatusOwnerTeamOptionDto { Id = x.Id, Name = x.Name, Value = x.Value }).ToList(),
            Teams = teams, Users = users
        };
    }

    public async Task<List<ApparatusListItemDto>> GetListAsync(string moduleCode, string? keyword, string? kind)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var query = _db.Apparatuses
            .Where(x => x.ModuleCode == moduleCode)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(x => x.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                (x.ProductsId ?? "").Contains(keyword) ||
                x.Name.Contains(keyword) ||
                (x.Brand ?? "").Contains(keyword) ||
                (x.Manufacturer ?? "").Contains(keyword) ||
                (x.Vendor != null && x.Vendor.Name.Contains(keyword)) ||
                (x.Model ?? "").Contains(keyword));
        }

        Console.WriteLine($"[ApparatusService] GetList. moduleCode={moduleCode}, keyword={keyword}, kind={kind}");

        return await query
            .OrderBy(x => x.Name)
            .Select(x => new ApparatusListItemDto
            {
                Id = x.Id,
                ModuleCode = x.ModuleCode,
                ProductsId = x.ProductsId,
                Name = x.Name,
                Kind = x.Kind,
                Brand = x.Brand,
                VendorId = x.VendorId,
                VendorName = x.Vendor == null ? null : x.Vendor.Name,
                Model = x.Model,
                Number = x.Number,
                ReservationStatus = x.ReservationStatus,
                Place = x.Place,
                Custodian = _db.Users
                    .Where(user => x.CustodianAccount != null
                        && user.Account.ToLower() == x.CustodianAccount.ToLower())
                    .Select(user => user.DisplayName)
                    .FirstOrDefault(),
                CustodianAccount = x.CustodianAccount,
                DepartmentOptionId = x.DepartmentOptionId,
                DepartmentName = x.DepartmentOption == null
                    ? null
                    : DepartmentTeamRules.DisplayName(x.DepartmentOption.Name, x.DepartmentOption.Value),
                OwnerTeamOptionId = x.OwnerTeamOptionId,
                OwnerTeamName = x.OwnerTeamOption == null ? null : (string.IsNullOrWhiteSpace(x.OwnerTeamOption.Name) ? x.OwnerTeamOption.Value : x.OwnerTeamOption.Name),
                Agent = x.Agent,
                Note = x.Note,
                EnvironmentGroupDeviceId = x.EnvironmentGroupDevices
                    .OrderByDescending(d => d.IsInEnvironment)
                    .ThenByDescending(d => d.PresenceUpdatedAt ?? d.AddedAt)
                    .Select(d => (Guid?)d.Id)
                    .FirstOrDefault(),
                EnvironmentGroupId = x.EnvironmentGroupDevices
                    .OrderByDescending(d => d.IsInEnvironment)
                    .ThenByDescending(d => d.PresenceUpdatedAt ?? d.AddedAt)
                    .Select(d => (Guid?)d.EquipmentGroupId)
                    .FirstOrDefault(),
                EnvironmentGroupName = x.EnvironmentGroupDevices
                    .OrderByDescending(d => d.IsInEnvironment)
                    .ThenByDescending(d => d.PresenceUpdatedAt ?? d.AddedAt)
                    .Select(d => d.EquipmentGroup.Name)
                    .FirstOrDefault(),
                IsInEnvironment = x.EnvironmentGroupDevices
                    .OrderByDescending(d => d.IsInEnvironment)
                    .ThenByDescending(d => d.PresenceUpdatedAt ?? d.AddedAt)
                    .Select(d => (bool?)d.IsInEnvironment)
                    .FirstOrDefault()
            })
            .ToListAsync();
    }

    public async Task<ApparatusDetailDto?> GetByIdAsync(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var entity = await _db.Apparatuses
            .Include(x => x.Files)
            .Include(x => x.DepartmentOption)
            .Include(x => x.OwnerTeamOption)
            .Include(x => x.Vendor)
            .Include(x => x.EnvironmentGroupDevices).ThenInclude(x => x.EquipmentGroup)
            .FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == id);

        Console.WriteLine($"[ApparatusService] GetById. moduleCode={moduleCode}, id={id}, found={entity != null}");

        if (entity is null) return null;

        var custodianNames = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            _db,
            [entity.CustodianAccount]);
        var outboxes = await LoadEquipmentOutboxesAsync(entity.Files);
        var verifiedFiles = await VerifyFormalFilesAsync(entity.Files);
        var lifecycleRecords = moduleCode == "equipment"
            ? await _db.ReservationItems.AsNoTracking()
                .Where(x => x.ApparatusId == entity.Id)
                .OrderByDescending(x => x.Reservation.StartTime)
                .Select(x => new EquipmentLifecycleRecordDto
                {
                    ReservationId = x.ReservationId,
                    ReservationNo = x.Reservation.ReservationNo,
                    Status = x.Reservation.Status,
                    StartTime = x.Reservation.StartTime,
                    EndTime = x.Reservation.EndTime,
                    BorrowedAt = x.Reservation.BorrowedAt,
                    ReturnedAt = x.Reservation.ReturnedAt
                })
                .ToListAsync()
            : [];
        return ToDetailDto(
            entity,
            ApparatusCustodianResolver.GetDisplayName(custodianNames, entity.CustodianAccount),
            outboxes, verifiedFiles, lifecycleRecords);
    }

    public async Task<string> CreateAsync(string moduleCode, ApparatusUpsertRequest request)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        ValidateUpsert(request);
        var selectedVendor = await ValidateNewEquipmentVendorAsync(moduleCode, request);
        var ownership = await ValidateOwnershipAsync(
            request,
            CancellationToken.None);

        var id = string.IsNullOrWhiteSpace(request.Id)
            ? await GenerateNewIdAsync()
            : request.Id.Trim();

        var exists = await _db.Apparatuses.AnyAsync(x => x.Id == id);
        if (exists)
        {
            throw new InvalidOperationException($"資產 ID 已存在：{id}。請重新整理頁面後再新增。");
        }

        var entity = new Apparatus
        {
            Id = id,
            ModuleCode = moduleCode,
            ProductsId = request.ProductsId,
            Name = request.Name.Trim(),
            NameEn = request.NameEn,
            Kind = request.Kind!.Trim(),
            PartNo = request.PartNo,
            Manufacturer = selectedVendor?.Name ?? request.Manufacturer,
            ManufacturerNumber = request.ManufacturerNumber,
            VendorId = moduleCode == "equipment" ? request.VendorId : null,
            StorageFolderName = moduleCode == "equipment" && !string.IsNullOrWhiteSpace(request.ProductsId)
                ? ManagedAssetFileStorage.SafeFolderName($"{request.Name.Trim()}_{request.ProductsId.Trim()}")
                : null,
            Brand = request.Brand,
            Model = request.Model,
            Number = request.Number,
            ProcurementStaff = request.ProcurementStaff,
            Imei = request.Imei,
            Os = request.Os,
            OsVersion = request.OsVersion,
            InspectionDate = request.InspectionDate,
            MaintenanceDate = request.MaintenanceDate,
            Place = request.Place,
            CostPrice = request.CostPrice,
            YearsUse = request.YearsUse,
            DaysUse = request.DaysUse,
            PriceUse = request.PriceUse,
            CustodianAccount = ownership.CustodianAccount,
            DepartmentOptionId = request.DepartmentOptionId,
            OwnerTeamOptionId = ownership.OwnerTeamOptionId,
            Agent = request.Agent,
            ReservationStatus = string.IsNullOrWhiteSpace(request.ReservationStatus) ? "可借用" : request.ReservationStatus,
            Feature = request.Feature,
            Spec = request.Spec,
            Note = request.Note,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Apparatuses.Add(entity);
        await _db.SaveChangesAsync();

        var folder = moduleCode == "equipment"
            ? "(managed equipment folder is created on approved archive)"
            : await GetApparatusFolderAsync(moduleCode, id);
        if (moduleCode != "equipment") Directory.CreateDirectory(folder);

        await RebuildLatestRawDataAsync(moduleCode, id);

        Console.WriteLine($"[ApparatusService] Created asset. moduleCode={moduleCode}, id={id}, folder={folder}");
        return id;
    }

    public async Task<bool> UpdateAsync(string moduleCode, string id, ApparatusUpsertRequest request)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var entity = await _db.Apparatuses.FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == id);
        if (entity == null)
        {
            return false;
        }

        ValidateUpsert(request);
        var selectedVendor = await ValidateEquipmentVendorUpdateAsync(moduleCode, entity, request);
        var ownership = await ValidateOwnershipAsync(
            request,
            CancellationToken.None);
        ApplyConcurrencyToken(entity, request.RowVersion);

        entity.ModuleCode = moduleCode;
        entity.ProductsId = request.ProductsId;
        entity.Name = request.Name.Trim();
        entity.NameEn = request.NameEn;
        entity.Kind = request.Kind!.Trim();
        entity.PartNo = request.PartNo;
        entity.Manufacturer = selectedVendor?.Name ?? request.Manufacturer;
        entity.ManufacturerNumber = request.ManufacturerNumber;
        if (moduleCode == "equipment") entity.VendorId = request.VendorId;
        entity.Brand = request.Brand;
        entity.Model = request.Model;
        entity.Number = request.Number;
        entity.ProcurementStaff = request.ProcurementStaff;
        entity.Imei = request.Imei;
        entity.Os = request.Os;
        entity.OsVersion = request.OsVersion;
        entity.InspectionDate = request.InspectionDate;
        entity.MaintenanceDate = request.MaintenanceDate;
        entity.Place = request.Place;
        entity.CostPrice = request.CostPrice;
        entity.YearsUse = request.YearsUse;
        entity.DaysUse = request.DaysUse;
        entity.PriceUse = request.PriceUse;
        entity.CustodianAccount = ownership.CustodianAccount;
        entity.DepartmentOptionId = request.DepartmentOptionId;
        entity.OwnerTeamOptionId = ownership.OwnerTeamOptionId;
        entity.Agent = request.Agent;
        entity.ReservationStatus = request.ReservationStatus;
        entity.Feature = request.Feature;
        entity.Spec = request.Spec;
        entity.Note = request.Note;
        entity.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Console.WriteLine($"[ApparatusService] Concurrency conflict. moduleCode={moduleCode}, id={id}, error={ex}");
            throw new InvalidOperationException("資料已被其他人修改，請重新整理後再編輯。", ex);
        }

        await RebuildLatestRawDataAsync(moduleCode, id);

        Console.WriteLine($"[ApparatusService] Updated asset. moduleCode={moduleCode}, id={id}, rowVersion={entity.Xmin}");
        return true;
    }

    public async Task<bool> DeleteAsync(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM apparatus WHERE \"Id\" = {id} FOR UPDATE");

        var entity = await _db.Apparatuses
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == id);

        if (entity == null)
        {
            return false;
        }

        if (await _db.ReservationItems
                .AsNoTracking()
                .AnyAsync(x => x.ApparatusId == id))
        {
            throw new InvalidOperationException(
                "This apparatus has reservation history and cannot be deleted. Retain the apparatus record instead.");
        }

        if (await _db.EquipmentGroupDevices.AsNoTracking().AnyAsync(x => x.ApparatusId == id))
        {
            throw new InvalidOperationException(
                "This apparatus belongs to a test environment group and cannot be deleted. Remove the group membership first.");
        }

        var fileIds = entity.Files.Select(x => x.Id).ToArray();
        if (fileIds.Length > 0 && await _db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                fileIds.Contains(x.SourceEntityId)))
            throw new InvalidOperationException(
                "This equipment has documents tracked by KM and cannot be physically deleted.");

        foreach (var file in entity.Files)
        {
            DeletePhysicalFile(file);
        }

        _db.ApparatusFiles.RemoveRange(entity.Files);
        _db.Apparatuses.Remove(entity);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        var folder = await GetApparatusFolderAsync(moduleCode, id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
            Console.WriteLine($"[ApparatusService] Deleted asset folder: {folder}");
        }

        await DeleteLatestRawDataByLocalFolderAsync(folder);

        Console.WriteLine($"[ApparatusService] Deleted asset. moduleCode={moduleCode}, id={id}");
        return true;
    }

    public async Task<List<ApparatusFileDto>> GetFilesAsync(string moduleCode, string apparatusId)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var apparatus = await _db.Apparatuses.SingleOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);
        if (apparatus is null)
        {
            return new();
        }

        var files = await _db.ApparatusFiles
            .Where(x => x.ApparatusId == apparatusId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
        var outboxes = await LoadEquipmentOutboxesAsync(files);
        var verifiedFiles = await VerifyFormalFilesAsync(files);
        return files.Select(x => ToFileDto(x, outboxes, verifiedFiles.Contains(x.Id))).ToList();
    }

    public async Task UploadFilesAsync(string moduleCode, string apparatusId, IReadOnlyList<IFormFile> files,
        string? uploadEmp, Guid? approvalFileId = null)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        if (files.Count == 0)
        {
            throw new InvalidOperationException("沒有收到檔案");
        }
        if (approvalFileId.HasValue && (approvalFileId.Value == Guid.Empty || files.Count != 1))
            throw new InvalidOperationException("Approved Asset submission must contain exactly one file.");

        var apparatus = await _db.Apparatuses.SingleOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);
        if (apparatus is null)
        {
            throw new InvalidOperationException($"找不到資產：moduleCode={moduleCode}, id={apparatusId}");
        }

        if (moduleCode == "equipment")
        {
            await UploadManagedEquipmentFilesAsync(apparatus, files, uploadEmp, approvalFileId);
            return;
        }

        var folder = await GetApparatusFolderAsync(moduleCode, apparatusId);
        Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            if (file.Length <= 0)
            {
                throw new InvalidOperationException($"檔案是空的：{file.FileName}");
            }

            var fileId = approvalFileId ?? Guid.NewGuid();
            var safeFileName = CleanFileName(file.FileName);
            var fileFolder = Path.Combine(folder, fileId.ToString("N"));
            Directory.CreateDirectory(fileFolder);

            var fullPath = Path.Combine(fileFolder, safeFileName);
            var existing = approvalFileId.HasValue
                ? await _db.ApparatusFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId)
                : null;
            if (existing is not null && (existing.ApparatusId != apparatusId ||
                existing.FileName != safeFileName || existing.FilePath != fileFolder ||
                existing.FileSize != file.Length))
                throw new InvalidOperationException("Approved Asset file identity changed during retry.");

            if (File.Exists(fullPath))
            {
                if (approvalFileId.HasValue)
                {
                    if (!await HasSameContentAsync(file, fullPath))
                    {
                        if (existing is not null)
                            throw new InvalidOperationException("Approved Asset file checksum changed during retry.");
                        // A crash can leave a partial file before the DB row exists. This
                        // directory belongs to the submission ID, so staging can repair it.
                        await using var replacement = File.Create(fullPath);
                        await file.CopyToAsync(replacement);
                    }
                }
                else
                {
                    await using var stream = File.Create(fullPath);
                    await file.CopyToAsync(stream);
                }
            }
            else
            {
                await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
                await file.CopyToAsync(stream);
            }

            if (existing is not null) continue;

            var now = DateTime.UtcNow;

            var entity = new ApparatusFile
            {
                Id = fileId,
                ApparatusId = apparatusId,
                FileName = safeFileName,
                FilePath = fileFolder,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSize = file.Length,
                UploadEmp = string.IsNullOrWhiteSpace(uploadEmp) ? "SYSTEM" : uploadEmp,
                CreatedAt = now
            };

            _db.ApparatusFiles.Add(entity);

            Console.WriteLine($"[ApparatusService] Uploaded asset file local. moduleCode={moduleCode}, apparatusId={apparatusId}, fileId={entity.Id}, path={fullPath}");
        }

        await _db.SaveChangesAsync();

        await RebuildLatestRawDataAsync(moduleCode, apparatusId);

        Console.WriteLine($"[ApparatusService] UploadFiles done. moduleCode={moduleCode}, apparatusId={apparatusId}, count={files.Count}");
    }

    private static async Task<bool> HasSameContentAsync(IFormFile candidate, string storedPath)
    {
        if (new FileInfo(storedPath).Length != candidate.Length) return false;
        await using var incoming = candidate.OpenReadStream();
        await using var stored = File.OpenRead(storedPath);
        var expected = await SHA256.HashDataAsync(incoming);
        var actual = await SHA256.HashDataAsync(stored);
        return expected.AsSpan().SequenceEqual(actual);
    }

    public async Task<(byte[] Content, string FileName, string ContentType)> GetFileContentAsync(Guid fileId)
    {
        var file = await _db.ApparatusFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
        {
            throw new FileNotFoundException("找不到檔案資料");
        }

        if (file.ArchiveStatus is not null && file.ArchiveStatus != FileArchiveStatuses.Archived)
            throw new FileNotFoundException("檔案尚未完成正式歸檔");

        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到實體檔案：{fullPath}");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath);
        var originalName = file.OriginalFileName ?? file.FileName;
        var contentType = file.ContentType;
        if (ManagedAssetFileStorage.IsImage(contentType, originalName) &&
            (string.IsNullOrWhiteSpace(contentType) ||
             contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            contentType = ImageContentType(originalName);
        return (bytes, originalName, contentType ?? "application/octet-stream");
    }

    public async Task<bool> DeleteFileAsync(Guid fileId)
    {
        var file = await _db.ApparatusFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
        {
            return false;
        }

        if (await _db.KmIntegrationOutbox.AsNoTracking().AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityId == fileId))
            throw new InvalidOperationException(
                "此設備文件已進入 KM 追蹤流程，尚未有正式刪除契約，禁止直接刪除。");

        var apparatusId = file.ApparatusId;

        var apparatus = await _db.Apparatuses
            .FirstOrDefaultAsync(x => x.Id == apparatusId);

        var moduleCode = NormalizeModuleCode(apparatus?.ModuleCode);

        DeletePhysicalFile(file);

        _db.ApparatusFiles.Remove(file);
        await _db.SaveChangesAsync();

        await RebuildLatestRawDataAsync(moduleCode, apparatusId);

        Console.WriteLine($"[ApparatusService] Deleted asset file record. fileId={fileId}, apparatusId={apparatusId}");
        return true;
    }

    public async Task<bool> ReviewFileMetadataAsync(
        Guid fileId, EquipmentMetadataReviewRequest request, string actor)
    {
        var file = await _db.ApparatusFiles.Include(x => x.Apparatus)
            .SingleOrDefaultAsync(x => x.Id == fileId);
        if (file is null) return false;
        if (file.Apparatus.ModuleCode != "equipment" || string.IsNullOrWhiteSpace(file.MetadataJson))
            throw new InvalidOperationException("此檔案目前沒有可確認的設備 Metadata 建議。");
        if (request.Status is not (MetadataVerificationStatuses.Verified or MetadataVerificationStatuses.Rejected))
            throw new InvalidOperationException("Metadata 僅能標記為已確認或已退回。");

        if (!string.IsNullOrWhiteSpace(request.CorrectedMetadataJson))
        {
            using var parsed = JsonDocument.Parse(request.CorrectedMetadataJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("修正後 Metadata 必須是 JSON object。");
            file.MetadataJson = JsonSerializer.Serialize(parsed.RootElement);
            file.MetadataSource = $"UserConfirmed:{(string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim())}";
        }
        file.MetadataVerificationStatus = request.Status;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task RebuildLatestRawDataAsync(string moduleCode, string apparatusId)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var entity = await _db.Apparatuses
            .Include(x => x.Files)
            .Include(x => x.Vendor)
            .FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);

        if (entity == null)
        {
            Console.WriteLine($"[ApparatusService] Rebuild RAW skipped. asset not found. moduleCode={moduleCode}, id={apparatusId}");
            return;
        }

        var folder = await GetApparatusFolderAsync(moduleCode, apparatusId);

        // New equipment documents already live in the managed three-level formal archive.
        // Keep RAW latest-package behavior only for legacy files so the formal NAS tree is
        // not mirrored a second time or allowed to escape the legacy local-root boundary.
        var files = entity.Files
            .Where(x => moduleCode != "equipment" || x.ArchiveStatus is null)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new RawDataLatestPackageFile
            {
                FileId = x.Id.ToString(),
                FileName = x.FileName,
                LocalFilePath = Path.Combine(x.FilePath, x.FileName),
                ContentType = x.ContentType,
                FileSize = x.FileSize,
                UploadEmp = x.UploadEmp,
                UploadedAt = x.CreatedAt
            })
            .ToList();

        var metadata = new
        {
            id = entity.Id,
            moduleCode = entity.ModuleCode,
            productsId = entity.ProductsId,
            name = entity.Name,
            nameEn = entity.NameEn,
            kind = entity.Kind,
            partNo = entity.PartNo,
            manufacturer = entity.Manufacturer,
            vendorId = entity.VendorId,
            vendorName = entity.Vendor?.Name,
            manufacturerNumber = entity.ManufacturerNumber,
            brand = entity.Brand,
            model = entity.Model,
            number = entity.Number,
            procurementStaff = entity.ProcurementStaff,
            imei = entity.Imei,
            os = entity.Os,
            osVersion = entity.OsVersion,
            inspectionDate = entity.InspectionDate,
            maintenanceDate = entity.MaintenanceDate,
            place = entity.Place,
            costPrice = entity.CostPrice,
            yearsUse = entity.YearsUse,
            daysUse = entity.DaysUse,
            priceUse = entity.PriceUse,
            custodianAccount = entity.CustodianAccount,
            agent = entity.Agent,
            reservationStatus = entity.ReservationStatus,
            feature = entity.Feature,
            spec = entity.Spec,
            note = entity.Note,
            createdAt = entity.CreatedAt,
            updatedAt = entity.UpdatedAt,
            localFolder = folder
        };

        var result = await _rawDataExportService.ExportLatestPackageAsync(new RawDataLatestPackageRequest
        {
            SourceSystem = "CSIT",
            SourceType = "Apparatus",
            ModuleCode = moduleCode,
            EntityId = apparatusId,
            LocalRootFolder = folder,
            Metadata = metadata,
            Files = files
        });

        foreach (var file in entity.Files)
        {
            if (moduleCode == "equipment" && file.ArchiveStatus is not null)
                continue;
            if (result.Success)
            {
                file.NasFolderPath = result.NasFolderPath;
                file.NasFilePath = null;
                file.RawJsonPath = result.RawJsonPath;
                file.IsRawDataExported = true;
                file.RawDataExportedAt = DateTime.UtcNow;
                file.RawDataExportError = null;
            }
            else
            {
                file.IsRawDataExported = false;
                file.RawDataExportError = result.ErrorMessage;
            }
        }

        await _db.SaveChangesAsync();

        if (result.Success)
        {
            Console.WriteLine($"[ApparatusService] Rebuild RAW success. moduleCode={moduleCode}, id={apparatusId}, folder={result.NasFolderPath}");
        }
        else
        {
            Console.WriteLine($"[ApparatusService] Rebuild RAW failed. moduleCode={moduleCode}, id={apparatusId}, error={result.ErrorMessage}");
        }
    }

    private async Task DeleteLatestRawDataByLocalFolderAsync(string localFolder)
    {
        var nasFolder = BuildNasFolderPathByLocalFolder(localFolder);

        try
        {
            if (Directory.Exists(nasFolder))
            {
                Directory.Delete(nasFolder, recursive: true);
                Console.WriteLine($"[ApparatusService] Deleted latest RAW folder: {nasFolder}");
            }
            else
            {
                Console.WriteLine($"[ApparatusService] Latest RAW folder not found, skipped: {nasFolder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusService] Delete latest RAW folder failed. folder={nasFolder}, error={ex}");
        }
    }

    private string BuildNasFolderPathByLocalFolder(string localFolderPath)
    {
        var fullLocalFolder = Path.GetFullPath(localFolderPath);
        var root = Path.GetPathRoot(fullLocalFolder);

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"無法解析本機資料夾根目錄：{localFolderPath}");
        }

        var relativePath = fullLocalFolder[root.Length..].TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return Path.Combine(GetRawDataRootPath(), relativePath);
    }

    private string GetRawDataRootPath()
    {
        if (string.IsNullOrWhiteSpace(_rawDataSettings.NasRootPath))
        {
            throw new InvalidOperationException("RawData:NasRootPath 未設定");
        }

        return _rawDataSettings.NasRootPath.Trim();
    }

    private async Task<string> GetApparatusFolderAsync(string moduleCode, string apparatusId)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var rootPath = await _db.SystemOptions
            .Where(x =>
                x.Category == "AssetRootPath" &&
                x.IsEnabled &&
                x.Name.ToLower() == moduleCode.ToLower())
            .Select(x => x.Value)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException($"找不到資產根目錄設定：Category=AssetRootPath, Name={moduleCode}");
        }

        var folder = Path.Combine(rootPath, CleanFolderName(apparatusId));

        Console.WriteLine($"[ApparatusService] Resolve asset folder. moduleCode={moduleCode}, rootPath={rootPath}, folder={folder}");

        return folder;
    }

    private void ApplyConcurrencyToken(Apparatus entity, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            Console.WriteLine($"[ApparatusService] RowVersion empty, skip concurrency check. id={entity.Id}");
            return;
        }

        if (!uint.TryParse(rowVersion, out var expectedXmin))
        {
            throw new InvalidOperationException("資料版本格式錯誤，請重新整理後再編輯。");
        }

        _db.Entry(entity).Property(x => x.Xmin).OriginalValue = expectedXmin;
        Console.WriteLine($"[ApparatusService] Apply concurrency token. id={entity.Id}, expectedXmin={expectedXmin}, currentXmin={entity.Xmin}");
    }

    private static void DeletePhysicalFile(ApparatusFile file)
    {
        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            Console.WriteLine($"[ApparatusService] Deleted local file: {fullPath}");
        }
        else
        {
            Console.WriteLine($"[ApparatusService] Local file not found, skipped: {fullPath}");
        }

        var pendingPath = fullPath + ".pending";
        if (File.Exists(pendingPath)) File.Delete(pendingPath);

        TryDeleteEmptyDirectory(file.FilePath);
    }

    private static void TryDeleteEmptyDirectory(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                Console.WriteLine($"[ApparatusService] Deleted empty file folder: {folder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApparatusService] Delete empty file folder skipped. folder={folder}, error={ex.Message}");
        }
    }

    private ApparatusDetailDto ToDetailDto(Apparatus x, string? custodianDisplayName,
        IReadOnlyDictionary<Guid, KmIntegrationOutbox> outboxes,
        IReadOnlySet<Guid> verifiedFiles,
        List<EquipmentLifecycleRecordDto> lifecycleRecords)
    {
        var environmentAssignment = x.EnvironmentGroupDevices
            .OrderByDescending(d => d.IsInEnvironment)
            .ThenByDescending(d => d.PresenceUpdatedAt ?? d.AddedAt)
            .FirstOrDefault();
        return new ApparatusDetailDto
        {
            Id = x.Id,
            ModuleCode = x.ModuleCode,
            RowVersion = x.Xmin.ToString(),
            ProductsId = x.ProductsId,
            Name = x.Name,
            NameEn = x.NameEn,
            Kind = x.Kind,
            PartNo = x.PartNo,
            Manufacturer = x.Manufacturer,
            ManufacturerNumber = x.ManufacturerNumber,
            VendorId = x.VendorId,
            VendorName = x.Vendor?.Name,
            Brand = x.Brand,
            Model = x.Model,
            Number = x.Number,
            ProcurementStaff = x.ProcurementStaff,
            Imei = x.Imei,
            Os = x.Os,
            OsVersion = x.OsVersion,
            InspectionDate = x.InspectionDate,
            MaintenanceDate = x.MaintenanceDate,
            Place = x.Place,
            CostPrice = x.CostPrice,
            YearsUse = x.YearsUse,
            DaysUse = x.DaysUse,
            PriceUse = x.PriceUse,
            Custodian = custodianDisplayName,
            CustodianAccount = x.CustodianAccount,
            DepartmentOptionId = x.DepartmentOptionId,
            DepartmentName = x.DepartmentOption == null
                ? null
                : DepartmentTeamRules.DisplayName(x.DepartmentOption.Name, x.DepartmentOption.Value),
            OwnerTeamOptionId = x.OwnerTeamOptionId,
            OwnerTeamName = x.OwnerTeamOption == null ? null : DepartmentTeamRules.DisplayName(x.OwnerTeamOption.Name, x.OwnerTeamOption.Value),
            Agent = x.Agent,
            ReservationStatus = x.ReservationStatus,
            Feature = x.Feature,
            Spec = x.Spec,
            Note = x.Note,
            EnvironmentGroupDeviceId = environmentAssignment?.Id,
            EnvironmentGroupId = environmentAssignment?.EquipmentGroupId,
            EnvironmentGroupName = environmentAssignment?.EquipmentGroup.Name,
            IsInEnvironment = environmentAssignment?.IsInEnvironment,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt,
            LifecycleRecords = lifecycleRecords,
            Files = x.Files
                .OrderByDescending(f => f.CreatedAt)
                .Select(file => ToFileDto(file, outboxes, verifiedFiles.Contains(file.Id)))
                .ToList()
        };
    }

    private ApparatusFileDto ToFileDto(ApparatusFile x,
        IReadOnlyDictionary<Guid, KmIntegrationOutbox> outboxes,
        bool isFormalArchiveVerified = false)
    {
        outboxes.TryGetValue(x.Id, out var outbox);
        var isDocument = x.Apparatus?.ModuleCode == "equipment" &&
            !ManagedAssetFileStorage.IsImage(x.ContentType, x.OriginalFileName ?? x.FileName);
        return new ApparatusFileDto
        {
            Id = x.Id,
            ApparatusId = x.ApparatusId,
            FileName = x.OriginalFileName ?? x.FileName,
            StoredFileName = x.FileName,
            ContentType = x.ContentType,
            FileSize = x.FileSize,
            CreatedAt = x.CreatedAt,
            ImageUrl = $"/api/assets/files/{x.Id}/image",
            DownloadUrl = $"/api/assets/files/{x.Id}/content",
            IsImage = ManagedAssetFileStorage.IsImage(x.ContentType, x.OriginalFileName ?? x.FileName),
            Sha256 = x.Sha256,
            ArchiveStatus = x.ArchiveStatus,
            ArchiveError = x.ArchiveError,
            IsKmEligibleDocument = isDocument,
            HasKmSchedule = outbox is not null,
            KmVersion = outbox?.Version,
            KmEnabled = _kmOptions.Enabled && _kmOptions.EquipmentEnabled,
            KmStatus = outbox?.Status.ToString(),
            KmError = outbox?.LastErrorMessage,
            MetadataJson = x.MetadataJson,
            MetadataSource = x.MetadataSource,
            MetadataVersion = x.MetadataVersion,
            MetadataVerificationStatus = x.MetadataVerificationStatus,
            CanDelete = outbox is null,
            IsFormalArchiveVerified = isFormalArchiveVerified
        };
    }

    private static async Task<HashSet<Guid>> VerifyFormalFilesAsync(IEnumerable<ApparatusFile> files)
    {
        var verified = new HashSet<Guid>();
        foreach (var file in files)
        {
            if (file.ArchiveStatus == FileArchiveStatuses.Archived)
            {
                verified.Add(file.Id);
                continue;
            }
            if (file.ArchiveStatus is not null) continue;
            var path = Path.Combine(file.FilePath, file.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length != file.FileSize) continue;
            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                await using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            }
            verified.Add(file.Id);
        }
        return verified;
    }

    private static string ImageContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };

    private async Task<IReadOnlyDictionary<Guid, KmIntegrationOutbox>> LoadEquipmentOutboxesAsync(
        IEnumerable<ApparatusFile> files)
    {
        var ids = files.Select(x => x.Id).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, KmIntegrationOutbox>();
        return await _db.KmIntegrationOutbox.AsNoTracking()
            .Where(x => x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityType == KmIntegrationSourceEntityTypes.ApparatusFile &&
                ids.Contains(x.SourceEntityId))
            .ToDictionaryAsync(x => x.SourceEntityId);
    }

    private async Task<Vendor?> ValidateNewEquipmentVendorAsync(string moduleCode, ApparatusUpsertRequest request)
    {
        if (moduleCode != "equipment") return null;
        if (!request.VendorId.HasValue)
            throw new InvalidOperationException("新增設備必須選擇有效且啟用中的廠商。");
        return await _db.Vendors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.VendorId.Value && x.IsActive)
            ?? throw new InvalidOperationException("新增設備必須選擇有效且啟用中的廠商。");
    }

    private async Task<Vendor?> ValidateEquipmentVendorUpdateAsync(string moduleCode, Apparatus entity,
        ApparatusUpsertRequest request)
    {
        if (moduleCode != "equipment") return null;
        if (request.VendorId == entity.VendorId)
            return request.VendorId.HasValue
                ? await _db.Vendors.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.VendorId.Value)
                : null; // Historical links remain valid after a vendor is disabled.
        if (!request.VendorId.HasValue)
        {
            if (entity.VendorId.HasValue) throw new InvalidOperationException("不可清除設備既有的正式廠商關聯。");
            return null;
        }
        return await _db.Vendors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.VendorId.Value && x.IsActive)
            ?? throw new InvalidOperationException("只能選擇啟用中的廠商。");
    }

    private async Task UploadManagedEquipmentFilesAsync(Apparatus apparatus, IReadOnlyList<IFormFile> files,
        string? uploadEmp, Guid? approvalFileId)
    {
        if (string.IsNullOrWhiteSpace(apparatus.ProductsId))
            throw new InvalidOperationException("此設備缺少資產編號，請補齊後再正式歸檔附件；系統不會產生假資產編號。");
        apparatus.StorageFolderName ??= ManagedAssetFileStorage.SafeFolderName(
            $"{apparatus.Name}_{apparatus.ProductsId}");
        await _db.SaveChangesAsync();
        var folder = await _managedStorage.EquipmentFolderAsync(apparatus.StorageFolderName);
        var archived = 0;
        var failures = new List<string>();
        var validated = files.Select(file => new
        {
            File = file,
            OriginalName = ManagedAssetFileStorage.ValidateOriginalFileName(file.FileName, file.Length)
        }).ToList();
        foreach (var item in validated)
        {
            var file = item.File;
            var original = item.OriginalName;
            var fileId = approvalFileId ?? Guid.NewGuid();
            var existing = approvalFileId.HasValue
                ? await _db.ApparatusFiles.SingleOrDefaultAsync(x => x.Id == fileId)
                : null;
            if (existing is not null)
            {
                if (existing.ApparatusId != apparatus.Id || existing.OriginalFileName != original ||
                    existing.FileSize != file.Length)
                    throw new InvalidOperationException("Approved Asset file identity changed during retry.");
                if (existing.ArchiveStatus == FileArchiveStatuses.Archived &&
                    File.Exists(Path.Combine(existing.FilePath, existing.FileName)))
                {
                    await EnsureEquipmentOutboxAsync(apparatus, existing);
                    archived++;
                    continue;
                }
            }
            var now = DateTime.UtcNow;
            var entity = existing ?? new ApparatusFile
            {
                Id = fileId, ApparatusId = apparatus.Id, OriginalFileName = original,
                FileName = ManagedAssetFileStorage.StoredFileName(fileId, original), FilePath = folder,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSize = file.Length, UploadEmp = string.IsNullOrWhiteSpace(uploadEmp) ? "SYSTEM" : uploadEmp.Trim(),
                CreatedAt = now, MetadataVerificationStatus = MetadataVerificationStatuses.NotApplicable
            };
            entity.ArchiveStatus = FileArchiveStatuses.Pending;
            entity.ArchiveError = null;
            entity.ArchiveAttemptCount++;
            entity.ArchiveUpdatedAt = now;
            if (existing is null) _db.ApparatusFiles.Add(entity);
            await _db.SaveChangesAsync();
            try
            {
                await using var source = file.OpenReadStream();
                entity.Sha256 = await _managedStorage.ArchiveAsync(source, folder, entity.FileName);
                entity.ArchiveStatus = FileArchiveStatuses.Archived;
                entity.ArchiveNextRetryAt = null;
                entity.ArchiveUpdatedAt = DateTime.UtcNow;
                await EnsureEquipmentOutboxAsync(apparatus, entity, saveChanges: false);
                await _db.SaveChangesAsync();
                archived++;
            }
            catch (Exception ex)
            {
                entity.ArchiveStatus = FileArchiveStatuses.Failed;
                entity.ArchiveError = ex.Message[..Math.Min(ex.Message.Length, 2000)];
                entity.ArchiveNextRetryAt = DateTime.UtcNow.AddMinutes(5);
                entity.ArchiveUpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                failures.Add(original);
            }
        }
        await RebuildLatestRawDataAsync("equipment", apparatus.Id);
        if (failures.Count != 0)
            throw new InvalidOperationException($"設備已建立／保留；附件已歸檔 {archived} 份，{failures.Count} 份失敗並已記錄：{string.Join("、", failures)}");
    }

    private async Task EnsureEquipmentOutboxAsync(
        Apparatus apparatus, ApparatusFile file, bool saveChanges = true)
    {
        var originalName = file.OriginalFileName ?? file.FileName;
        if (ManagedAssetFileStorage.IsImage(file.ContentType, originalName)) return;
        if (!await _db.KmIntegrationOutbox.AnyAsync(x =>
                x.EventType == KmIntegrationEventTypes.EquipmentDocumentArchived &&
                x.SourceEntityId == file.Id))
            _db.KmIntegrationOutbox.Add(
                KmIntegrationOutbox.ForArchivedEquipmentDocument(apparatus, file, DateTime.UtcNow));
        if (saveChanges) await _db.SaveChangesAsync();
    }

    private static void ValidateUpsert(ApparatusUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("名稱不可空白");
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            throw new InvalidOperationException("類別不可空白");
        }

    }

    private async Task<(string? CustodianAccount, Guid? OwnerTeamOptionId)> ValidateOwnershipAsync(
        ApparatusUpsertRequest request,
        CancellationToken cancellationToken)
    {
        string? custodianAccount = null;
        SystemOption? department = null;
        department = await RequireOwnershipDepartmentAsync(request.DepartmentOptionId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.CustodianAccount))
        {
            custodianAccount = request.CustodianAccount.Trim().ToLowerInvariant();
            var selectedUser = request.CustodianAccount.Trim();
            if (selectedUser is null)
                throw new InvalidOperationException($"保管人帳號不存在：{request.CustodianAccount.Trim()}。");
            custodianAccount = selectedUser.Trim().ToLowerInvariant();
        }
        else
        {
            throw new InvalidOperationException("請選擇保管人。");
        }

        SystemOption? selectedTeam = null;
        if (request.OwnerTeamOptionId.HasValue)
        {
            var enabledTeams = await _db.SystemOptions.AsNoTracking()
                .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled)
                .ToListAsync(cancellationToken);
            var allowedTeams = DepartmentTeamRules.GetAllowedTeamsForDepartment(
                department.Value,
                enabledTeams,
                team => team.Name,
                team => team.Value);
            selectedTeam = allowedTeams.SingleOrDefault(team => team.Id == request.OwnerTeamOptionId.Value);
            if (allowedTeams.All(team => team.Id != request.OwnerTeamOptionId.Value))
                throw new InvalidOperationException("設備所屬 Team 必須是所選部門可使用的啟用 Team 選項。");
        }
        else
        {
            throw new InvalidOperationException("請選擇設備所屬 Team。");
        }

        if (!string.IsNullOrWhiteSpace(custodianAccount) && department is not null && selectedTeam is not null)
        {
            IReadOnlyList<SharedIdentityAccountListItem> accounts;
            try
            {
                accounts = await _provisioningClient.ListAccountsAsync(cancellationToken);
            }
            catch (SharedIdentityProvisioningException exception)
            {
                throw new InvalidOperationException("目前無法驗證保管人，請稍後再試。", exception);
            }

            var custodian = accounts.SingleOrDefault(x =>
                string.Equals(x.Account.Trim(), custodianAccount, StringComparison.OrdinalIgnoreCase));
            if (custodian is null ||
                !string.Equals(custodian.AccountStatus.Trim(), "Active", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(custodian.SystemAccessStatus.Trim(), "Active", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(custodian.Department.Trim(), department.Value.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(custodian.TeamCode?.Trim(), selectedTeam.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("保管人必須屬於所選部門與 Team。");
            }
        }

        return (custodianAccount, request.OwnerTeamOptionId);
    }

    private async Task<SystemOption> RequireOwnershipDepartmentAsync(
        Guid? optionId,
        CancellationToken cancellationToken)
    {
        var department = await _db.SystemOptions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == optionId &&
                 x.Category == SystemOptionCategories.Department &&
                 x.IsEnabled,
            cancellationToken);
        if (department is null || string.IsNullOrWhiteSpace(department.Value))
            throw new InvalidOperationException("請選擇有效且啟用中的部門。");

        return department;
    }

    private static string NormalizeModuleCode(string? moduleCode)
    {
        return string.IsNullOrWhiteSpace(moduleCode) ? "equipment" : moduleCode.Trim();
    }

    private static string CleanFileName(string value)
    {
        return CleanFolderName(Path.GetFileName(value));
    }

    private static string CleanFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string((value ?? string.Empty)
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned.Trim();
    }
}

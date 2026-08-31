using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class ApparatusService : IApparatusService
{
    private readonly AppDbContext _db;
    private readonly ApparatusSettings _settings;
    private readonly RawDataSettings _rawDataSettings;
    private readonly IRawDataExportService _rawDataExportService;

    public ApparatusService(
        AppDbContext db,
        IOptions<ApparatusSettings> options,
        IOptions<RawDataSettings> rawDataOptions,
        IRawDataExportService rawDataExportService)
    {
        _db = db;
        _settings = options.Value;
        _rawDataSettings = rawDataOptions.Value;
        _rawDataExportService = rawDataExportService;
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

        var users = await _db.Users.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Account)
            .Select(x => new ApparatusCustodianOptionDto
            {
                Account = x.Account,
                DisplayName = x.DisplayName,
                Department = x.Department
            })
            .ToListAsync(cancellationToken);

        return new ApparatusOwnershipOptionsDto { Teams = teams, Users = users };
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
                Model = x.Model,
                Number = x.Number,
                ReservationStatus = x.ReservationStatus,
                Place = x.Place,
                Custodian = x.Custodian,
                CustodianAccount = x.CustodianAccount,
                OwnerTeamOptionId = x.OwnerTeamOptionId,
                OwnerTeamName = x.OwnerTeamOption == null ? null : x.OwnerTeamOption.Name,
                Agent = x.Agent,
                Note = x.Note
            })
            .ToListAsync();
    }

    public async Task<ApparatusDetailDto?> GetByIdAsync(string moduleCode, string id)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var entity = await _db.Apparatuses
            .Include(x => x.Files)
            .Include(x => x.OwnerTeamOption)
            .FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == id);

        Console.WriteLine($"[ApparatusService] GetById. moduleCode={moduleCode}, id={id}, found={entity != null}");

        return entity == null ? null : ToDetailDto(entity);
    }

    public async Task<string> CreateAsync(string moduleCode, ApparatusUpsertRequest request)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        ValidateUpsert(request);
        var ownership = await ValidateOwnershipAsync(request, CancellationToken.None);

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
            Manufacturer = request.Manufacturer,
            ManufacturerNumber = request.ManufacturerNumber,
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
            CustodianDepartment = request.CustodianDepartment,
            Custodian = request.Custodian!.Trim(),
            CustodianAccount = ownership.CustodianAccount,
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

        var folder = await GetApparatusFolderAsync(moduleCode, id);
        Directory.CreateDirectory(folder);

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
        var ownership = await ValidateOwnershipAsync(request, CancellationToken.None);
        ApplyConcurrencyToken(entity, request.RowVersion);

        entity.ModuleCode = moduleCode;
        entity.ProductsId = request.ProductsId;
        entity.Name = request.Name.Trim();
        entity.NameEn = request.NameEn;
        entity.Kind = request.Kind!.Trim();
        entity.PartNo = request.PartNo;
        entity.Manufacturer = request.Manufacturer;
        entity.ManufacturerNumber = request.ManufacturerNumber;
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
        entity.CustodianDepartment = request.CustodianDepartment;
        entity.Custodian = request.Custodian!.Trim();
        entity.CustodianAccount = ownership.CustodianAccount;
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

        var exists = await _db.Apparatuses.AnyAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);
        if (!exists)
        {
            return new();
        }

        return await _db.ApparatusFiles
            .Where(x => x.ApparatusId == apparatusId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToFileDto(x))
            .ToListAsync();
    }

    public async Task UploadFilesAsync(string moduleCode, string apparatusId, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        if (files.Count == 0)
        {
            throw new InvalidOperationException("沒有收到檔案");
        }

        var exists = await _db.Apparatuses.AnyAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);
        if (!exists)
        {
            throw new InvalidOperationException($"找不到資產：moduleCode={moduleCode}, id={apparatusId}");
        }

        var folder = await GetApparatusFolderAsync(moduleCode, apparatusId);
        Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            if (file.Length <= 0)
            {
                throw new InvalidOperationException($"檔案是空的：{file.FileName}");
            }

            var fileId = Guid.NewGuid();
            var safeFileName = CleanFileName(file.FileName);
            var fileFolder = Path.Combine(folder, fileId.ToString("N"));
            Directory.CreateDirectory(fileFolder);

            var fullPath = Path.Combine(fileFolder, safeFileName);

            await using (var stream = File.Create(fullPath))
            {
                await file.CopyToAsync(stream);
            }

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

    public async Task<(byte[] Content, string FileName, string ContentType)> GetFileContentAsync(Guid fileId)
    {
        var file = await _db.ApparatusFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
        {
            throw new FileNotFoundException("找不到檔案資料");
        }

        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到實體檔案：{fullPath}");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath);
        return (bytes, file.FileName, file.ContentType ?? "application/octet-stream");
    }

    public async Task<bool> DeleteFileAsync(Guid fileId)
    {
        var file = await _db.ApparatusFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
        {
            return false;
        }

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

    private async Task RebuildLatestRawDataAsync(string moduleCode, string apparatusId)
    {
        moduleCode = NormalizeModuleCode(moduleCode);

        var entity = await _db.Apparatuses
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.ModuleCode == moduleCode && x.Id == apparatusId);

        if (entity == null)
        {
            Console.WriteLine($"[ApparatusService] Rebuild RAW skipped. asset not found. moduleCode={moduleCode}, id={apparatusId}");
            return;
        }

        var folder = await GetApparatusFolderAsync(moduleCode, apparatusId);

        var files = entity.Files
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
            custodianDepartment = entity.CustodianDepartment,
            custodian = entity.Custodian,
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

    private static ApparatusDetailDto ToDetailDto(Apparatus x)
    {
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
            CustodianDepartment = x.CustodianDepartment,
            Custodian = x.Custodian,
            CustodianAccount = x.CustodianAccount,
            OwnerTeamOptionId = x.OwnerTeamOptionId,
            OwnerTeamName = x.OwnerTeamOption?.Name,
            Agent = x.Agent,
            ReservationStatus = x.ReservationStatus,
            Feature = x.Feature,
            Spec = x.Spec,
            Note = x.Note,
            Files = x.Files
                .OrderByDescending(f => f.CreatedAt)
                .Select(ToFileDto)
                .ToList()
        };
    }

    private static ApparatusFileDto ToFileDto(ApparatusFile x)
    {
        return new ApparatusFileDto
        {
            Id = x.Id,
            ApparatusId = x.ApparatusId,
            FileName = x.FileName,
            ContentType = x.ContentType,
            FileSize = x.FileSize,
            CreatedAt = x.CreatedAt,
            ImageUrl = $"/api/assets/files/{x.Id}/content"
        };
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

        if (string.IsNullOrWhiteSpace(request.Custodian))
        {
            throw new InvalidOperationException("保管人不可空白");
        }
    }

    private async Task<(string? CustodianAccount, Guid? OwnerTeamOptionId)> ValidateOwnershipAsync(
        ApparatusUpsertRequest request,
        CancellationToken cancellationToken)
    {
        string? custodianAccount = null;
        if (!string.IsNullOrWhiteSpace(request.CustodianAccount))
        {
            custodianAccount = request.CustodianAccount.Trim().ToLowerInvariant();
            var exists = await _db.Users.AsNoTracking()
                .AnyAsync(x => x.Account.ToLower() == custodianAccount, cancellationToken);
            if (!exists)
                throw new InvalidOperationException($"保管人帳號不存在：{request.CustodianAccount.Trim()}。");
        }

        if (request.OwnerTeamOptionId.HasValue)
        {
            var teamExists = await _db.SystemOptions.AsNoTracking().AnyAsync(
                x => x.Id == request.OwnerTeamOptionId.Value
                    && x.Category == SystemOptionCategories.Team
                    && x.IsEnabled,
                cancellationToken);
            if (!teamExists)
                throw new InvalidOperationException("設備所屬 Team 必須是啟用中的 Team 選項。");
        }

        return (custodianAccount, request.OwnerTeamOptionId);
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

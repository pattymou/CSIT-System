using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class RawDataExportService : IRawDataExportService
{
    private readonly RawDataSettings _settings;

    public RawDataExportService(IOptions<RawDataSettings> options)
    {
        _settings = options.Value;
    }

    public async Task<RawDataExportResult> ExportAsync(RawDataExportRequest request)
    {
        Console.WriteLine($"[RawDataExportService] Export start. sourceType={request.SourceType}, localFile={request.LocalFilePath}");

        if (!_settings.Enabled)
        {
            return new RawDataExportResult
            {
                Success = true,
                ErrorMessage = "RawData export disabled."
            };
        }

        if (string.IsNullOrWhiteSpace(_settings.NasRootPath))
        {
            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = "RawData:NasRootPath 未設定"
            };
        }

        if (string.IsNullOrWhiteSpace(request.LocalFilePath) || !File.Exists(request.LocalFilePath))
        {
            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = $"找不到原始檔：{request.LocalFilePath}"
            };
        }

        try
        {
            var nasFilePath = BuildNasFilePathByLocalPath(request.LocalFilePath);
            var targetFolder = Path.GetDirectoryName(nasFilePath)
                ?? throw new InvalidOperationException($"無法取得 NAS 目標資料夾：{nasFilePath}");

            Directory.CreateDirectory(targetFolder);

            File.Copy(request.LocalFilePath, nasFilePath, overwrite: true);

            var rawJsonPath = Path.Combine(targetFolder, "rawdata.json");

            var rawObject = new
            {
                schemaVersion = "2.0",
                mode = "mirror-local-path",
                exportedAt = DateTime.UtcNow,
                sourceSystem = request.SourceSystem,
                sourceType = request.SourceType,
                moduleCode = request.ModuleCode,
                recordId = request.RecordId,
                recordNo = request.RecordNo,
                caseId = request.CaseId,
                caseNo = request.CaseNo,
                taskId = request.TaskId,
                taskNo = request.TaskNo,
                file = new
                {
                    fileId = request.FileId,
                    fileName = request.FileName,
                    contentType = request.ContentType,
                    fileSize = request.FileSize,
                    localFilePath = request.LocalFilePath,
                    nasFolderPath = targetFolder,
                    nasFilePath,
                    rawJsonPath,
                    uploadedBy = request.UploadEmp,
                    uploadedAt = request.UploadedAt
                },
                parsedMetadata = ParseBasicMetadata(request.FileName)
            };

            await WriteJsonAsync(rawJsonPath, rawObject);

            Console.WriteLine($"[RawDataExportService] Export success. folder={targetFolder}, nasFilePath={nasFilePath}, rawJsonPath={rawJsonPath}");

            return new RawDataExportResult
            {
                Success = true,
                NasFolderPath = targetFolder,
                NasFilePath = nasFilePath,
                RawJsonPath = rawJsonPath
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RawDataExportService] Export failed: {ex}");

            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<RawDataExportResult> ExportLatestPackageAsync(RawDataLatestPackageRequest request)
    {
        Console.WriteLine($"[RawDataExportService] Latest package export start. sourceType={request.SourceType}, moduleCode={request.ModuleCode}, entityId={request.EntityId}, files={request.Files.Count}");

        if (!_settings.Enabled)
        {
            return new RawDataExportResult
            {
                Success = true,
                ErrorMessage = "RawData export disabled."
            };
        }

        if (string.IsNullOrWhiteSpace(_settings.NasRootPath))
        {
            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = "RawData:NasRootPath 未設定"
            };
        }

        if (string.IsNullOrWhiteSpace(request.EntityId))
        {
            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = "EntityId 不可空白"
            };
        }

        try
        {
            var localRootFolder = ResolveLocalRootFolder(request);
            var targetFolder = BuildNasFolderPathByLocalFolder(localRootFolder);

            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, recursive: true);
                Console.WriteLine($"[RawDataExportService] Old latest package deleted: {targetFolder}");
            }

            Directory.CreateDirectory(targetFolder);

            var exportedFiles = new List<object>();

            foreach (var file in request.Files)
            {
                if (string.IsNullOrWhiteSpace(file.LocalFilePath) || !File.Exists(file.LocalFilePath))
                {
                    Console.WriteLine($"[RawDataExportService] Latest package file skipped, local file not found: {file.LocalFilePath}");
                    continue;
                }

                var sourceFullPath = Path.GetFullPath(file.LocalFilePath);
                var relativeFilePath = Path.GetRelativePath(localRootFolder, sourceFullPath);
                var nasFilePath = Path.Combine(targetFolder, relativeFilePath);

                var nasFileFolder = Path.GetDirectoryName(nasFilePath);
                if (!string.IsNullOrWhiteSpace(nasFileFolder))
                {
                    Directory.CreateDirectory(nasFileFolder);
                }

                File.Copy(sourceFullPath, nasFilePath, overwrite: true);

                exportedFiles.Add(new
                {
                    fileId = file.FileId,
                    fileName = file.FileName,
                    contentType = file.ContentType,
                    fileSize = file.FileSize,
                    localFilePath = file.LocalFilePath,
                    nasFilePath,
                    uploadedBy = file.UploadEmp,
                    uploadedAt = file.UploadedAt,
                    parsedMetadata = ParseBasicMetadata(file.FileName)
                });

                Console.WriteLine($"[RawDataExportService] Latest package file copied. fileId={file.FileId}, nasFilePath={nasFilePath}");
            }

            var rawJsonPath = Path.Combine(targetFolder, "rawdata.json");

            object rawObject;

            if (string.Equals(request.SourceType, "ThreeLevelProject", StringComparison.OrdinalIgnoreCase))
            {
                rawObject = request.Metadata;
            }
            else
            {
                rawObject = new
                {
                    schemaVersion = "2.0",
                    mode = "latest-package-mirror-local-path",
                    exportedAt = DateTime.UtcNow,
                    sourceSystem = request.SourceSystem,
                    sourceType = request.SourceType,
                    moduleCode = request.ModuleCode,
                    entityId = request.EntityId,
                    localRootFolder,
                    nasFolderPath = targetFolder,
                    metadata = request.Metadata,
                    files = exportedFiles
                };
            }

            await WriteJsonAsync(rawJsonPath, rawObject);

            Console.WriteLine($"[RawDataExportService] Latest package export success. folder={targetFolder}, files={exportedFiles.Count}");

            return new RawDataExportResult
            {
                Success = true,
                NasFolderPath = targetFolder,
                RawJsonPath = rawJsonPath
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RawDataExportService] Latest package export failed: {ex}");

            return new RawDataExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<bool> DeleteLatestPackageAsync(string sourceType, string moduleCode, string entityId)
    {
        if (!_settings.Enabled)
        {
            Console.WriteLine("[RawDataExportService] Delete latest package skipped. RawData disabled.");
            return Task.FromResult(true);
        }

        if (string.IsNullOrWhiteSpace(_settings.NasRootPath))
        {
            Console.WriteLine("[RawDataExportService] Delete latest package skipped. NasRootPath empty.");
            return Task.FromResult(false);
        }

        var targetFolder = BuildFallbackLatestPackageFolder(sourceType, moduleCode, entityId);

        try
        {
            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, recursive: true);
                Console.WriteLine($"[RawDataExportService] Latest package deleted: {targetFolder}");
            }
            else
            {
                Console.WriteLine($"[RawDataExportService] Latest package folder not found, skipped: {targetFolder}");
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RawDataExportService] Delete latest package failed. folder={targetFolder}, error={ex}");
            return Task.FromResult(false);
        }
    }

    private string BuildNasFilePathByLocalPath(string localFilePath)
    {
        var fullLocalPath = Path.GetFullPath(localFilePath);

        var root = Path.GetPathRoot(fullLocalPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"無法解析本機路徑根目錄：{localFilePath}");
        }

        var relativePath = fullLocalPath[root.Length..].TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return Path.Combine(_settings.NasRootPath, relativePath);
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

        return Path.Combine(_settings.NasRootPath, relativePath);
    }

    private string ResolveLocalRootFolder(RawDataLatestPackageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.LocalRootFolder))
        {
            return Path.GetFullPath(request.LocalRootFolder);
        }

        var validFiles = request.Files
            .Where(x => !string.IsNullOrWhiteSpace(x.LocalFilePath))
            .Select(x => new
            {
                File = x,
                FullPath = Path.GetFullPath(x.LocalFilePath),
                Directory = Path.GetDirectoryName(Path.GetFullPath(x.LocalFilePath)) ?? string.Empty
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Directory))
            .ToList();

        if (validFiles.Count == 0)
        {
            return BuildFallbackLatestPackageFolder(request.SourceType, request.ModuleCode, request.EntityId);
        }

        if (validFiles.Count == 1)
        {
            var only = validFiles[0];
            var directoryName = Path.GetFileName(only.Directory);

            if (string.Equals(directoryName, CleanPathPart(only.File.FileId), StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(only.Directory)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    return parent;
                }
            }

            return only.Directory;
        }

        var common = GetCommonDirectory(validFiles.Select(x => x.Directory).ToList());

        if (string.IsNullOrWhiteSpace(common))
        {
            return validFiles[0].Directory;
        }

        return common;
    }

    private string BuildFallbackLatestPackageFolder(string sourceType, string moduleCode, string entityId)
    {
        return Path.Combine(
            _settings.NasRootPath,
            CleanPathPart(sourceType),
            CleanPathPart(moduleCode),
            CleanPathPart(entityId));
    }

    private static string GetCommonDirectory(List<string> directories)
    {
        if (directories.Count == 0)
        {
            return string.Empty;
        }

        var normalized = directories
            .Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var firstParts = normalized[0].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var commonParts = new List<string>();

        for (var i = 0; i < firstParts.Length; i++)
        {
            var part = firstParts[i];

            var allMatch = normalized.All(path =>
            {
                var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return parts.Length > i && string.Equals(parts[i], part, StringComparison.OrdinalIgnoreCase);
            });

            if (!allMatch)
            {
                break;
            }

            commonParts.Add(part);
        }

        if (commonParts.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Path.DirectorySeparatorChar, commonParts);
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(value, jsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private static object ParseBasicMetadata(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var parts = nameWithoutExt.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new
        {
            originalName = fileName,
            nameWithoutExtension = nameWithoutExt,
            parts,
            guessedCategory = GuessCategory(parts)
        };
    }

    private static string GuessCategory(string[] parts)
    {
        var joined = string.Join("-", parts).ToUpperInvariant();

        if (joined.Contains("WL") || joined.Contains("WIFI") || joined.Contains("WI-FI"))
            return "WiFi";

        if (joined.Contains("5G") || joined.Contains("NR"))
            return "5G";

        if (joined.Contains("SMALLCELL") || joined.Contains("SMALL-CELL"))
            return "SmallCell";

        return "Unknown";
    }

    private static string CleanFileName(string value)
    {
        return CleanPathPart(Path.GetFileName(value));
    }

    private static string CleanPathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string((value ?? string.Empty)
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned.Trim();
    }
}
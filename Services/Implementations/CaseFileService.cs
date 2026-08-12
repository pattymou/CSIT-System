using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Models.Config;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public class CaseFileService : ICaseFileService
{
    private const string TaskReportUploadKind = "TestReport";

    private readonly AppDbContext _db;
    private readonly UploadSettings _settings;
    private readonly RawDataSettings _rawDataSettings;
    private readonly IRawDataExportService _rawDataExportService;

    public CaseFileService(
        AppDbContext db,
        IOptions<UploadSettings> options,
        IOptions<RawDataSettings> rawDataOptions,
        IRawDataExportService rawDataExportService)
    {
        _db = db;
        _settings = options.Value;
        _rawDataSettings = rawDataOptions.Value;
        _rawDataExportService = rawDataExportService;
    }

    public async Task<List<ModuleCaseFileDto>> GetFilesAsync(Guid caseId)
    {
        return await _db.ModuleCaseFiles
            .Where(x => x.CaseId == caseId && x.TaskId == null && string.IsNullOrWhiteSpace(x.TaskNo))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToDto(x))
            .ToListAsync();
    }

    public async Task<List<ModuleCaseFileDto>> GetFilesByCaseNoAsync(Guid recordId, string caseNo)
    {
        return await _db.ModuleCaseFiles
            .Where(x => x.RecordId == recordId && x.CaseNo == caseNo && x.TaskId == null && string.IsNullOrWhiteSpace(x.TaskNo))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToDto(x))
            .ToListAsync();
    }

    public async Task<List<ModuleCaseFileDto>> GetTaskFilesAsync(Guid taskId)
    {
        return await _db.ModuleCaseFiles
            .Where(x => x.TaskId == taskId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToDto(x))
            .ToListAsync();
    }

    public async Task<List<ModuleCaseFileDto>> GetTaskFilesByTaskNoAsync(Guid caseId, string taskNo)
    {
        return await _db.ModuleCaseFiles
            .Where(x => x.CaseId == caseId && x.TaskNo == taskNo)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToDto(x))
            .ToListAsync();
    }

    public async Task UploadAsync(Guid caseId, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        var caseEntity = await _db.ModuleRecordCases
            .Include(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == caseId);

        if (caseEntity == null)
            throw new InvalidOperationException("找不到 Case");

        await SaveFilesAsync(
            recordId: caseEntity.RecordId,
            caseId: caseEntity.Id,
            caseNo: caseEntity.CaseNo,
            taskId: null,
            taskNo: null,
            taskFolderName: null,
            projectName: caseEntity.Record.Name,
            folderCaseName: caseEntity.Name,
            uploadKind: _settings.UploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task UploadByCaseNoAsync(Guid recordId, string caseNo, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        if (string.IsNullOrWhiteSpace(caseNo))
            throw new InvalidOperationException("CaseNo 不可為空");

        var record = await _db.ModuleRecords.FirstOrDefaultAsync(x => x.Id == recordId);
        if (record == null)
            throw new InvalidOperationException("找不到主單");

        var existingCase = await _db.ModuleRecordCases
            .FirstOrDefaultAsync(x => x.RecordId == recordId && x.CaseNo == caseNo);

        var folderCaseName = string.IsNullOrWhiteSpace(existingCase?.Name)
            ? caseNo
            : existingCase.Name;

        await SaveFilesAsync(
            recordId: recordId,
            caseId: existingCase?.Id,
            caseNo: caseNo,
            taskId: null,
            taskNo: null,
            taskFolderName: null,
            projectName: record.Name,
            folderCaseName: folderCaseName,
            uploadKind: _settings.UploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task UploadTaskFilesAsync(Guid taskId, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        var task = await _db.ModuleRecordTasks
            .Include(x => x.Case)
            .ThenInclude(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == taskId);

        if (task == null)
            throw new InvalidOperationException("找不到子任務");

        await SaveFilesAsync(
            recordId: task.Case.RecordId,
            caseId: task.CaseId,
            caseNo: task.Case.CaseNo,
            taskId: task.Id,
            taskNo: task.TaskNo,
            taskFolderName: task.Name,
            projectName: task.Case.Record.Name,
            folderCaseName: task.Case.Name,
            uploadKind: _settings.UploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task UploadTaskFilesByTaskNoAsync(Guid caseId, string taskNo, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        if (string.IsNullOrWhiteSpace(taskNo))
            throw new InvalidOperationException("TaskNo 不可為空");

        var caseEntity = await _db.ModuleRecordCases
            .Include(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == caseId);

        if (caseEntity == null)
            throw new InvalidOperationException("找不到 Case");

        await SaveFilesAsync(
            recordId: caseEntity.RecordId,
            caseId: caseEntity.Id,
            caseNo: caseEntity.CaseNo,
            taskId: null,
            taskNo: taskNo,
            taskFolderName: taskNo,
            projectName: caseEntity.Record.Name,
            folderCaseName: caseEntity.Name,
            uploadKind: _settings.UploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task UploadTaskReportAsync(Guid taskId, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        var task = await _db.ModuleRecordTasks
            .Include(x => x.Case)
            .ThenInclude(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == taskId);

        if (task == null)
            throw new InvalidOperationException("找不到子任務");

        await SaveFilesAsync(
            recordId: task.Case.RecordId,
            caseId: task.CaseId,
            caseNo: task.Case.CaseNo,
            taskId: task.Id,
            taskNo: task.TaskNo,
            taskFolderName: task.Name,
            projectName: task.Case.Record.Name,
            folderCaseName: task.Case.Name,
            uploadKind: TaskReportUploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task UploadTaskReportByTaskNoAsync(Guid caseId, string taskNo, IReadOnlyList<IFormFile> files, string? uploadEmp)
    {
        if (string.IsNullOrWhiteSpace(taskNo))
            throw new InvalidOperationException("TaskNo 不可為空");

        var caseEntity = await _db.ModuleRecordCases
            .Include(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == caseId);

        if (caseEntity == null)
            throw new InvalidOperationException("找不到 Case");

        await SaveFilesAsync(
            recordId: caseEntity.RecordId,
            caseId: caseEntity.Id,
            caseNo: caseEntity.CaseNo,
            taskId: null,
            taskNo: taskNo,
            taskFolderName: taskNo,
            projectName: caseEntity.Record.Name,
            folderCaseName: caseEntity.Name,
            uploadKind: TaskReportUploadKind,
            files: files,
            uploadEmp: uploadEmp);
    }

    public async Task BindFilesToCaseAsync(Guid recordId, string caseNo, Guid caseId, string caseName)
    {
        var files = await _db.ModuleCaseFiles
            .Where(x => x.RecordId == recordId && x.CaseNo == caseNo)
            .ToListAsync();

        foreach (var file in files)
            file.CaseId = caseId;

        await _db.SaveChangesAsync();
        await MoveCaseFolderAsync(recordId, caseNo, caseNo, caseName);
    }

    public async Task BindFilesToTaskAsync(Guid caseId, string taskNo, Guid taskId)
    {
        var task = await _db.ModuleRecordTasks
            .Include(x => x.Case)
            .ThenInclude(x => x.Record)
            .FirstOrDefaultAsync(x => x.Id == taskId);

        if (task == null)
            throw new InvalidOperationException("找不到子任務");

        var files = await _db.ModuleCaseFiles
            .Where(x => x.CaseId == caseId && x.TaskNo == taskNo && x.TaskId == null)
            .ToListAsync();

        foreach (var file in files)
        {
            file.TaskId = taskId;
        }

        await _db.SaveChangesAsync();

        await MoveTaskFolderAfterBindAsync(task, files);

        Console.WriteLine($"[CaseFileService] Bind files to task done. caseId={caseId}, taskNo={taskNo}, taskId={taskId}, files={files.Count}");
    }

    public async Task MoveCaseFolderAsync(Guid recordId, string caseNo, string oldCaseName, string newCaseName)
    {
        if (string.IsNullOrWhiteSpace(newCaseName))
            return;

        if (string.Equals(oldCaseName, newCaseName, StringComparison.OrdinalIgnoreCase))
            return;

        var record = await _db.ModuleRecords.FirstOrDefaultAsync(x => x.Id == recordId);
        if (record == null)
            throw new InvalidOperationException("找不到主單");

        await MoveFolderAndUpdateFilesAsync(recordId, caseNo, record.Name, oldCaseName, newCaseName);
    }

    public async Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(Guid fileId)
    {
        var file = await _db.ModuleCaseFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
            throw new FileNotFoundException("找不到檔案資料");

        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到實體檔案");

        var bytes = await File.ReadAllBytesAsync(fullPath);
        return (bytes, file.FileName, file.ContentType ?? "application/octet-stream");
    }

    public async Task<bool> DeleteAsync(Guid fileId)
    {
        var file = await _db.ModuleCaseFiles.FirstOrDefaultAsync(x => x.Id == fileId);
        if (file == null)
            return false;

        var recordId = file.RecordId;
        var fullPath = Path.Combine(file.FilePath, file.FileName);

        DeleteNasMirrorFileByLocalPath(fullPath);
        DeletePhysicalFile(file);

        _db.ModuleCaseFiles.Remove(file);
        await _db.SaveChangesAsync();

        await RebuildProjectRawDataAsync(recordId);

        return true;
    }

    public async Task DeleteFilesByTaskIdAsync(Guid taskId)
    {
        var files = await _db.ModuleCaseFiles
            .Where(x => x.TaskId == taskId)
            .ToListAsync();

        foreach (var file in files)
        {
            DeleteNasMirrorFileByLocalPath(Path.Combine(file.FilePath, file.FileName));
            DeletePhysicalFile(file);
        }

        _db.ModuleCaseFiles.RemoveRange(files);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[CaseFileService] DeleteFilesByTaskId done. taskId={taskId}, files={files.Count}");
    }

    public async Task DeleteFilesByCaseIdAsync(Guid caseId)
    {
        var files = await _db.ModuleCaseFiles
            .Where(x => x.CaseId == caseId)
            .ToListAsync();

        foreach (var file in files)
        {
            DeleteNasMirrorFileByLocalPath(Path.Combine(file.FilePath, file.FileName));
            DeletePhysicalFile(file);
        }

        _db.ModuleCaseFiles.RemoveRange(files);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[CaseFileService] DeleteFilesByCaseId done. caseId={caseId}, files={files.Count}");
    }

    public async Task DeleteFilesByRecordIdAsync(Guid recordId)
    {
        var projectFolder = await GetProjectFolderAsync(recordId);

        var files = await _db.ModuleCaseFiles
            .Where(x => x.RecordId == recordId)
            .ToListAsync();

        foreach (var file in files)
        {
            DeleteNasMirrorFileByLocalPath(Path.Combine(file.FilePath, file.FileName));
            DeletePhysicalFile(file);
        }

        _db.ModuleCaseFiles.RemoveRange(files);
        await _db.SaveChangesAsync();

        DeleteNasMirrorFolderByLocalFolder(projectFolder);

        Console.WriteLine($"[CaseFileService] DeleteFilesByRecordId done. recordId={recordId}, files={files.Count}");
    }

    private async Task SaveFilesAsync(
        Guid recordId,
        Guid? caseId,
        string caseNo,
        Guid? taskId,
        string? taskNo,
        string? taskFolderName,
        string projectName,
        string folderCaseName,
        string uploadKind,
        IReadOnlyList<IFormFile> files,
        string? uploadEmp)
    {
        ValidateSettings();

        if (files.Count == 0)
            throw new InvalidOperationException("沒有收到任何檔案，已中止上傳。");

        var moduleCode = await GetModuleCodeByRecordIdAsync(recordId);
        var rootPath = await GetThreeLevelRootPathAsync(moduleCode);

        Console.WriteLine($"[CaseFileService] Start saving files. recordId={recordId}, caseNo={caseNo}, taskId={taskId}, taskNo={taskNo}, uploadKind={uploadKind}, files={files.Count}");

        foreach (var file in files)
        {
            if (file.Length <= 0)
                throw new InvalidOperationException($"檔案 {file.FileName} 是空檔案，請選擇有內容的檔案。");

            var safeFileName = CleanFileName(file.FileName);

            var folderPath = await BuildUploadFolderAsync(
                rootPath,
                projectName,
                folderCaseName,
                taskFolderName,
                uploadKind,
                safeFileName);

            Directory.CreateDirectory(folderPath);

            var fullPath = Path.Combine(folderPath, safeFileName);

            if (File.Exists(fullPath))
                throw new InvalidOperationException($"檔案已存在：{safeFileName}，請刪除舊檔或更名後再上傳。");

            await using (var stream = File.Create(fullPath))
            {
                await file.CopyToAsync(stream);
            }

            var entity = new ModuleCaseFile
            {
                Id = Guid.NewGuid(),
                RecordId = recordId,
                CaseId = caseId,
                CaseNo = caseNo,
                TaskId = taskId,
                TaskNo = taskNo,
                FileName = safeFileName,
                FilePath = folderPath,
                ContentType = file.ContentType,
                FileSize = file.Length,
                UploadEmp = string.IsNullOrWhiteSpace(uploadEmp) ? "System" : uploadEmp,
                CreatedAt = DateTime.UtcNow
            };

            _db.ModuleCaseFiles.Add(entity);
            await _db.SaveChangesAsync();

            Console.WriteLine($"[CaseFileService] File uploaded. recordId={recordId}, fileId={entity.Id}, path={fullPath}");
        }

        await RebuildProjectRawDataAsync(recordId);
    }

    public async Task RebuildProjectRawDataAsync(Guid recordId)
    {
        var record = await _db.ModuleRecords
            .Include(x => x.Module)
            .FirstOrDefaultAsync(x => x.Id == recordId);

        if (record == null)
        {
            Console.WriteLine($"[CaseFileService] Rebuild project RAW skipped. record not found. recordId={recordId}");
            return;
        }

        var moduleCode = record.Module?.Code ?? await GetModuleCodeByRecordIdAsync(recordId);
        var projectFolder = await GetProjectFolderAsync(recordId);

        Directory.CreateDirectory(projectFolder);

        var cases = await _db.ModuleRecordCases
            .Where(x => x.RecordId == recordId)
            .OrderBy(x => x.CaseNo)
            .ToListAsync();

        var tasks = await _db.ModuleRecordTasks
            .Where(x => x.Case.RecordId == recordId)
            .OrderBy(x => x.TaskNo)
            .ToListAsync();

        var allFiles = await _db.ModuleCaseFiles
            .Where(x => x.RecordId == recordId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        var projectFilesForMirror = allFiles
            .Where(x => IsUnderFolder(Path.Combine(x.FilePath, x.FileName), projectFolder))
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
            schema = "CSIT.ThreeLevel.ProjectRawData",
            schemaVersion = "1.0",
            generatedAt = DateTime.UtcNow,
            source = new
            {
                system = "CSIT",
                type = "ThreeLevelProject",
                moduleCode,
                entityId = record.Id.ToString()
            },
            project = new
            {
                id = record.Id,
                moduleId = record.ModuleId,
                moduleCode,
                name = record.Name,
                recordNo = record.RecordNo,
                customer = record.Customer,
                owner = record.Owner,
                pmSales = record.PmSales,
                status = record.Status,
                result = record.Result,
                progress = record.Progress,
                startDate = record.StartDate,
                expectedEndDate = record.ExpectedEndDate,
                sampleReadyDate = record.SampleReadyDate,
                note = record.Note,
                applicantNote = record.ApplicantNote,
                team = record.Team,
                npi = record.Npi,
                department = record.Department,
                applicant = record.RequestApplicant,
                location = record.Location,
                requestDepartment = record.RequestDepartment,
                requestApplicant = record.RequestApplicant,
                hardwareVersion = record.HardwareVersion,
                softwareVersion = record.SoftwareVersion,
                hardwareEngineer = record.HardwareEngineer,
                softwareEngineer = record.SoftwareEngineer,
                pjm = record.Pjm,
                subPu = record.SubPu,
                assignOwner = record.AssignOwner,
                mechanicalEngineer = record.MechanicalEngineer,
                firmwareVersion = record.FirmwareVersion,
                wirelessDrive = record.WirelessDrive,
                customerProductName = record.CustomerProductName,
                chipset = record.Chipset,
                sampleMacAddress = record.SampleMacAddress,
                utilityVersion = record.UtilityVersion,
                dspModel = record.DspModel,
                dqaOwner = record.DqaOwner,
                jiraLink = record.JiraLink,
                notifyUsers = record.NotifyUsers,
                createdAt = record.CreatedAt,
                updatedAt = record.UpdatedAt,
                localFolder = projectFolder,
                nasFolder = BuildNasFolderPathByLocalFolder(projectFolder)
            },
            cases = cases.Select(c => new
            {
                id = c.Id,
                recordId = c.RecordId,
                caseNo = c.CaseNo,
                name = c.Name,
                status = c.Status,
                note = c.Note,
                sortOrder = c.SortOrder,
                wifiNo = c.WifiNo,
                btNo = c.BtNo,
                gcfNo = c.GcfNo,
                ptcrbNo = c.PtcrbNo,
                isDraft = c.IsDraft,
                createdAt = c.CreatedAt,
                updatedAt = c.UpdatedAt,
                localFolder = Path.Combine(projectFolder, CleanFolderName(c.Name)),
                nasFolder = BuildNasFolderPathByLocalFolder(Path.Combine(projectFolder, CleanFolderName(c.Name))),
                files = allFiles
                    .Where(f =>
                        f.CaseId == c.Id &&
                        f.TaskId == null &&
                        string.IsNullOrWhiteSpace(f.TaskNo) &&
                        IsUnderFolder(Path.Combine(f.FilePath, f.FileName), projectFolder))
                    .Select(ToRawFileObject)
                    .ToList(),
                tasks = tasks
                    .Where(t => t.CaseId == c.Id)
                    .Select(t => new
                    {
                        id = t.Id,
                        caseId = t.CaseId,
                        taskNo = t.TaskNo,
                        name = t.Name,
                        assignEngineer = t.AssignEngineer,
                        status = t.Status,
                        result = t.Result,
                        progress = t.Progress,
                        startDate = t.StartDate,
                        expectedEndDate = t.ExpectedEndDate,
                        subPu = t.SubPu,
                        modelName = t.ModelName,
                        lab = t.Lab,
                        quoted = t.Quoted,
                        reimburse = t.Reimburse,
                        note = t.Note,
                        createdAt = t.CreatedAt,
                        updatedAt = t.UpdatedAt,
                        localFolder = Path.Combine(projectFolder, CleanFolderName(c.Name), CleanFolderName(t.Name)),
                        nasFolder = BuildNasFolderPathByLocalFolder(Path.Combine(projectFolder, CleanFolderName(c.Name), CleanFolderName(t.Name))),
                        files = allFiles
                            .Where(f =>
                                f.TaskId == t.Id &&
                                IsUnderFolder(Path.Combine(f.FilePath, f.FileName), projectFolder))
                            .Select(ToRawFileObject)
                            .ToList(),
                        testReports = allFiles
                            .Where(f =>
                                f.TaskId == t.Id &&
                                !IsUnderFolder(Path.Combine(f.FilePath, f.FileName), projectFolder))
                            .Select(ToRawFileObject)
                            .ToList()
                    })
                    .ToList(),
                pendingTaskFiles = allFiles
                    .Where(f =>
                        f.CaseId == c.Id &&
                        f.TaskId == null &&
                        !string.IsNullOrWhiteSpace(f.TaskNo))
                    .GroupBy(f => f.TaskNo)
                    .Select(g => new
                    {
                        taskNo = g.Key,
                        files = g
                            .Where(f => IsUnderFolder(Path.Combine(f.FilePath, f.FileName), projectFolder))
                            .Select(ToRawFileObject)
                            .ToList(),
                        testReports = g
                            .Where(f => !IsUnderFolder(Path.Combine(f.FilePath, f.FileName), projectFolder))
                            .Select(ToRawFileObject)
                            .ToList()
                    })
                    .ToList()
            }).ToList()
        };

        var result = await _rawDataExportService.ExportLatestPackageAsync(new RawDataLatestPackageRequest
        {
            SourceSystem = "CSIT",
            SourceType = "ThreeLevelProject",
            ModuleCode = moduleCode,
            EntityId = recordId.ToString(),
            LocalRootFolder = projectFolder,
            Metadata = metadata,
            Files = projectFilesForMirror
        });

        var testReportFiles = allFiles
            .Where(x => !IsUnderFolder(Path.Combine(x.FilePath, x.FileName), projectFolder))
            .ToList();

        MirrorExternalFilesToNas(testReportFiles);

        foreach (var file in allFiles)
        {
            var localFullPath = Path.Combine(file.FilePath, file.FileName);

            file.NasFolderPath = result.Success
                ? Path.GetDirectoryName(BuildNasFilePathByLocalPath(localFullPath))
                : null;

            file.NasFilePath = result.Success
                ? BuildNasFilePathByLocalPath(localFullPath)
                : null;

            file.RawJsonPath = result.Success
                ? Path.Combine(BuildNasFolderPathByLocalFolder(projectFolder), "rawdata.json")
                : null;

            file.IsRawDataExported = result.Success;
            file.RawDataExportedAt = result.Success ? DateTime.UtcNow : null;
            file.RawDataExportError = result.Success ? null : result.ErrorMessage;
        }

        await _db.SaveChangesAsync();

        Console.WriteLine($"[CaseFileService] Rebuild project RAW done. recordId={recordId}, success={result.Success}, folder={result.NasFolderPath}, error={result.ErrorMessage}");
    }

    private object ToRawFileObject(ModuleCaseFile file)
    {
        var localFullPath = Path.Combine(file.FilePath, file.FileName);
        var nasFilePath = BuildNasFilePathByLocalPath(localFullPath);

        return new
        {
            id = file.Id,
            recordId = file.RecordId,
            caseId = file.CaseId,
            caseNo = file.CaseNo,
            taskId = file.TaskId,
            taskNo = file.TaskNo,
            fileName = file.FileName,
            contentType = file.ContentType,
            fileSize = file.FileSize,
            uploadEmp = file.UploadEmp,
            createdAt = file.CreatedAt,
            localFolder = file.FilePath,
            localFilePath = localFullPath,
            nasFolder = Path.GetDirectoryName(nasFilePath),
            nasFilePath
        };
    }

    private void MirrorExternalFilesToNas(List<ModuleCaseFile> files)
    {
        foreach (var file in files)
        {
            var localFullPath = Path.Combine(file.FilePath, file.FileName);

            if (!File.Exists(localFullPath))
            {
                Console.WriteLine($"[CaseFileService] External mirror skipped. local file not found: {localFullPath}");
                continue;
            }

            var nasFilePath = BuildNasFilePathByLocalPath(localFullPath);
            var nasFolder = Path.GetDirectoryName(nasFilePath);

            if (!string.IsNullOrWhiteSpace(nasFolder))
                Directory.CreateDirectory(nasFolder);

            File.Copy(localFullPath, nasFilePath, overwrite: true);

            Console.WriteLine($"[CaseFileService] External file mirrored. local={localFullPath}, nas={nasFilePath}");
        }
    }

    private async Task MoveTaskFolderAfterBindAsync(ModuleRecordTask task, List<ModuleCaseFile> files)
    {
        if (files.Count == 0)
            return;

        var record = task.Case.Record;

        var moduleCode = await GetModuleCodeByRecordIdAsync(record.Id);
        var rootPath = await GetThreeLevelRootPathAsync(moduleCode);

        var oldFolder = Path.Combine(
            rootPath,
            CleanFolderName(record.Name),
            CleanFolderName(task.Case.Name),
            CleanFolderName(task.TaskNo));

        var newFolder = Path.Combine(
            rootPath,
            CleanFolderName(record.Name),
            CleanFolderName(task.Case.Name),
            CleanFolderName(task.Name));

        if (!Directory.Exists(oldFolder))
            return;

        Directory.CreateDirectory(newFolder);

        foreach (var sourceFile in Directory.GetFiles(oldFolder))
        {
            var targetFile = Path.Combine(newFolder, Path.GetFileName(sourceFile));

            if (File.Exists(targetFile))
                File.Delete(targetFile);

            File.Move(sourceFile, targetFile);
        }

        if (!Directory.EnumerateFileSystemEntries(oldFolder).Any())
            Directory.Delete(oldFolder);

        foreach (var file in files.Where(x => IsUnderFolder(Path.Combine(x.FilePath, x.FileName), oldFolder)))
        {
            file.FilePath = newFolder;
        }

        await _db.SaveChangesAsync();

        Console.WriteLine($"[CaseFileService] Task folder moved after bind. {oldFolder} -> {newFolder}");
    }

    private async Task MoveFolderAndUpdateFilesAsync(Guid recordId, string caseNo, string projectName, string oldCaseName, string newCaseName)
    {
        var moduleCode = await GetModuleCodeByRecordIdAsync(recordId);
        var rootPath = await GetThreeLevelRootPathAsync(moduleCode);

        var oldFolder = Path.Combine(rootPath, CleanFolderName(projectName), CleanFolderName(oldCaseName));
        var newFolder = Path.Combine(rootPath, CleanFolderName(projectName), CleanFolderName(newCaseName));

        if (!Directory.Exists(oldFolder))
        {
            Console.WriteLine($"[CaseFileService] Move skipped, old folder not found: {oldFolder}");
            return;
        }

        if (!string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(newFolder))
            {
                foreach (var sourceFile in Directory.GetFiles(oldFolder, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(oldFolder, sourceFile);
                    var targetFile = Path.Combine(newFolder, relative);
                    var targetDir = Path.GetDirectoryName(targetFile);

                    if (!string.IsNullOrWhiteSpace(targetDir))
                        Directory.CreateDirectory(targetDir);

                    if (File.Exists(targetFile))
                        File.Delete(targetFile);

                    File.Move(sourceFile, targetFile);
                }

                if (!Directory.EnumerateFileSystemEntries(oldFolder).Any())
                    Directory.Delete(oldFolder, true);
            }
            else
            {
                Directory.Move(oldFolder, newFolder);
            }
        }

        var files = await _db.ModuleCaseFiles
            .Where(x => x.RecordId == recordId && x.CaseNo == caseNo && x.FilePath.StartsWith(oldFolder))
            .ToListAsync();

        foreach (var file in files)
        {
            file.FilePath = file.FilePath.Replace(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
        }

        await _db.SaveChangesAsync();

        Console.WriteLine($"[CaseFileService] Case folder moved: {oldFolder} -> {newFolder}");
    }

    private async Task<string> BuildUploadFolderAsync(
        string rootPath,
        string projectName,
        string caseName,
        string? taskFolderName,
        string uploadKind,
        string fileName)
    {
        if (IsTestReportUploadKind(uploadKind))
        {
            var result = await TryBuildTestReportFolderAsync(fileName);

            if (result.Success)
            {
                Console.WriteLine($"[CaseFileService] Test report path selected. fileName={fileName}, folder={result.FolderPath}");
                return result.FolderPath;
            }

            var testReportRootPath = await GetTestReportRootPathAsync();

            var fallbackFolder = Path.Combine(
                testReportRootPath,
                CleanFolderName(projectName),
                CleanFolderName(caseName),
                CleanFolderName(string.IsNullOrWhiteSpace(taskFolderName) ? "UnknownTask" : taskFolderName));

            Console.WriteLine($"[CaseFileService] Test report filename parse failed. fallback={fallbackFolder}");
            return fallbackFolder;
        }

        if (!string.IsNullOrWhiteSpace(taskFolderName))
        {
            return Path.Combine(
                rootPath,
                CleanFolderName(projectName),
                CleanFolderName(caseName),
                CleanFolderName(taskFolderName));
        }

        return Path.Combine(
            rootPath,
            CleanFolderName(projectName),
            CleanFolderName(caseName));
    }

    private async Task<(bool Success, string FolderPath)> TryBuildTestReportFolderAsync(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var parts = nameWithoutExtension.Split(
            '-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 5)
            return (false, string.Empty);

        if (!string.Equals(parts[0], "SIT", StringComparison.OrdinalIgnoreCase))
            return (false, string.Empty);

        if (!string.Equals(parts[1], "TR", StringComparison.OrdinalIgnoreCase))
            return (false, string.Empty);

        var category = parts[2];
        var testType = parts[3];
        var model = parts[4];

        var testReportRootPath = await GetTestReportRootPathAsync();

        var folderPath = Path.Combine(
            testReportRootPath,
            CleanFolderName(model),
            CleanFolderName(category),
            CleanFolderName(testType));

        return (true, folderPath);
    }

    private async Task<string> GetProjectFolderAsync(Guid recordId)
    {
        var record = await _db.ModuleRecords
            .Include(x => x.Module)
            .FirstOrDefaultAsync(x => x.Id == recordId);

        if (record == null)
            throw new InvalidOperationException($"找不到主單：recordId={recordId}");

        var moduleCode = record.Module?.Code ?? await GetModuleCodeByRecordIdAsync(recordId);
        var rootPath = await GetThreeLevelRootPathAsync(moduleCode);

        return Path.Combine(rootPath, CleanFolderName(record.Name));
    }

    private async Task<string> GetModuleCodeByRecordIdAsync(Guid recordId)
    {
        var moduleCode = await _db.ModuleRecords
            .Where(x => x.Id == recordId)
            .Select(x => x.Module.Code)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(moduleCode))
            throw new InvalidOperationException($"找不到主單所屬模組：recordId={recordId}");

        return moduleCode.Trim();
    }

    private async Task<string> GetThreeLevelRootPathAsync(string moduleCode)
    {
        var rootPath = await _db.SystemOptions
            .Where(x =>
                x.Category == "ThreeLevelRootPath" &&
                x.IsEnabled &&
                x.Name.ToLower() == moduleCode.ToLower())
            .Select(x => x.Value)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException($"找不到三層公版根目錄設定：Category=ThreeLevelRootPath, Name={moduleCode}");

        return rootPath.Trim();
    }

    private async Task<string> GetTestReportRootPathAsync()
    {
        var rootPath = await _db.SystemOptions
            .Where(x =>
                x.Category == "TestReportRootPath" &&
                x.IsEnabled &&
                x.Name.ToLower() == "default")
            .Select(x => x.Value)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("找不到測試報告根目錄設定：Category=TestReportRootPath, Name=default");

        return rootPath.Trim();
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.BasePath))
            throw new InvalidOperationException("UploadSettings:BasePath 未設定");

        if (string.IsNullOrWhiteSpace(_settings.UploadKind))
            throw new InvalidOperationException("UploadSettings:UploadKind 未設定");
    }

    private string BuildNasFilePathByLocalPath(string localFilePath)
    {
        var fullLocalPath = Path.GetFullPath(localFilePath);
        var root = Path.GetPathRoot(fullLocalPath);

        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"無法解析本機路徑根目錄：{localFilePath}");

        var relativePath = fullLocalPath[root.Length..].TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return Path.Combine(GetRawDataRootPath(), relativePath);
    }

    private string BuildNasFolderPathByLocalFolder(string localFolderPath)
    {
        var fullLocalFolder = Path.GetFullPath(localFolderPath);
        var root = Path.GetPathRoot(fullLocalFolder);

        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"無法解析本機資料夾根目錄：{localFolderPath}");

        var relativePath = fullLocalFolder[root.Length..].TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return Path.Combine(GetRawDataRootPath(), relativePath);
    }

    private string GetRawDataRootPath()
    {
        if (string.IsNullOrWhiteSpace(_rawDataSettings.NasRootPath))
            throw new InvalidOperationException("RawData:NasRootPath 未設定");

        return _rawDataSettings.NasRootPath.Trim();
    }

    private void DeleteNasMirrorFileByLocalPath(string localFilePath)
    {
        var nasFilePath = BuildNasFilePathByLocalPath(localFilePath);

        try
        {
            if (File.Exists(nasFilePath))
            {
                File.Delete(nasFilePath);
                Console.WriteLine($"[CaseFileService] NAS mirror file deleted: {nasFilePath}");
            }

            var folder = Path.GetDirectoryName(nasFilePath);
            TryDeleteEmptyDirectory(folder);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaseFileService] Delete NAS mirror file failed. nas={nasFilePath}, error={ex.Message}");
        }
    }

    private void DeleteNasMirrorFolderByLocalFolder(string localFolder)
    {
        var nasFolder = BuildNasFolderPathByLocalFolder(localFolder);

        try
        {
            if (Directory.Exists(nasFolder))
            {
                Directory.Delete(nasFolder, true);
                Console.WriteLine($"[CaseFileService] NAS mirror folder deleted: {nasFolder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaseFileService] Delete NAS mirror folder failed. folder={nasFolder}, error={ex.Message}");
        }
    }

    private static void DeletePhysicalFile(ModuleCaseFile file)
    {
        var fullPath = Path.Combine(file.FilePath, file.FileName);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            Console.WriteLine($"[CaseFileService] Local file deleted: {fullPath}");
        }
        else
        {
            Console.WriteLine($"[CaseFileService] Local file not found: {fullPath}");
        }

        TryDeleteEmptyDirectory(file.FilePath);
    }

    private static void TryDeleteEmptyDirectory(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                Console.WriteLine($"[CaseFileService] Empty folder deleted: {folder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaseFileService] Delete empty folder skipped. folder={folder}, error={ex.Message}");
        }
    }

    private static bool IsUnderFolder(string path, string folder)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullFolder = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestReportUploadKind(string uploadKind)
    {
        return string.Equals(uploadKind, TaskReportUploadKind, StringComparison.OrdinalIgnoreCase);
    }

    private static ModuleCaseFileDto ToDto(ModuleCaseFile x)
    {
        return new ModuleCaseFileDto
        {
            Id = x.Id,
            RecordId = x.RecordId,
            CaseId = x.CaseId,
            CaseNo = x.CaseNo,
            TaskId = x.TaskId,
            TaskNo = x.TaskNo,
            FileName = x.FileName,
            FilePath = x.FilePath,
            ContentType = x.ContentType,
            FileSize = x.FileSize,
            UploadEmp = x.UploadEmp,
            CreatedAt = x.CreatedAt
        };
    }

    private static string CleanFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string((value ?? string.Empty)
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned.Trim();
    }

    private static string CleanFileName(string value)
    {
        return CleanFolderName(Path.GetFileName(value));
    }
}

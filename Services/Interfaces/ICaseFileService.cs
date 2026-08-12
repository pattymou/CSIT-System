using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface ICaseFileService
{
    Task<List<ModuleCaseFileDto>> GetFilesAsync(Guid caseId);
    Task<List<ModuleCaseFileDto>> GetFilesByCaseNoAsync(Guid recordId, string caseNo);
    Task<List<ModuleCaseFileDto>> GetTaskFilesAsync(Guid taskId);
    Task UploadAsync(Guid caseId, IReadOnlyList<IFormFile> files, string? uploadEmp);
    Task UploadByCaseNoAsync(Guid recordId, string caseNo, IReadOnlyList<IFormFile> files, string? uploadEmp);
    Task UploadTaskReportAsync(Guid taskId, IReadOnlyList<IFormFile> files, string? uploadEmp);
    Task BindFilesToCaseAsync(Guid recordId, string caseNo, Guid caseId, string caseName);
    Task MoveCaseFolderAsync(Guid recordId, string caseNo, string oldCaseName, string newCaseName);
    Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(Guid fileId);
    Task<bool> DeleteAsync(Guid fileId);
    Task UploadTaskReportByTaskNoAsync(Guid caseId, string taskNo, IReadOnlyList<IFormFile> files, string? uploadEmp);

    Task<List<ModuleCaseFileDto>> GetTaskFilesByTaskNoAsync(Guid caseId, string taskNo);

    Task BindFilesToTaskAsync(Guid caseId, string taskNo, Guid taskId);
    Task DeleteFilesByTaskIdAsync(Guid taskId);

    Task DeleteFilesByCaseIdAsync(Guid caseId);

    Task DeleteFilesByRecordIdAsync(Guid recordId);

    Task RebuildProjectRawDataAsync(Guid recordId);

    Task UploadTaskFilesAsync(Guid taskId, IReadOnlyList<IFormFile> files, string? uploadEmp);

    Task UploadTaskFilesByTaskNoAsync(Guid caseId, string taskNo, IReadOnlyList<IFormFile> files, string? uploadEmp);
}

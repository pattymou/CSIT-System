using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IRawDataExportService
{
    Task<RawDataExportResult> ExportAsync(RawDataExportRequest request);

    Task<RawDataExportResult> ExportLatestPackageAsync(RawDataLatestPackageRequest request);

    Task<bool> DeleteLatestPackageAsync(string sourceType, string moduleCode, string entityId);
}
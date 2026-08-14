using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services;

internal static class TestCatalogRules
{
    public static void ValidateTemplateUpdate(TestPlanTemplate entity, TestPlanTemplateUpsertRequest request)
    {
        switch (entity.Status)
        {
            case TemplateStatus.Draft:
                if (request.Status is not (TemplateStatus.Draft or TemplateStatus.Published))
                    throw new InvalidOperationException("A draft test plan template can only remain Draft or become Published.");
                break;
            case TemplateStatus.Published:
                if (request.Status is not (TemplateStatus.Published or TemplateStatus.Retired))
                    throw new InvalidOperationException("A published test plan template can only remain Published or become Retired.");
                EnsureTestPlanContentUnchanged(entity, request);
                break;
            case TemplateStatus.Retired:
                if (request.Status != TemplateStatus.Retired)
                    throw new InvalidOperationException("A retired test plan template cannot change status.");
                EnsureTestPlanContentUnchanged(entity, request);
                break;
            default:
                throw new InvalidOperationException($"Unsupported test plan template status: {entity.Status}.");
        }
    }

    public static void ValidateTemplateUpdate(ReportTemplate entity, ReportTemplateUpsertRequest request)
    {
        switch (entity.Status)
        {
            case TemplateStatus.Draft:
                if (request.Status is not (TemplateStatus.Draft or TemplateStatus.Published))
                    throw new InvalidOperationException("A draft report template can only remain Draft or become Published.");
                break;
            case TemplateStatus.Published:
                if (request.Status is not (TemplateStatus.Published or TemplateStatus.Retired))
                    throw new InvalidOperationException("A published report template can only remain Published or become Retired.");
                EnsureReportContentUnchanged(entity, request);
                break;
            case TemplateStatus.Retired:
                if (request.Status != TemplateStatus.Retired)
                    throw new InvalidOperationException("A retired report template cannot change status.");
                EnsureReportContentUnchanged(entity, request);
                break;
            default:
                throw new InvalidOperationException($"Unsupported report template status: {entity.Status}.");
        }
    }

    public static void EnsureResourceCanBeDeactivated(bool hasActiveProfile, string resourceName)
    {
        if (hasActiveProfile)
            throw new InvalidOperationException(
                $"{resourceName} is referenced by an active test execution profile. Disable or update the active profile first.");
    }

    public static void ValidateDefaultProfileShape(TestExecutionProfileUpsertRequest request)
    {
        if (request.Status == TestExecutionProfileStatus.Disabled && request.IsDefault)
            throw new InvalidOperationException("A disabled test execution profile cannot be the default profile.");
    }

    public static void EnsureDefaultProfileAvailable(bool duplicateExists)
    {
        if (duplicateExists)
            throw new InvalidOperationException(
                "This test capability already has an active default profile. Disable or update that profile first.");
    }

    private static void EnsureTestPlanContentUnchanged(TestPlanTemplate entity, TestPlanTemplateUpsertRequest request)
    {
        if (!Same(entity.Code, request.Code, normalizeCode: true) ||
            !Same(entity.Name, request.Name) ||
            !Same(entity.Version, request.Version) ||
            !Same(entity.SourceFilePath, request.SourceFilePath) ||
            !Same(entity.StructuredDefinition, request.StructuredDefinition) ||
            !Same(entity.CreatedBy, request.CreatedBy))
        {
            throw new InvalidOperationException(
                "Published or retired test plan template content is immutable. Create a new template version instead.");
        }
    }

    private static void EnsureReportContentUnchanged(ReportTemplate entity, ReportTemplateUpsertRequest request)
    {
        if (!Same(entity.Code, request.Code, normalizeCode: true) ||
            !Same(entity.Name, request.Name) ||
            !Same(entity.Version, request.Version) ||
            entity.TemplateType != request.TemplateType ||
            !Same(entity.TemplateFilePath, request.TemplateFilePath) ||
            !Same(entity.ResultSchema, request.ResultSchema))
        {
            throw new InvalidOperationException(
                "Published or retired report template content is immutable. Create a new template version instead.");
        }
    }

    private static bool Same(string? stored, string? requested, bool normalizeCode = false)
    {
        var left = Clean(stored);
        var right = Clean(requested);
        if (normalizeCode)
        {
            left = left?.ToUpperInvariant();
            right = right?.ToUpperInvariant();
        }
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;

namespace SIT.DepartmentSystem.Web.Services.ValidationRequests;

public interface IValidationRequestApplicationOptionSource
{
    Task<ValidationRequestApplicationOptions> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record ValidationRequestApplicationOptions(
    IReadOnlyList<ValidationOptionDto> Customers,
    IReadOnlyList<ValidationOptionDto> PuCodes);

public sealed class ValidationRequestApplicationOptionSource(AppDbContext db)
    : IValidationRequestApplicationOptionSource
{
    private const string TemplateCode = "VALIDATION_TEST_REQUEST";
    private const string CustomerFieldCode = "CUSTOMER";
    private const string PuCodeFieldCode = "PU_CODE";
    private const string SystemOptionPrefix = "SystemOption:";

    public async Task<ValidationRequestApplicationOptions> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var versionId = await db.ValidationFormTemplateVersions.AsNoTracking()
            .Where(x => x.Template.Code == TemplateCode)
            .OrderByDescending(x => x.Status == ValidationTemplateStatuses.Published)
            .ThenByDescending(x => x.VersionNumber)
            .Select(x => x.Id)
            .FirstAsync(cancellationToken);

        var fields = await db.ValidationFieldDefinitions.AsNoTracking()
            .Where(x => x.SectionDefinition.TemplateVersionId == versionId
                && x.IsEnabled
                && (x.Code == CustomerFieldCode || x.Code == PuCodeFieldCode))
            .Include(x => x.Options)
            .ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new ValidationRequestApplicationOptions(
            await ResolveAsync(fields, CustomerFieldCode, cancellationToken),
            await ResolveAsync(fields, PuCodeFieldCode, cancellationToken));
    }

    private async Task<IReadOnlyList<ValidationOptionDto>> ResolveAsync(
        IReadOnlyDictionary<string, ValidationFieldDefinition> fields,
        string fieldCode,
        CancellationToken cancellationToken)
    {
        if (!fields.TryGetValue(fieldCode, out var field))
            return [];

        if (field.DataSource?.StartsWith(SystemOptionPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            var category = field.DataSource[SystemOptionPrefix.Length..];
            var options = await db.SystemOptions.AsNoTracking()
                .Where(x => x.Category == category && x.IsEnabled)
                .OrderBy(x => x.Sort).ThenBy(x => x.Name).ThenBy(x => x.Value)
                .ToListAsync(cancellationToken);
            return options.Select(x => new ValidationOptionDto(
                x.Id, x.Value, string.IsNullOrWhiteSpace(x.Name) ? x.Value : x.Name, true, x.Sort)).ToList();
        }

        return field.Options
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new ValidationOptionDto(
                x.Id, x.ValueCode, x.DisplayLabel, x.IsEnabled, x.DisplayOrder))
            .ToList();
    }
}

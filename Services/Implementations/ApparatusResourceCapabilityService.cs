using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class ApparatusResourceCapabilityService : IApparatusResourceCapabilityService
{
    private readonly AppDbContext _db;

    public ApparatusResourceCapabilityService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ApparatusResourceCapabilityDto>> GetByApparatusAsync(
        string apparatusId,
        CancellationToken cancellationToken = default)
    {
        var id = Require(apparatusId, nameof(apparatusId));
        await EnsureEquipmentExistsAsync(id, cancellationToken);
        return await _db.ApparatusResourceCapabilities.AsNoTracking()
            .Where(x => x.ApparatusId == id)
            .OrderBy(x => x.ResourceType)
            .ThenBy(x => x.CapabilityTag)
            .Select(x => Map(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApparatusResourceCapabilityDto>> ReplaceAsync(
        string apparatusId,
        IReadOnlyCollection<ApparatusResourceCapabilityInput> mappings,
        CancellationToken cancellationToken = default)
    {
        var id = Require(apparatusId, nameof(apparatusId));
        ArgumentNullException.ThrowIfNull(mappings);
        await EnsureEquipmentExistsAsync(id, cancellationToken);

        var catalogValues = await _db.EquipmentGroupRequirements.AsNoTracking()
            .Select(x => new { x.ResourceType, x.CapabilityTag })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (catalogValues.Count == 0 && mappings.Count != 0)
            throw new InvalidOperationException("No EquipmentGroupRequirement catalog values exist. Create catalog requirements before assigning resource capabilities.");

        var canonical = new List<(string ResourceType, string? CapabilityTag)>();
        foreach (var mapping in mappings)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            var resourceType = Require(mapping.ResourceType, nameof(mapping.ResourceType));
            var capabilityTag = Clean(mapping.CapabilityTag);
            var match = catalogValues.FirstOrDefault(x =>
                string.Equals(x.ResourceType, resourceType, StringComparison.Ordinal)
                && (capabilityTag is null || string.Equals(x.CapabilityTag, capabilityTag, StringComparison.Ordinal)));
            if (match is null)
            {
                var value = capabilityTag is null ? resourceType : $"{resourceType} / {capabilityTag}";
                throw new InvalidOperationException($"Resource capability is not defined by any EquipmentGroupRequirement: {value}.");
            }

            canonical.Add((match.ResourceType, capabilityTag is null ? null : match.CapabilityTag));
        }

        var duplicate = canonical
            .GroupBy(x => new { x.ResourceType, x.CapabilityTag })
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException("Duplicate resource capability mappings are not allowed.");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _db.ApparatusResourceCapabilities
                .Where(x => x.ApparatusId == id)
                .ExecuteDeleteAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var entities = canonical.Select(x => new ApparatusResourceCapability
            {
                Id = Guid.NewGuid(),
                ApparatusId = id,
                ResourceType = x.ResourceType,
                CapabilityTag = x.CapabilityTag,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();
            _db.ApparatusResourceCapabilities.AddRange(entities);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return entities.OrderBy(x => x.ResourceType).ThenBy(x => x.CapabilityTag).Select(Map).ToList();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<ResourceMatchingCatalogValueDto>> GetCatalogValuesAsync(
        CancellationToken cancellationToken = default) =>
        await _db.EquipmentGroupRequirements.AsNoTracking()
            .Select(x => new ResourceMatchingCatalogValueDto
            {
                ResourceType = x.ResourceType,
                CapabilityTag = x.CapabilityTag
            })
            .Distinct()
            .OrderBy(x => x.ResourceType)
            .ThenBy(x => x.CapabilityTag)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> GetMatchingApparatusIdsAsync(
        string resourceType,
        string? capabilityTag,
        IReadOnlyCollection<string>? apparatusIds = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalResourceType = Require(resourceType, nameof(resourceType));
        var canonicalCapabilityTag = Clean(capabilityTag);
        var query = _db.ApparatusResourceCapabilities.AsNoTracking()
            .Where(x => x.ResourceType == canonicalResourceType);
        if (canonicalCapabilityTag is not null)
            query = query.Where(x => x.CapabilityTag == canonicalCapabilityTag);
        if (apparatusIds is not null)
            query = query.Where(x => apparatusIds.Contains(x.ApparatusId));
        return (await query.Select(x => x.ApparatusId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task EnsureEquipmentExistsAsync(string apparatusId, CancellationToken cancellationToken)
    {
        var apparatus = await _db.Apparatuses.AsNoTracking()
            .Where(x => x.Id == apparatusId)
            .Select(x => new { x.ModuleCode })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Apparatus {apparatusId} was not found.");
        if (!string.Equals(apparatus.ModuleCode, ApparatusReservationRules.EquipmentModuleCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resource capabilities can only be assigned to equipment apparatus.");
    }

    private static ApparatusResourceCapabilityDto Map(ApparatusResourceCapability x) => new()
    {
        Id = x.Id,
        ApparatusId = x.ApparatusId,
        ResourceType = x.ResourceType,
        CapabilityTag = x.CapabilityTag,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt
    };

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

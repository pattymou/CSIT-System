using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class ApparatusAvailabilityService : IApparatusAvailabilityService
{
    private static readonly ReservationStatus[] OccupyingStatuses =
    [
        ReservationStatus.Pending,
        ReservationStatus.Approved,
        ReservationStatus.Borrowed
    ];

    private readonly AppDbContext _db;

    public ApparatusAvailabilityService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlySet<string>> GetAvailableApparatusIdsAsync(
        IReadOnlyCollection<string> apparatusIds,
        DateTime startTime,
        DateTime endTime,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default)
    {
        if (apparatusIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var ids = apparatusIds.Distinct(StringComparer.Ordinal).ToArray();
        var available = await _db.Apparatuses.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Where(x => x.ModuleCode.ToLower() == ApparatusReservationRules.EquipmentModuleCode)
            .Where(x => x.ReservationStatus == ApparatusReservationRules.BookableStatus)
            .Where(x => !_db.ReservationItems.Any(item =>
                item.ApparatusId == x.Id
                && OccupyingStatuses.Contains(item.Reservation.Status)
                && item.Reservation.StartTime < endTime
                && item.Reservation.EndTime > startTime
                && (!excludedReservationId.HasValue || item.ReservationId != excludedReservationId.Value)))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        return available.ToHashSet(StringComparer.Ordinal);
    }

    public async Task EnsureBookableAsync(
        IReadOnlyCollection<string> apparatusIds,
        CancellationToken cancellationToken = default)
    {
        var ids = apparatusIds.Distinct(StringComparer.Ordinal).ToArray();
        var apparatuses = await _db.Apparatuses.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.ModuleCode, x.ReservationStatus })
            .ToListAsync(cancellationToken);
        var unavailable = apparatuses
            .Where(x => !ApparatusReservationRules.IsBookable(x.ModuleCode, x.ReservationStatus))
            .ToList();
        if (unavailable.Count != 0)
            throw new InvalidOperationException(
                $"Apparatus is not bookable: {string.Join(", ", unavailable.Select(x => $"{x.Id} ({x.ReservationStatus})"))}.");
    }

    public async Task EnsureNoOverlapAsync(
        IReadOnlyCollection<string> apparatusIds,
        DateTime startTime,
        DateTime endTime,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default)
    {
        var ids = apparatusIds.Distinct(StringComparer.Ordinal).ToArray();
        var conflicts = await _db.ReservationItems.AsNoTracking()
            .Where(x => ids.Contains(x.ApparatusId))
            .Where(x => OccupyingStatuses.Contains(x.Reservation.Status))
            .Where(x => x.Reservation.StartTime < endTime && x.Reservation.EndTime > startTime)
            .Where(x => !excludedReservationId.HasValue || x.ReservationId != excludedReservationId.Value)
            .Select(x => new { x.ApparatusId, x.Reservation.ReservationNo })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (conflicts.Count != 0)
            throw new InvalidOperationException(
                $"The requested time overlaps an existing reservation: {string.Join(", ", conflicts.Select(x => $"{x.ApparatusId} / {x.ReservationNo}"))}.");
    }
}

using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class EnvironmentAvailabilityService(AppDbContext db) : IEnvironmentAvailabilityService
{
    public async Task<EnvironmentAvailabilityDto> GetAvailabilityAsync(
        Guid equipmentGroupId,
        DateTime startTime,
        DateTime endTime,
        Guid? excludeReservationId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeRange(startTime, endTime);

        var group = await db.EquipmentGroups.AsNoTracking()
            .Where(x => x.Id == equipmentGroupId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                MembershipDeviceCount = x.Devices.Count
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Equipment group {equipmentGroupId} was not found.");

        // Presence and readiness are deliberately absent: every current membership,
        // including a temporarily moved-out device, participates in time availability.
        var memberApparatusIds = db.EquipmentGroupDevices.AsNoTracking()
            .Where(x => x.EquipmentGroupId == equipmentGroupId)
            .Select(x => x.ApparatusId);
        var blockingStatuses = ReservationOccupancyRules.BlockingStatuses;

        // Query from the reservation root so a reservation occupying several member
        // devices is returned once with all related devices attached.
        var conflicts = await db.Reservations.AsNoTracking()
            .Where(x => blockingStatuses.Contains(x.Status))
            .Where(x => x.StartTime < endTime && x.EndTime > startTime)
            .Where(x => !excludeReservationId.HasValue || x.Id != excludeReservationId.Value)
            .Where(x => x.Items.Any(item => memberApparatusIds.Contains(item.ApparatusId)))
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.ReservationNo)
            .Select(x => new EnvironmentAvailabilityConflictDto
            {
                ReservationId = x.Id,
                ReservationNo = x.ReservationNo,
                ReservationStatus = x.Status,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                ReservationMode = x.EquipmentGroupId.HasValue || x.TestExecutionProfileId.HasValue
                    ? ReservationMode.Environment
                    : ReservationMode.Direct,
                RelatedDevices = x.Items
                    .Where(item => memberApparatusIds.Contains(item.ApparatusId))
                    .OrderBy(item => item.ApparatusId)
                    .Select(item => new EnvironmentAvailabilityRelatedDeviceDto
                    {
                        ApparatusId = item.ApparatusId,
                        ApparatusName = item.ApparatusName,
                        ProductsId = item.ProductsId
                    }).ToList()
            })
            .ToListAsync(cancellationToken);

        return new EnvironmentAvailabilityDto
        {
            EquipmentGroupId = group.Id,
            GroupCode = group.Code,
            GroupName = group.Name,
            StartTime = startTime,
            EndTime = endTime,
            IsTimeAvailable = conflicts.Count == 0,
            MembershipDeviceCount = group.MembershipDeviceCount,
            BlockingReservationCount = conflicts.Count,
            BlockingDeviceCount = conflicts
                .SelectMany(x => x.RelatedDevices)
                .Select(x => x.ApparatusId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            Conflicts = conflicts
        };
    }

    private static void ValidateTimeRange(DateTime startTime, DateTime endTime)
    {
        if (startTime.Kind != DateTimeKind.Utc || endTime.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("StartTime and EndTime must be UTC values.");
        if (startTime >= endTime)
            throw new InvalidOperationException("StartTime must be earlier than EndTime.");
    }
}

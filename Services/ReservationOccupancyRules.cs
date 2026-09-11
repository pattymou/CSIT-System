using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services;

internal static class ReservationOccupancyRules
{
    public static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.Pending,
        ReservationStatus.Approved,
        ReservationStatus.Borrowed
    ];

    public static bool Overlaps(
        DateTime existingStart,
        DateTime existingEnd,
        DateTime requestedStart,
        DateTime requestedEnd) =>
        existingStart < requestedEnd && existingEnd > requestedStart;
}

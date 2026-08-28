using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class ReservationPolicyService(AppDbContext db) : IReservationPolicyService
{
    public const string Category = "Reservation";
    public const string MaxBorrowDaysKey = "reservation.max_borrow_days";
    public const string DepartmentMaxConcurrentEquipmentKey = "reservation.department_max_concurrent_equipment";
    public const string MaxExtensionDaysKey = "reservation.max_extension_days";

    private static readonly ReservationStatus[] OccupyingStatuses =
        [ReservationStatus.Pending, ReservationStatus.Approved, ReservationStatus.Borrowed];

    public async Task<ReservationPolicySettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var options = await db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == Category && x.IsEnabled)
            .Where(x => x.Name == MaxBorrowDaysKey
                || x.Name == DepartmentMaxConcurrentEquipmentKey
                || x.Name == MaxExtensionDaysKey)
            .ToDictionaryAsync(x => x.Name, x => x.Value, cancellationToken);

        var maxBorrowDays = Positive(options.GetValueOrDefault(MaxBorrowDaysKey), 7);
        var quota = NonNegative(options.GetValueOrDefault(DepartmentMaxConcurrentEquipmentKey));
        var maxExtensionDays = Positive(options.GetValueOrDefault(MaxExtensionDaysKey), maxBorrowDays);
        return new(maxBorrowDays, quota == 0 ? null : quota, maxExtensionDays);
    }

    public async Task ValidateInitialDurationAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startTime, endTime);
        var settings = await GetSettingsAsync(cancellationToken);
        if (endTime - startTime > TimeSpan.FromDays(settings.MaxBorrowDays))
            throw new InvalidOperationException($"單次設備借用最長為 {settings.MaxBorrowDays} 天，請縮短借用期間或於後續申請續借。");
    }

    public async Task ValidateExtensionDurationAsync(DateTime currentEndTime, DateTime requestedEndTime, CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(currentEndTime, requestedEndTime);
        var settings = await GetSettingsAsync(cancellationToken);
        if (requestedEndTime - currentEndTime > TimeSpan.FromDays(settings.MaxExtensionDays))
            throw new InvalidOperationException($"每次續借最多可延長 {settings.MaxExtensionDays} 天。");
    }

    public async Task<DepartmentQuotaResult> CheckDepartmentQuotaAsync(
        string department,
        DateTime startTime,
        DateTime endTime,
        int requestedEquipmentCount,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startTime, endTime);
        if (string.IsNullOrWhiteSpace(department)) throw new InvalidOperationException("ApplicantDepartment is required.");
        if (requestedEquipmentCount <= 0) throw new InvalidOperationException("At least one apparatus is required.");
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.DepartmentMaxConcurrentEquipment.HasValue)
            return new(true, 0, requestedEquipmentCount, requestedEquipmentCount, null, null);

        var reservations = await db.Reservations.AsNoTracking()
            .Where(x => x.ApplicantDepartment == department.Trim()
                && OccupyingStatuses.Contains(x.Status)
                && x.StartTime < endTime && startTime < x.EndTime
                && (!excludedReservationId.HasValue || x.Id != excludedReservationId.Value))
            .Select(x => new { x.StartTime, x.EndTime, Count = x.Items.Count })
            .ToListAsync(cancellationToken);

        var events = reservations
            .SelectMany(x => new[] { new QuotaEvent(x.StartTime, x.Count), new QuotaEvent(x.EndTime, -x.Count) })
            .Concat([new QuotaEvent(startTime, requestedEquipmentCount), new QuotaEvent(endTime, -requestedEquipmentCount)])
            .OrderBy(x => x.At)
            .ThenBy(x => x.Delta) // half-open: negative End events precede positive Start events
            .ToList();
        var existingEvents = reservations
            .SelectMany(x => new[] { new QuotaEvent(x.StartTime, x.Count), new QuotaEvent(x.EndTime, -x.Count) })
            .OrderBy(x => x.At).ThenBy(x => x.Delta).ToList();

        var existingMaximum = MaximumWithin(existingEvents, startTime, endTime);
        var projectedMaximum = MaximumWithin(events, startTime, endTime);
        var limit = settings.DepartmentMaxConcurrentEquipment.Value;
        var allowed = projectedMaximum <= limit;
        var error = allowed ? null
            : $"目前此部門於該時段最多已使用 {existingMaximum} 台設備，本次新增 {requestedEquipmentCount} 台後將達 {projectedMaximum} 台，超過部門上限 {limit} 台。";
        return new(allowed, existingMaximum, requestedEquipmentCount, projectedMaximum, limit, error);
    }

    public async Task EnsureDepartmentQuotaAsync(
        string department,
        DateTime startTime,
        DateTime endTime,
        int requestedEquipmentCount,
        Guid? excludedReservationId = null,
        bool acquireTransactionLock = false,
        CancellationToken cancellationToken = default)
    {
        if (acquireTransactionLock)
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Department quota lock requires an active transaction.");
            var lockKey = "reservation-department:" + department.Trim();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))",
                cancellationToken);
        }
        var result = await CheckDepartmentQuotaAsync(
            department, startTime, endTime, requestedEquipmentCount, excludedReservationId, cancellationToken);
        if (!result.IsAllowed) throw new InvalidOperationException(result.Error);
    }

    private static int MaximumWithin(IReadOnlyList<QuotaEvent> events, DateTime startTime, DateTime endTime)
    {
        var current = 0;
        var maximum = 0;
        foreach (var group in events.GroupBy(x => x.At).OrderBy(x => x.Key))
        {
            if (group.Key > startTime && group.Key <= endTime)
                maximum = Math.Max(maximum, current);
            current += group.Sum(x => x.Delta);
            if (group.Key >= startTime && group.Key < endTime)
                maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static void ValidateUtcRange(DateTime startTime, DateTime endTime)
    {
        if (startTime.Kind != DateTimeKind.Utc || endTime.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("StartTime and EndTime must be UTC values.");
        if (startTime >= endTime) throw new InvalidOperationException("StartTime must be earlier than EndTime.");
    }

    private static int Positive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int NonNegative(string? value) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : 0;

    private sealed record QuotaEvent(DateTime At, int Delta);
}

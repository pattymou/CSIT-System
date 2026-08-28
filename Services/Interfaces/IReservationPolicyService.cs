namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public sealed record ReservationPolicySettings(
    int MaxBorrowDays,
    int? DepartmentMaxConcurrentEquipment,
    int MaxExtensionDays);

public sealed record DepartmentQuotaResult(
    bool IsAllowed,
    int ExistingMaximum,
    int RequestedEquipmentCount,
    int ProjectedMaximum,
    int? Limit,
    string? Error);

public interface IReservationPolicyService
{
    Task<ReservationPolicySettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task ValidateInitialDurationAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task ValidateExtensionDurationAsync(DateTime currentEndTime, DateTime requestedEndTime, CancellationToken cancellationToken = default);
    Task<DepartmentQuotaResult> CheckDepartmentQuotaAsync(
        string department,
        DateTime startTime,
        DateTime endTime,
        int requestedEquipmentCount,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default);
    Task EnsureDepartmentQuotaAsync(
        string department,
        DateTime startTime,
        DateTime endTime,
        int requestedEquipmentCount,
        Guid? excludedReservationId = null,
        bool acquireTransactionLock = false,
        CancellationToken cancellationToken = default);
}

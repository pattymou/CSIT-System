namespace SIT.DepartmentSystem.Web.Services.Interfaces;

public interface IApparatusAvailabilityService
{
    Task<IReadOnlySet<string>> GetAvailableApparatusIdsAsync(
        IReadOnlyCollection<string> apparatusIds,
        DateTime startTime,
        DateTime endTime,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default);

    Task EnsureBookableAsync(
        IReadOnlyCollection<string> apparatusIds,
        CancellationToken cancellationToken = default);

    Task EnsureNoOverlapAsync(
        IReadOnlyCollection<string> apparatusIds,
        DateTime startTime,
        DateTime endTime,
        Guid? excludedReservationId = null,
        CancellationToken cancellationToken = default);
}

namespace SIT.DepartmentSystem.Web.Models.Api;

public static class KmQueueFilters
{
    public const string Unfinished = "unfinished";
    public const string Waiting = "waiting";
    public const string Processing = "processing";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public static bool IsValid(string? value) => value is Unfinished or Waiting or Processing or Failed or Completed;
}

public sealed record KmQueueStatsDto(int Unfinished, int Waiting, int Processing, int Failed, int Completed);

public sealed record KmQueueItemDto(
    long QueueNumber, Guid Id, string FileName, string SourceModule,
    string UploadedBy, DateTime? UploadedAt, DateTime QueuedAt,
    string Status, string StatusDisplay, int RetryCount, DateTime? NextRetryAt,
    string RetryState, string? ErrorCode, string? Detail,
    string? DeletionCategory = null, int? Priority = null, bool WaitsForKm = true);

public sealed record KmQueuePageDto(
    IReadOnlyList<KmQueueItemDto> Items, int Page, int PageSize, int Total,
    KmQueueStatsDto Stats, bool Enabled, string? Notice);

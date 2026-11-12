using System.Security.Principal;
using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

/// <summary>
/// One-shot DEV-only dispatch entry point. The watcher supplies the exact outbox and
/// event IDs from an Administrator-created, short-lived request; no HTTP endpoint or
/// arbitrary command execution is exposed.
/// </summary>
public static class DevKmOutboxDispatch
{
    private const string LocalServiceSid = "S-1-5-19";

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output,
        Guid outboxId, Guid eventId, CancellationToken cancellationToken)
    {
        var result = new Result
        {
            IdentitySid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown",
            OutboxId = outboxId,
            EventId = eventId
        };
        if (!string.Equals(result.IdentitySid, LocalServiceSid, StringComparison.Ordinal))
        {
            result.ErrorCode = "NOT_LOCAL_SERVICE";
            return await WriteAsync(output, result, 1);
        }
        if (outboxId == Guid.Empty || eventId == Guid.Empty)
        {
            result.ErrorCode = "INVALID_ID";
            return await WriteAsync(output, result, 1);
        }
        try
        {
            using var scope = services.CreateScope();
            var worker = scope.ServiceProvider.GetRequiredService<KmSyncWorker>();
            var dispatched = await worker.ProcessSingleVerificationAttachmentAsync(
                outboxId, eventId, cancellationToken);
            result.Outcome = dispatched.Status is Entities.KmIntegrationStatus.Sent
                ? "ACCEPTED" : "FAILED";
            result.Status = dispatched.Status.ToString();
            result.KmJobIdPresent = !string.IsNullOrWhiteSpace(dispatched.KmJobId);
            result.ErrorCode = dispatched.ErrorCode;
            return await WriteAsync(output, result, result.Outcome == "ACCEPTED" ? 0 : 1);
        }
        catch (KmSingleDispatchException exception)
        {
            result.ErrorCode = "DISPATCH_REJECTED";
            result.FailureStage = exception.Stage;
            return await WriteAsync(output, result, 1);
        }
        catch (Exception exception)
        {
            result.ErrorCode = exception.GetType().Name;
            result.FailureStage = exception.TargetSite is null
                ? "Unknown"
                : exception.TargetSite.DeclaringType?.Name + "." + exception.TargetSite.Name;
            return await WriteAsync(output, result, 1);
        }
    }

    private static async Task<int> WriteAsync(TextWriter output, Result result, int exitCode)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(result));
        await output.FlushAsync();
        return exitCode;
    }

    private sealed class Result
    {
        public string IdentitySid { get; init; } = "unknown";
        public Guid OutboxId { get; init; }
        public Guid EventId { get; init; }
        public string Outcome { get; set; } = "FAIL";
        public string? Status { get; set; }
        public bool KmJobIdPresent { get; set; }
        public string? ErrorCode { get; set; }
        public string? FailureStage { get; set; }
    }
}

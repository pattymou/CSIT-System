using System.Security.Principal;
using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class DevEquipmentFileDeletionRecovery
{
    private const string LocalServiceSid = "S-1-5-19";

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output,
        Guid jobId, string actor, string reason, CancellationToken cancellationToken)
    {
        var result = new Result
        {
            IdentitySid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown",
            JobId = jobId
        };
        if (!string.Equals(result.IdentitySid, LocalServiceSid, StringComparison.Ordinal))
        {
            result.ErrorCode = "NOT_LOCAL_SERVICE";
            return await WriteAsync(output, result, 1);
        }
        try
        {
            using var scope = services.CreateScope();
            var recovery = scope.ServiceProvider
                .GetRequiredService<EquipmentFileDeletionManualRecoveryService>();
            var recovered = await recovery.RecoverAndDispatchAsync(
                jobId, actor, reason, cancellationToken);
            result.Outcome = recovered.KmHttpAccepted ? "ACCEPTED" : "FAILED";
            result.HttpStatus = recovered.KmHttpAccepted ? 202 : null;
            result.Status = recovered.Status;
            result.KmDeletionJobId = recovered.KmDeletionJobId;
            result.ErrorCode = recovered.ErrorCode;
            return await WriteAsync(output, result, recovered.KmHttpAccepted ? 0 : 1);
        }
        catch (EquipmentFileDeletionManualRecoveryException exception)
        {
            result.ErrorCode = "RECOVERY_REJECTED";
            result.FailureStage = exception.Stage;
            return await WriteAsync(output, result, 1);
        }
        catch (Exception exception)
        {
            result.ErrorCode = exception.GetType().Name;
            result.FailureStage = exception.TargetSite?.DeclaringType?.Name + "." +
                exception.TargetSite?.Name;
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
        public Guid JobId { get; init; }
        public string Outcome { get; set; } = "FAIL";
        public int? HttpStatus { get; set; }
        public string? Status { get; set; }
        public string? KmDeletionJobId { get; set; }
        public string? ErrorCode { get; set; }
        public string? FailureStage { get; set; }
    }
}

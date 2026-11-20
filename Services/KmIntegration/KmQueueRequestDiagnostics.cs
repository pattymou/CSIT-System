using Npgsql;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmQueueRequestDiagnostics(
    RequestDelegate next, ILogger<KmQueueRequestDiagnostics> logger)
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string CorrelationItem = "KmQueueCorrelationId";
    public const string SessionFailureItem = "KmQueueSessionFailure";
    public const string ErrorTypeItem = "KmQueueErrorType";
    public const string SqlStateItem = "KmQueueSqlState";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsQueuePath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var supplied = context.Request.Headers[CorrelationHeader].ToString();
        var correlationId = Guid.TryParseExact(supplied, "N", out var parsed)
            ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
        context.Items[CorrelationItem] = correlationId;
        context.Response.Headers[CorrelationHeader] = correlationId;
        var startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            Log(context, startedAtUtc, correlationId, 500, "ServerException",
                $"{ex.GetType().Name}/{ex.GetBaseException().GetType().Name}",
                (ex.GetBaseException() as PostgresException)?.SqlState);
            throw;
        }

        var status = context.Response.StatusCode;
        var category = status switch
        {
            StatusCodes.Status401Unauthorized =>
                context.Items[SessionFailureItem] is string failure ? failure : "AuthenticationRequired",
            StatusCodes.Status403Forbidden => "Forbidden",
            >= 500 => "ServerFailure",
            >= 300 and < 400 => "Redirect",
            >= 400 => "HttpFailure",
            _ => "Success"
        };
        Log(context, startedAtUtc, correlationId, status, category,
            context.Items[ErrorTypeItem] as string, context.Items[SqlStateItem] as string);
    }

    private void Log(HttpContext context, DateTimeOffset startedAtUtc,
        string correlationId, int status, string category, string? errorType, string? sqlState)
    {
        logger.Log(status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information,
            "KM queue read Path={Path} HttpStatus={HttpStatus} StartedAtUtc={StartedAtUtc} CorrelationId={CorrelationId} RequestId={RequestId} ErrorCategory={ErrorCategory} ErrorType={ErrorType} SqlState={SqlState}",
            context.Request.Path.Value, status, startedAtUtc, correlationId,
            context.TraceIdentifier, category, errorType, sqlState);
    }

    private static bool IsQueuePath(PathString path) =>
        path.Equals("/api/administration/km-queue/ingestion", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/administration/km-queue/deletion", StringComparison.OrdinalIgnoreCase);
}

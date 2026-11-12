using System.Security.Principal;
using System.Text.Json;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

/// <summary>
/// One-shot, non-HTTP diagnostic entry point used only by the protected DEV watcher.
/// It deliberately delegates token acquisition to the production provider and never
/// writes the token or any credential material to its result.
/// </summary>
public static class DevKmM2MTokenVerification
{
    private const string LocalServiceSid = "S-1-5-19";

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = new Result
        {
            IdentitySid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown",
            SignatureValidation = "NOT_PERFORMED"
        };

        if (!string.Equals(result.IdentitySid, LocalServiceSid, StringComparison.Ordinal))
        {
            result.Outcome = "FAIL";
            result.FailureStage = "Identity";
            result.ErrorCode = "NOT_LOCAL_SERVICE";
            return await WriteResultAsync(output, result, 1);
        }

        try
        {
            var provider = services.GetRequiredService<IKmIntegrationAccessTokenProvider>();
            var accessToken = await provider.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Provider returned an empty access token.");

            // The production provider only returns after HTTP success, Bearer token_type,
            // expected claims and expiry have passed. Signature verification is intentionally
            // reported separately because this process does not impersonate an API receiver.
            result.Outcome = "PASS";
            result.HttpStatus = 200;
            result.Bearer = true;
            result.ClaimsValidation = "PASS";
            result.TokenAcquired = true;
            return await WriteResultAsync(output, result, 0);
        }
        catch (KmIntegrationTokenException exception)
        {
            result.Outcome = "FAIL";
            result.FailureStage = exception.HttpStatus.HasValue || exception.OAuthError is not null
                ? "HttpResponse"
                : "ProviderOrConfiguration";
            result.HttpStatus = exception.HttpStatus is { } status ? (int)status : null;
            result.OAuthError = exception.OAuthError;
            result.ErrorCode = exception.OAuthError is not null ? "OAUTH_REJECTED" : "TOKEN_REQUEST_FAILED";
            return await WriteResultAsync(output, result, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Outcome = "FAIL";
            result.FailureStage = "TimeoutOrCancellation";
            result.ErrorCode = "CANCELLED";
            return await WriteResultAsync(output, result, 1);
        }
        catch (Exception exception)
        {
            result.Outcome = "FAIL";
            result.FailureStage = "ProviderOrConfiguration";
            result.ErrorCode = exception.GetType().Name;
            return await WriteResultAsync(output, result, 1);
        }
    }

    private static async Task<int> WriteResultAsync(TextWriter output, Result result, int exitCode)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(result));
        await output.FlushAsync();
        return exitCode;
    }

    private sealed class Result
    {
        public string IdentitySid { get; init; } = "unknown";
        public string Outcome { get; set; } = "FAIL";
        public int? HttpStatus { get; set; }
        public bool? Bearer { get; set; }
        public bool TokenAcquired { get; set; }
        public string? ClaimsValidation { get; set; }
        public string SignatureValidation { get; init; } = "NOT_PERFORMED";
        public string? FailureStage { get; set; }
        public string? ErrorCode { get; set; }
        public string? OAuthError { get; set; }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public sealed class KmIntegrationTokenException(
    string message, HttpStatusCode? httpStatus = null, string? oauthError = null,
    Exception? innerException = null, string? oauthErrorDescription = null) : Exception(message, innerException)
{
    public HttpStatusCode? HttpStatus { get; } = httpStatus;
    public string? OAuthError { get; } = oauthError;
    public string? OAuthErrorDescription { get; } = oauthErrorDescription;
}

public sealed class ClientCredentialsKmIntegrationAccessTokenProvider(
    IHttpClientFactory clients,
    IOptions<KmIntegrationM2MOptions> configured,
    TimeProvider clock) : IKmIntegrationAccessTokenProvider
{
    public const string HttpClientName = "KmIntegrationM2M";

    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private TokenCache? cached;
    private FailureCache? recentFailure;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref cached);
        if (snapshot is not null && clock.GetUtcNow() < snapshot.RefreshAt)
            return snapshot.Value;

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = cached;
            if (snapshot is not null && clock.GetUtcNow() < snapshot.RefreshAt)
                return snapshot.Value;

            if (recentFailure is { } failure && clock.GetUtcNow() < failure.RetryAfter)
                throw new KmIntegrationTokenException(failure.Message, failure.HttpStatus, failure.OAuthError,
                    oauthErrorDescription: failure.OAuthErrorDescription);

            try
            {
                var refreshed = await RequestTokenAsync(cancellationToken);
                recentFailure = null;
                Volatile.Write(ref cached, refreshed);
                return refreshed.Value;
            }
            catch (KmIntegrationTokenException ex)
            {
                recentFailure = new FailureCache(ex.Message, ex.HttpStatus, ex.OAuthError,
                    ex.OAuthErrorDescription,
                    clock.GetUtcNow().AddSeconds(5));
                throw;
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<TokenCache> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.IsConfigured ||
            !Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(options.TokenEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Host != authority.Host || endpoint.Port != authority.Port ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            options.ClientId!.Contains(':') ||
            !Path.IsPathFullyQualified(options.ClientSecretFile!))
            throw new KmIntegrationTokenException("KM M2M configuration is incomplete or invalid.");

        string secret;
        try
        {
            var file = new FileInfo(options.ClientSecretFile!);
            if (!file.Exists || file.Length is < 1 or > 4096)
                throw new IOException("KM client secret file is unavailable.");
            secret = await File.ReadAllTextAsync(file.FullName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KmIntegrationTokenException("KM client secret file is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl))
            throw new KmIntegrationTokenException("KM client secret file is invalid.");

        // RFC 6749 client_secret_basic form-encodes both parts before Base64.
        // OpenIddict decodes '+' as a space, so an unescaped '+' in the secret would be changed.
        byte[] basicBytes = Encoding.ASCII.GetBytes(string.Concat(
            Uri.EscapeDataString(options.ClientId!), ":", Uri.EscapeDataString(secret)));
        string basicValue;
        try { basicValue = Convert.ToBase64String(basicBytes); }
        finally { CryptographicOperations.ZeroMemory(basicBytes); }
        secret = string.Empty;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = options.Scope!
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicValue);
        basicValue = string.Empty;

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new KmIntegrationTokenException("KM token endpoint connection failed.", ex.StatusCode);
        }
        catch (TaskCanceledException)
        {
            throw new KmIntegrationTokenException("KM token endpoint timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadOAuthErrorAsync(response.Content, cancellationToken);
                throw new KmIntegrationTokenException(
                    $"KM token endpoint rejected the request (HTTP {(int)response.StatusCode}).",
                    response.StatusCode, error.Error,
                    oauthErrorDescription: error.Description);
            }

            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = json.RootElement;
                if (!root.TryGetProperty("access_token", out var tokenElement) ||
                    tokenElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(tokenElement.GetString()) ||
                    !root.TryGetProperty("token_type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("expires_in", out var durationElement) ||
                    durationElement.ValueKind != JsonValueKind.Number ||
                    !durationElement.TryGetInt64(out var duration) || duration is < 1 or > 86400)
                    throw new KmIntegrationTokenException("KM token response is invalid.");

                var value = tokenElement.GetString()!;
                var now = clock.GetUtcNow();
                var expiresAt = ReadExpectedClaimsAndExpiry(value, options, now);
                var responseExpiry = now.AddSeconds(duration);
                if (responseExpiry < expiresAt) expiresAt = responseExpiry;
                var lifetime = expiresAt - now;
                if (lifetime <= TimeSpan.Zero)
                    throw new KmIntegrationTokenException("KM token expiry is invalid.");
                var skew = TimeSpan.FromSeconds(Math.Min(60, lifetime.TotalSeconds / 10));
                return new TokenCache(value, expiresAt - skew);
            }
            catch (KmIntegrationTokenException) { throw; }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
            {
                throw new KmIntegrationTokenException("KM token response is invalid.");
            }
        }
    }

    private static DateTimeOffset ReadExpectedClaimsAndExpiry(
        string token, KmIntegrationM2MOptions options, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
            throw new KmIntegrationTokenException("KM access token format is invalid.");
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var claims = payload.RootElement;
        if (!HasClaim(claims, "aud", options.Audience!))
            throw new KmIntegrationTokenException("KM access token audience is invalid.");
        if (!header.RootElement.TryGetProperty("alg", out var algorithm) ||
            algorithm.GetString() is not ("RS256" or "RS384" or "RS512" or "ES256" or "ES384" or "ES512") ||
            !HasClaim(claims, "iss", options.Authority!) ||
            !HasClaim(claims, "sub", options.ClientId!) ||
            !HasClaim(claims, "client_id", options.ClientId!) ||
            !HasClaim(claims, "principal_type", "service") ||
            !HasClaim(claims, "scope", options.Scope!, splitSpaces: true) ||
            !claims.TryGetProperty("exp", out var expiry) ||
            expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var unixTime))
            throw new KmIntegrationTokenException("KM access token claims are invalid.");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixTime);
        if (expiresAt <= now)
            throw new KmIntegrationTokenException("KM token expiry is invalid.");
        return expiresAt;
    }

    private static bool HasClaim(JsonElement claims, string name, string expected, bool splitSpaces = false)
    {
        if (!claims.TryGetProperty(name, out var claim)) return false;
        if (claim.ValueKind == JsonValueKind.String)
        {
            var value = claim.GetString();
            return splitSpaces
                ? value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(expected, StringComparer.Ordinal) == true
                : string.Equals(value, expected, StringComparison.Ordinal);
        }
        return claim.ValueKind == JsonValueKind.Array &&
            claim.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), expected, StringComparison.Ordinal));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private static async Task<(string? Error, string? Description)> ReadOAuthErrorAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
            string? safeError = null;
            string? safeDescription = null;
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var value = error.GetString();
                if (value is { Length: > 0 and <= 64 } &&
                    value.All(c => char.IsAsciiLetterLower(c) || c is '_' or '-'))
                    safeError = value;
            }
            if (json.RootElement.TryGetProperty("error_description", out var description) &&
                description.ValueKind == JsonValueKind.String)
            {
                var value = description.GetString();
                // Never surface arbitrary server text that could echo a credential or token.
                if (value is { Length: > 0 and <= 256 } &&
                    value.All(c => c is >= ' ' and <= '~') &&
                    !System.Text.RegularExpressions.Regex.IsMatch(value, @"[A-Za-z0-9_+/=-]{24,}"))
                    safeDescription = value;
                else if (!string.IsNullOrEmpty(value))
                    safeDescription = "[redacted]";
            }
            return (safeError, safeDescription);
        }
        catch (JsonException) { }
        return (null, null);
    }

    private sealed record TokenCache(string Value, DateTimeOffset RefreshAt);
    private sealed record FailureCache(string Message, HttpStatusCode? HttpStatus,
        string? OAuthError, string? OAuthErrorDescription, DateTimeOffset RetryAfter);
}

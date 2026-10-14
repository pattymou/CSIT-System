using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmIntegrationM2MTokenProviderTests
{
    private const string TestSecret = "test+only/secret=";
    private const string Issuer = "https://identity.example.test:7443/";

    [Fact]
    public async Task UsesClientSecretBasicAndExpectedFormWithoutSecretInBody()
    {
        using var file = new SecretFile(TestSecret);
        var calls = 0;
        var provider = Provider(file.Path, (request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Issuer + "connect/token", request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
                request.Headers.Authorization!.Parameter!));
            Assert.Equal("csit-km-ingestion-dev:" + Uri.EscapeDataString(TestSecret), decoded);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("grant_type=client_credentials", body);
            Assert.DoesNotContain("audience=", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resource=", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("scope=km.ingestion.submit", body);
            Assert.DoesNotContain("client_secret", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestSecret, body, StringComparison.Ordinal);
            return Task.FromResult(OkToken(DateTimeOffset.UtcNow.AddMinutes(15)));
        });
        Assert.False(string.IsNullOrWhiteSpace(await provider.GetAccessTokenAsync(default)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingSecretAndMissingConfigurationFailBeforeHttp()
    {
        var calls = 0;
        Task<HttpResponseMessage> Handler(HttpRequestMessage _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(OkToken(DateTimeOffset.UtcNow.AddMinutes(15)));
        }
        var missingFile = Provider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Handler);
        var fileError = await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => missingFile.GetAccessTokenAsync(default));
        Assert.Contains("unavailable", fileError.Message);
        using var file = new SecretFile(TestSecret);
        var invalidOptions = Options(file.Path);
        invalidOptions.Scope = null;
        var missingScope = Provider(file.Path, Handler, invalidOptions);
        await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => missingScope.GetAccessTokenAsync(default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ValidResponseIsCachedUntilRefreshWindow()
    {
        using var file = new SecretFile(TestSecret);
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        var calls = 0;
        var provider = Provider(file.Path, (_, _) =>
        {
            calls++;
            return Task.FromResult(OkToken(clock.GetUtcNow().AddSeconds(120), 120));
        }, clock: clock);
        var first = await provider.GetAccessTokenAsync(default);
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(first, await provider.GetAccessTokenAsync(default));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.NotEqual(first, await provider.GetAccessTokenAsync(default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneTokenRequest()
    {
        using var file = new SecretFile(TestSecret);
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(file.Path, async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return OkToken(DateTimeOffset.UtcNow.AddMinutes(15));
        });
        var tasks = Enumerable.Range(0, 12).Select(_ => provider.GetAccessTokenAsync(default)).ToArray();
        await started.Task;
        release.SetResult();
        var tokens = await Task.WhenAll(tasks);
        Assert.Single(tokens.Distinct());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MalformedResponseAndExpiryFailClosed()
    {
        using var file = new SecretFile(TestSecret);
        var malformed = Provider(file.Path, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => malformed.GetAccessTokenAsync(default));
        var expired = Provider(file.Path, (_, _) =>
            Task.FromResult(OkToken(DateTimeOffset.UtcNow.AddSeconds(-1))));
        await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => expired.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task ErrorDetailsNeverContainSecretOrTokenAndFailureIsThrottled()
    {
        using var file = new SecretFile(TestSecret);
        var calls = 0;
        var provider = Provider(file.Path, (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_client\",\"error_description\":\"" +
                    TestSecret + "\"}", Encoding.UTF8, "application/json")
            });
        });
        var error = await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatus);
        Assert.Equal("invalid_client", error.OAuthError);
        Assert.DoesNotContain(TestSecret, error.ToString());
        await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Equal(1, calls);

        const string fakeRawToken = "test.raw-token-must-not-appear.signature";
        var badToken = Provider(file.Path, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = fakeRawToken, token_type = "Bearer", expires_in = 900
            }), Encoding.UTF8, "application/json")
        }));
        var tokenError = await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => badToken.GetAccessTokenAsync(default));
        Assert.DoesNotContain(fakeRawToken, tokenError.ToString());
    }

    [Fact]
    public async Task CancellationStopsBeforeOrDuringRequest()
    {
        using var file = new SecretFile(TestSecret);
        var calls = 0;
        var provider = Provider(file.Path, async (_, cancellationToken) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(alreadyCancelled.Token));
        Assert.Equal(0, calls);
        using var duringRequest = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(duringRequest.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExistingUserSecretKeysBindToM2MOptions()
    {
        var values = new Dictionary<string, string?>
        {
            ["KmIntegrationM2M:ClientId"] = "csit-km-ingestion-dev",
            ["KmIntegrationM2M:TokenEndpoint"] = Issuer + "connect/token",
            ["KmIntegrationM2M:Audience"] = "km.integration.dev",
            ["KmIntegrationM2M:Scope"] = "km.ingestion.submit",
            ["KmIntegrationM2M:ClientSecretFile"] = "C:\\secure\\client-secret.txt"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var bound = config.GetSection(KmIntegrationM2MOptions.SectionName).Get<KmIntegrationM2MOptions>()!;
        bound.Authority = Issuer;
        Assert.True(bound.IsConfigured);
        Assert.Equal("km.integration.dev", bound.Audience);
        Assert.Equal(values["KmIntegrationM2M:ClientSecretFile"], bound.ClientSecretFile);
    }

    [Fact]
    public async Task WrongAudienceFailsClosedEvenThoughAudienceIsNotRequested()
    {
        using var file = new SecretFile(TestSecret);
        var options = Options(file.Path);
        options.Audience = "other.integration.dev";
        var provider = Provider(file.Path, (_, _) =>
            Task.FromResult(OkToken(DateTimeOffset.UtcNow.AddMinutes(15))), options);
        var error = await Assert.ThrowsAsync<KmIntegrationTokenException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Equal("KM access token audience is invalid.", error.Message);
    }

    private static ClientCredentialsKmIntegrationAccessTokenProvider Provider(
        string file, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        KmIntegrationM2MOptions? options = null, TimeProvider? clock = null)
    {
        options ??= Options(file);
        return new ClientCredentialsKmIntegrationAccessTokenProvider(
            new ClientFactory(new HttpClient(new StubHandler(handler))),
            Microsoft.Extensions.Options.Options.Create(options), clock ?? TimeProvider.System);
    }

    private static KmIntegrationM2MOptions Options(string file) => new()
    {
        Authority = Issuer,
        ClientId = "csit-km-ingestion-dev",
        TokenEndpoint = Issuer + "connect/token",
        Audience = "km.integration.dev",
        Scope = "km.ingestion.submit",
        ClientSecretFile = file
    };

    private static HttpResponseMessage OkToken(DateTimeOffset expiry, int duration = 900)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = Issuer, sub = "csit-km-ingestion-dev", client_id = "csit-km-ingestion-dev",
            principal_type = "service", aud = "km.integration.dev", scope = "km.ingestion.submit",
            exp = expiry.ToUnixTimeSeconds()
        }));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = header + "." + payload + ".signature",
                token_type = "Bearer", expires_in = duration
            }), Encoding.UTF8, "application/json")
        };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class SecretFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();
        public SecretFile(string value) => File.WriteAllText(Path, value);
        public void Dispose() => File.Delete(Path);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SIT.DepartmentSystem.Web.Services;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmQueueDiagnosticsTests
{
    [Theory]
    [InlineData(401, "IdentityTimeout")]
    [InlineData(403, "Forbidden")]
    [InlineData(500, "ServerFailure")]
    public async Task QueueRequestCorrelatesSafeStatusAndCategory(int status, string category)
    {
        var lines = new List<string>();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new CaptureProvider(lines)));
        var correlationId = Guid.NewGuid().ToString("N");
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/administration/km-queue/ingestion";
        context.Request.Headers[KmQueueRequestDiagnostics.CorrelationHeader] = correlationId;
        var middleware = new KmQueueRequestDiagnostics(http =>
        {
            http.Response.StatusCode = status;
            if (status == 401)
                http.Items[KmQueueRequestDiagnostics.SessionFailureItem] = "IdentityTimeout";
            return Task.CompletedTask;
        }, factory.CreateLogger<KmQueueRequestDiagnostics>());

        await middleware.InvokeAsync(context);

        Assert.Equal(correlationId,
            context.Response.Headers[KmQueueRequestDiagnostics.CorrelationHeader]);
        var line = Assert.Single(lines);
        Assert.Contains("/api/administration/km-queue/ingestion", line);
        Assert.Contains($"HttpStatus={status}", line);
        Assert.Contains($"CorrelationId={correlationId}", line);
        Assert.Contains($"ErrorCategory={category}", line);
        Assert.DoesNotContain("Cookie", line);
        Assert.DoesNotContain("Token", line);
    }

    [Fact]
    public async Task QueueRejectsUntrustedCorrelationAndDoesNotEchoIt()
    {
        using var factory = LoggerFactory.Create(_ => { });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/administration/km-queue/deletion";
        context.Request.Headers[KmQueueRequestDiagnostics.CorrelationHeader] =
            "untrusted value with a secret";
        var middleware = new KmQueueRequestDiagnostics(_ => Task.CompletedTask,
            factory.CreateLogger<KmQueueRequestDiagnostics>());

        await middleware.InvokeAsync(context);

        Assert.True(Guid.TryParseExact(
            context.Response.Headers[KmQueueRequestDiagnostics.CorrelationHeader], "N", out _));
    }

    [Fact]
    public async Task BrowserQueueRequestDistinguishesHttpAndMalformedJson()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var response = new BrowserApiResponse
        {
            StatusCode = 401, Body = "", CorrelationId = correlationId
        };
        var module = new FakeModule(response);
        await using var client = new BrowserApiClient(new FakeJsRuntime(module));
        var exception = await Assert.ThrowsAsync<BrowserApiException>(() =>
            client.GetQueueAsync<Payload>("/api/administration/km-queue/ingestion",
                correlationId, CancellationToken.None));
        Assert.Equal(401, exception.StatusCode);
        Assert.Equal(correlationId, exception.CorrelationId);
        Assert.Equal(correlationId, module.LastArguments![3]);

        response.StatusCode = 200;
        response.Body = "<html>login</html>";
        await Assert.ThrowsAsync<BrowserApiDeserializationException>(() =>
            client.GetQueueAsync<Payload>("/api/administration/km-queue/ingestion",
                correlationId, CancellationToken.None));
        response.Body = "{\"value\":3}";
        Assert.Equal(3, (await client.GetQueueAsync<Payload>(
            "/api/administration/km-queue/ingestion", correlationId,
            CancellationToken.None)).Value);
    }

    private sealed record Payload(int Value);

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new((TValue)module);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FakeModule(BrowserApiResponse response) : IJSObjectReference
    {
        public object?[]? LastArguments { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastArguments = args;
            return new((TValue)(object)response);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(lines);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(List<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }

    private sealed class Scope : IDisposable
    {
        public static readonly Scope Instance = new();
        public void Dispose() { }
    }
}

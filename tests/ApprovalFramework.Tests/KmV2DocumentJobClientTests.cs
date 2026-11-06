using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SIT.DepartmentSystem.Web.Services.KmIntegration;

namespace ApprovalFramework.Tests;

public sealed class KmV2DocumentJobClientTests
{
    [Fact]
    public async Task AcceptedResponseRequiresExactCorrelationAndUsesStableIdempotencyKey()
    {
        var request = Request();
        var calls = 0;
        string? json = null;
        using var http = new HttpClient(new Handler(async message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(KmIntegrationClient.DocumentJobV2Path, message.RequestUri!.AbsolutePath);
            Assert.Equal(request.EventId.ToString("D"),
                Assert.Single(message.Headers.GetValues("Idempotency-Key")));
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            json = await message.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new KmV2DocumentJobAcceptedResponse(
                    request.EventId, request.CsitJobId, "KM-V2-1", "Accepted"))
            };
        }));
        var client = Client(http, timeoutSeconds: 2);

        var first = await client.SubmitDocumentJobAsync(request, default);
        var repeated = await client.SubmitDocumentJobAsync(request, default);

        Assert.True(first.Accepted);
        Assert.Equal("KM-V2-1", first.JobId);
        Assert.Equal(first.JobId, repeated.JobId);
        Assert.Equal(2, calls);
        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(request.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(request.CsitJobId, root.GetProperty("csitJobId").GetGuid());
        Assert.Equal("CSIT", root.GetProperty("sourceSystem").GetString());
        Assert.Equal("VendorFile", root.GetProperty("source").GetProperty("type").GetString());
        Assert.True(root.GetProperty("classification").GetProperty("l1").ValueKind ==
            JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("supersedes").ValueKind);
        Assert.EndsWith("Z", root.GetProperty("occurredAt").GetString());
        Assert.EndsWith("Z", root.GetProperty("document").GetProperty("approval")
            .GetProperty("approvedAt").GetString());
    }

    [Fact]
    public async Task Mismatched202IsRejectedWithoutAJobId()
    {
        var request = Request();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new KmV2DocumentJobAcceptedResponse(
                    request.EventId, Guid.NewGuid(), "KM-WRONG", "Accepted"))
            })));

        var result = await Client(http, 2).SubmitDocumentJobAsync(request, default);

        Assert.False(result.Accepted);
        Assert.False(result.Retryable);
        Assert.Equal("INVALID_RESPONSE", result.ErrorCode);
        Assert.Null(result.JobId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientHttpResponsesRemainRetryable(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(status))));

        var result = await Client(http, 2).SubmitDocumentJobAsync(Request(), default);

        Assert.False(result.Accepted);
        Assert.True(result.Retryable);
        Assert.Equal("HTTP_" + (int)status, result.ErrorCode);
    }

    [Fact]
    public async Task TimeoutRemainsRetryable()
    {
        using var http = new HttpClient(new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }));

        var result = await Client(http, 1).SubmitDocumentJobAsync(Request(), default);

        Assert.False(result.Accepted);
        Assert.True(result.Retryable);
        Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    private static KmV2DocumentJobRequest Request()
    {
        var now = new DateTime(2026, 11, 5, 1, 7, 4, DateTimeKind.Utc);
        return new KmV2DocumentJobRequest(
            "2.0", Guid.NewGuid(), Guid.NewGuid(),
            "VendorFileArchived", "CSIT",
            new KmV2DocumentJobSource("VendorFile", Guid.NewGuid(), "vendor",
                null, null, Guid.NewGuid(), null),
            new KmV2Document(Guid.NewGuid(), 1, "notes.txt", "text/plain", 12,
                new string('a', 64),
                "https://csit.invalid/api/integrations/km/v1/vendor-files/11111111-1111-1111-1111-111111111111/content",
                now, new KmV2DocumentApproval("ApprovalBypassed", Guid.NewGuid(), now)),
            new KmV2DocumentClassification(null, null, null), null, now);
    }

    private static KmIntegrationClient Client(HttpClient http, int timeoutSeconds) => new(
        http,
        Options.Create(new KmIntegrationOptions
        {
            Enabled = true,
            BaseUrl = "https://km.invalid/",
            TimeoutSeconds = timeoutSeconds
        }),
        new Token());

    private sealed class Token : IKmIntegrationAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("token");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) :
            this((message, _) => reply(message)) { }

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) =>
            _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _reply(request, cancellationToken);
    }
}

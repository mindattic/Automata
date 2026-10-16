using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers <see cref="AnthropicToolClient"/> directly (auth header selection, tools-omission,
/// 429/529 retry-with-backoff, non-retryable failures) via a stub <see cref="HttpMessageHandler"/>
/// that records every request and replays a queue of canned responses.
/// </summary>
[TestFixture]
public class AnthropicToolClientTests
{
    private static JsonArray OneUserMessage() => new JsonArray
    {
        new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "hi" } },
        },
    };

    private static AnthropicToolClient MakeClient(RecordingHandler handler) =>
        new(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance);

    [Test]
    public async Task OAuthKey_SendsBearerAuthorization_NotXApiKey()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        await client.CreateAsync("sk-ant-oat-test-key", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        var req = handler.Requests[0];
        Assert.That(req.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
        Assert.That(req.Headers.Authorization?.Parameter, Is.EqualTo("sk-ant-oat-test-key"));
        Assert.That(req.Headers.Contains("x-api-key"), Is.False);
    }

    [Test]
    public async Task PlainKey_SendsXApiKeyHeader_NotBearer()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        await client.CreateAsync("sk-ant-api03-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        var req = handler.Requests[0];
        Assert.That(req.Headers.Authorization, Is.Null);
        Assert.That(req.Headers.GetValues("x-api-key").Single(), Is.EqualTo("sk-ant-api03-plain"));
    }

    [Test]
    public async Task EveryRequest_SendsAnthropicVersionHeader()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        await client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        Assert.That(handler.Requests[0].Headers.GetValues("anthropic-version").Single(), Is.EqualTo("2023-06-01"));
    }

    [Test]
    public async Task EmptyTools_OmitsToolsFieldFromRequestBody()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        await client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        var body = (JsonObject)JsonNode.Parse(handler.Bodies[0])!;
        Assert.That(body.ContainsKey("tools"), Is.False);
    }

    [Test]
    public async Task NonEmptyTools_IncludesToolsFieldInRequestBody()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);
        var tools = new JsonArray { new JsonObject { ["name"] = "click_button" } };

        await client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), tools, 100, default);

        var body = JsonNode.Parse(handler.Bodies[0])!;
        Assert.That(body["tools"]![0]!["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
    }

    [Test]
    public async Task Status429_RetriesAfterHonoringRetryAfterHeader_ThenSucceeds()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"rate limited"}}""", TimeSpan.Zero),
            (HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        var result = await client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(result.Content[0]!["text"]!.GetValue<string>(), Is.EqualTo("ok"));
    }

    [Test]
    public async Task Status529Overloaded_RetriesAfterHonoringRetryAfterHeader_ThenSucceeds()
    {
        var handler = new RecordingHandler(
            ((HttpStatusCode)529, """{"type":"error","error":{"message":"overloaded"}}""", TimeSpan.Zero),
            (HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""", null));
        var client = MakeClient(handler);

        var result = await client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default);

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(result.Content[0]!["text"]!.GetValue<string>(), Is.EqualTo("ok"));
    }

    [Test]
    public void RetriesExhausted_ThrowsHttpRequestExceptionWithStatusCode()
    {
        var responses = Enumerable.Range(0, AnthropicToolClient.MaxRetries + 1)
            .Select(_ => ((HttpStatusCode, string, TimeSpan?))(HttpStatusCode.TooManyRequests, "still limited", TimeSpan.Zero))
            .ToArray();
        var handler = new RecordingHandler(responses);
        var client = MakeClient(handler);

        var ex = Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(handler.Requests, Has.Count.EqualTo(AnthropicToolClient.MaxRetries + 1));
    }

    [Test]
    public void NonRetryableStatus_ThrowsImmediately_WithoutRetrying()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"bad request"}}""", null));
        var client = MakeClient(handler);

        var ex = Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateAsync("sk-plain", "claude-x", "sys", OneUserMessage(), new JsonArray(), 100, default));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void ResolveRetryDelay_HonorsNumericRetryAfterHeader()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.TryAddWithoutValidation("Retry-After", "7");

        var delay = AnthropicToolClient.ResolveRetryDelay(resp, attempt: 0);

        Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(7)));
    }

    [TestCase(0, 5)]
    [TestCase(1, 10)]
    [TestCase(2, 20)]
    [TestCase(3, 40)]
    public void ResolveRetryDelay_NoRetryAfterHeader_ExponentialBackoffFromBaseDelay(int attempt, int expectedSeconds)
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var delay = AnthropicToolClient.ResolveRetryDelay(resp, attempt);

        Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    [Test]
    public void ResolveRetryDelay_NoRetryAfterHeader_CapsAtMaxDelay()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        // Uncapped exponential value at attempt 4 would be 80s; must clamp to MaxDelay (60s).
        var delay = AnthropicToolClient.ResolveRetryDelay(resp, attempt: 4);

        Assert.That(delay, Is.EqualTo(AnthropicToolClient.MaxDelay));
    }

    /// <summary>Replays a fixed queue of (status, body, Retry-After) responses and records every
    /// request's headers and body for later assertions.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public RecordingHandler(params (HttpStatusCode, string, TimeSpan?)[] responses)
        {
            this.responses = new(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            var (status, body, retryAfter) = responses.Count > 0
                ? responses.Dequeue()
                : (HttpStatusCode.InternalServerError, "no more stubbed responses", (TimeSpan?)null);

            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is { } delay) resp.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            return resp;
        }
    }
}

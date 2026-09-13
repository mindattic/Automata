using System.Net;
using System.Text;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers the "sticky failover" behaviour shared by every <c>IToolCallingLlm</c> adapter that
/// supports more than one API key per provider (<see cref="KeyPoolFailover"/>, and its use inside
/// <see cref="AnthropicToolCallingLlm"/>, <see cref="OpenAiToolCallingLlm"/>,
/// <see cref="GeminiToolCallingLlm"/>): the first key is used until it fails with an
/// auth/rate-limit/server/network error, then the next key is tried.
/// </summary>
[TestFixture]
public class KeyPoolFailoverTests
{
    private static (string SystemPrompt, System.Collections.Generic.IReadOnlyList<ToolLoopMessage> History, System.Collections.Generic.IReadOnlyList<ToolDefinition> Tools) SimpleTurn() =>
        ("system", new[] { new ToolLoopMessage.UserText("hi") }, System.Array.Empty<ToolDefinition>());

    [Test]
    public async Task Anthropic_FirstKeyUnauthorized_FailsOverToSecondKey()
    {
        var handler = new KeyAwareHandler(new()
        {
            ["sk-bad"] = (HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"invalid key"}}"""),
            ["sk-good"] = (HttpStatusCode.OK, """{"content":[{"type":"text","text":"hello"}]}"""),
        }, headerName: "x-api-key");
        var client = new AnthropicToolClient(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance);
        var llm = new AnthropicToolCallingLlm(client, () => new[] { "sk-bad", "sk-good" });

        var (systemPrompt, history, tools) = SimpleTurn();
        var result = await llm.CreateTurnAsync(systemPrompt, history, tools, 100, default);

        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
        Assert.That(handler.CallCountFor("sk-bad"), Is.EqualTo(1));
        Assert.That(handler.CallCountFor("sk-good"), Is.EqualTo(1));
    }

    [Test]
    public void Anthropic_EveryKeyFails_ThrowsTheLastKeysException()
    {
        var handler = new KeyAwareHandler(new()
        {
            ["sk-a"] = (HttpStatusCode.Unauthorized, "nope"),
            ["sk-b"] = (HttpStatusCode.Unauthorized, "nope"),
        }, headerName: "x-api-key");
        var client = new AnthropicToolClient(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance);
        var llm = new AnthropicToolCallingLlm(client, () => new[] { "sk-a", "sk-b" });

        var (systemPrompt, history, tools) = SimpleTurn();
        Assert.ThrowsAsync<HttpRequestException>(() => llm.CreateTurnAsync(systemPrompt, history, tools, 100, default));
        Assert.That(handler.CallCountFor("sk-a"), Is.EqualTo(1));
        Assert.That(handler.CallCountFor("sk-b"), Is.EqualTo(1));
    }

    [Test]
    public async Task OpenAi_FirstKeyUnauthorized_FailsOverToSecondKey()
    {
        var handler = new KeyAwareHandler(new()
        {
            ["sk-bad"] = (HttpStatusCode.Unauthorized, "nope"),
            ["sk-good"] = (HttpStatusCode.OK, """{"choices":[{"message":{"content":"hello"}}]}"""),
        }, headerName: null); // OpenAI adapter uses Authorization: Bearer, not a named header
        var llm = new OpenAiToolCallingLlm(new HttpClient(handler), NullLogger<OpenAiToolCallingLlm>.Instance,
            () => new[] { "sk-bad", "sk-good" });

        var (systemPrompt, history, tools) = SimpleTurn();
        var result = await llm.CreateTurnAsync(systemPrompt, history, tools, 100, default);

        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
        Assert.That(handler.CallCountFor("sk-bad"), Is.EqualTo(1));
        Assert.That(handler.CallCountFor("sk-good"), Is.EqualTo(1));
    }

    [Test]
    public async Task Gemini_FirstKeyUnauthorized_FailsOverToSecondKey()
    {
        var handler = new KeyAwareHandler(new()
        {
            ["key-bad"] = (HttpStatusCode.Unauthorized, "nope"),
            ["key-good"] = (HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}"""),
        }, headerName: "x-goog-api-key");
        var llm = new GeminiToolCallingLlm(new HttpClient(handler), NullLogger<GeminiToolCallingLlm>.Instance,
            () => new[] { "key-bad", "key-good" });

        var (systemPrompt, history, tools) = SimpleTurn();
        var result = await llm.CreateTurnAsync(systemPrompt, history, tools, 100, default);

        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
        Assert.That(handler.CallCountFor("key-bad"), Is.EqualTo(1));
        Assert.That(handler.CallCountFor("key-good"), Is.EqualTo(1));
    }

    [Test]
    public void ExecuteAsync_CallerCancellation_PropagatesWithoutTryingNextKey()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var secondKeyCalls = 0;

        Assert.ThrowsAsync<TaskCanceledException>(() => KeyPoolFailover.ExecuteAsync<string>(
            new[] { "key-a", "key-b" },
            cts.Token,
            key =>
            {
                if (key == "key-b") secondKeyCalls++;
                throw new TaskCanceledException("run stopped");
            }));

        Assert.That(secondKeyCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task SingleKeyResolver_StillWorks_NoPoolBehaviorChange()
    {
        var handler = new KeyAwareHandler(new()
        {
            ["sk-only"] = (HttpStatusCode.OK, """{"content":[{"type":"text","text":"hello"}]}"""),
        }, headerName: "x-api-key");
        var client = new AnthropicToolClient(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance);
        var llm = new AnthropicToolCallingLlm(client, (Func<string?>)(() => "sk-only"));

        var (systemPrompt, history, tools) = SimpleTurn();
        var result = await llm.CreateTurnAsync(systemPrompt, history, tools, 100, default);

        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
        Assert.That(handler.CallCountFor("sk-only"), Is.EqualTo(1));
    }

    /// <summary>Routes responses by the incoming auth header value (named header, or the Bearer
    /// token when <paramref name="headerName"/> is null); counts calls per key.</summary>
    private sealed class KeyAwareHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Code, string Body)> byKey;
        private readonly string? headerName;
        private readonly Dictionary<string, int> counts = new();

        public KeyAwareHandler(Dictionary<string, (HttpStatusCode, string)> byKey, string? headerName)
        {
            this.byKey = byKey;
            this.headerName = headerName;
        }

        public int CallCountFor(string key) => counts.GetValueOrDefault(key, 0);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = headerName is not null
                ? (request.Headers.TryGetValues(headerName, out var values) ? values.First() : "")
                : request.Headers.Authorization?.Parameter ?? "";
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;

            var (code, body) = byKey.TryGetValue(key, out var response)
                ? response
                : (HttpStatusCode.InternalServerError, "unrecognized key in test handler");
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}

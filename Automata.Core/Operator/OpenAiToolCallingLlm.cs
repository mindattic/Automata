using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MindAttic.Legion;

namespace Automata.Core.Operator;

/// <summary>
/// <see cref="IToolCallingLlm"/> adapter speaking OpenAI's Chat Completions function-calling wire
/// shape directly (Legion is chat-text-only and has no tool-calling concept — same reason
/// <see cref="AnthropicToolClient"/> bypasses it). This is the fallback tier when no Claude
/// credentials are configured. Ported verbatim from Prose.KdpPublish/Prose.Core.
/// </summary>
public class OpenAiToolCallingLlm : IToolCallingLlm
{
    private readonly HttpClient http;
    private readonly ILogger<OpenAiToolCallingLlm> log;
    private readonly string model;
    private readonly Func<string?> resolveApiKey;
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    private const int MaxRetries = 5;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public string Name => "OpenAI";

    // Both public ctors start (HttpClient, ILogger, …), which makes the typed-client factory's
    // constructor selection ambiguous — ActivatorUtilities throws at resolve time without this
    // attribute pinning the production one (crashed at startup once ReplayEngine began resolving
    // BrowserOperatorService eagerly).
    [ActivatorUtilitiesConstructor]
    public OpenAiToolCallingLlm(HttpClient http, ILogger<OpenAiToolCallingLlm> log)
    {
        this.http = http;
        this.log = log;
        this.resolveApiKey = () => MindAtticCredentialStore.GetKey("openai");
        this.model = "gpt-4.1";
    }

    /// <summary>Test-friendly constructor — injects the API-key resolver instead of reading the
    /// real shared credential store.</summary>
    public OpenAiToolCallingLlm(HttpClient http, ILogger<OpenAiToolCallingLlm> log, Func<string?> resolveApiKey, string model = "gpt-4.1")
    {
        this.http = http;
        this.log = log;
        this.resolveApiKey = resolveApiKey;
        this.model = model;
    }

    public Task<bool> IsConfiguredAsync() =>
        Task.FromResult(!string.IsNullOrWhiteSpace(resolveApiKey()));

    public async Task<ToolTurnResult> CreateTurnAsync(
        string systemPrompt,
        IReadOnlyList<ToolLoopMessage> history,
        IReadOnlyList<ToolDefinition> tools,
        int maxTokens,
        CancellationToken ct)
    {
        var apiKey = resolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No OpenAI API key configured ('openai' provider key in the credential store).");

        var messages = ToOpenAiMessages(systemPrompt, history);
        var toolsArray = ToOpenAiTools(tools);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
        };
        if (toolsArray.Count > 0) body["tools"] = toolsArray;

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(JsonNode.Parse(body.ToJsonString())),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var retryable = resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                if (retryable && attempt < MaxRetries)
                {
                    var delay = ResolveRetryDelay(resp, attempt);
                    log.LogWarning("OpenAI {Status} (attempt {Attempt}/{Max}) — retrying in {Delay}s",
                        (int)resp.StatusCode, attempt + 1, MaxRetries, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }
                log.LogWarning("OpenAI {Status}: {Body}", (int)resp.StatusCode, Truncate(raw, 500));
                throw new InvalidOperationException($"OpenAI API {(int)resp.StatusCode}: {Truncate(raw, 500)}");
            }

            var doc = JsonNode.Parse(raw) ?? throw new InvalidOperationException("OpenAI response was null JSON");
            var message = doc["choices"]?[0]?["message"]
                ?? throw new InvalidOperationException("OpenAI response had no choices[0].message");
            return new ToolTurnResult(FromOpenAiMessage(message));
        }
    }

    private static JsonArray ToOpenAiTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                },
            });
        }
        return arr;
    }

    private static JsonArray ToOpenAiMessages(string systemPrompt, IReadOnlyList<ToolLoopMessage> history)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };

        foreach (var msg in history)
        {
            switch (msg)
            {
                case ToolLoopMessage.UserText u:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = u.Text });
                    break;

                case ToolLoopMessage.AssistantTurn a:
                    var text = string.Join("\n", a.Parts.OfType<AssistantPart.Text>().Select(t => t.Value));
                    var calls = a.Parts.OfType<AssistantPart.ToolCall>().ToList();
                    var assistantMsg = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text.Length > 0 ? text : null,
                    };
                    if (calls.Count > 0)
                    {
                        var toolCallsArr = new JsonArray();
                        foreach (var c in calls)
                        {
                            toolCallsArr.Add(new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = c.Name,
                                    ["arguments"] = string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson,
                                },
                            });
                        }
                        assistantMsg["tool_calls"] = toolCallsArr;
                    }
                    messages.Add(assistantMsg);
                    break;

                case ToolLoopMessage.ToolResults r:
                    // OpenAI expects one "tool" message per call result, not grouped into one
                    // block like Anthropic's tool_result content array.
                    foreach (var res in r.Results)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = res.ToolCallId,
                            ["content"] = res.Content,
                        });
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown ToolLoopMessage type: {msg.GetType()}");
            }
        }
        return messages;
    }

    private static List<AssistantPart> FromOpenAiMessage(JsonNode message)
    {
        var parts = new List<AssistantPart>();
        var content = message["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content)) parts.Add(new AssistantPart.Text(content));

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                if (tc is null) continue;
                var id = tc["id"]?.GetValue<string>() ?? "";
                var fn = tc["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"]?.GetValue<string>() ?? "{}";
                parts.Add(new AssistantPart.ToolCall(id, name, args));
            }
        }
        return parts;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        var backoff = TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, attempt));
        return backoff > MaxDelay ? MaxDelay : backoff;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MindAttic.Legion;

namespace Automata.Core.Operator;

/// <summary>
/// <see cref="IToolCallingLlm"/> adapter for Google Gemini's generateContent API. Gemini is the
/// one provider that genuinely needs its own pathway: its function-calling wire format differs
/// structurally from both Anthropic and OpenAI — <c>functionDeclarations</c> instead of a tools
/// array envelope, <c>functionCall</c>/<c>functionResponse</c> parts inside role
/// "model"/"user" contents, arguments as live JSON objects rather than strings, and NO tool-call
/// ids (results correlate back by function NAME, so this adapter synthesizes ids for the neutral
/// shape and maps them back to names when serializing history).
/// </summary>
public class GeminiToolCallingLlm : IToolCallingLlm
{
    private readonly HttpClient http;
    private readonly ILogger<GeminiToolCallingLlm> log;
    private readonly string model;
    private readonly Func<string?> resolveApiKey;
    private const int MaxRetries = 5;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public string Name => "Gemini";

    public GeminiToolCallingLlm(
        HttpClient http,
        ILogger<GeminiToolCallingLlm> log,
        Func<string?>? resolveApiKey = null,
        string model = "gemini-2.5-flash")
    {
        this.http = http;
        this.log = log;
        this.resolveApiKey = resolveApiKey ?? (() => MindAtticCredentialStore.GetKey("gemini"));
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
            throw new InvalidOperationException("No Gemini API key configured — set one in Settings or the credential store.");

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } },
            },
            ["contents"] = ToGeminiContents(history),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = maxTokens },
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray
            {
                new JsonObject { ["functionDeclarations"] = ToFunctionDeclarations(tools) },
            };
        }

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(JsonNode.Parse(body.ToJsonString())),
            };
            req.Headers.Add("x-goog-api-key", apiKey);

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var retryable = resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || (int)resp.StatusCode >= 500;
                if (retryable && attempt < MaxRetries)
                {
                    var delay = ResolveRetryDelay(resp, attempt);
                    log.LogWarning("Gemini {Status} (attempt {Attempt}/{Max}) — retrying in {Delay}s",
                        (int)resp.StatusCode, attempt + 1, MaxRetries, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }
                log.LogWarning("Gemini {Status}: {Body}", (int)resp.StatusCode, Truncate(raw, 500));
                throw new InvalidOperationException($"Gemini API {(int)resp.StatusCode}: {Truncate(raw, 500)}");
            }

            var doc = JsonNode.Parse(raw) ?? throw new InvalidOperationException("Gemini response was null JSON");
            var parts = doc["candidates"]?[0]?["content"]?["parts"] as JsonArray
                ?? throw new InvalidOperationException("Gemini response had no candidates[0].content.parts");
            return new ToolTurnResult(FromGeminiParts(parts));
        }
    }

    private static JsonArray ToFunctionDeclarations(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema.DeepClone(),
            });
        }
        return arr;
    }

    internal static JsonArray ToGeminiContents(IReadOnlyList<ToolLoopMessage> history)
    {
        var contents = new JsonArray();
        // Gemini has no tool-call ids; results correlate by function NAME. Track the mapping
        // from our synthesized ids so ToolResults can be serialized back correctly.
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in history)
        {
            switch (msg)
            {
                case ToolLoopMessage.UserText u:
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = u.Text } },
                    });
                    break;

                case ToolLoopMessage.AssistantTurn a:
                {
                    var parts = new JsonArray();
                    foreach (var part in a.Parts)
                    {
                        switch (part)
                        {
                            case AssistantPart.Text t:
                                parts.Add(new JsonObject { ["text"] = t.Value });
                                break;
                            case AssistantPart.ToolCall c:
                                callIdToName[c.Id] = c.Name;
                                parts.Add(new JsonObject
                                {
                                    ["functionCall"] = new JsonObject
                                    {
                                        ["name"] = c.Name,
                                        ["args"] = JsonNode.Parse(
                                            string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson),
                                    },
                                });
                                break;
                        }
                    }
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                }

                case ToolLoopMessage.ToolResults r:
                {
                    var parts = new JsonArray();
                    foreach (var res in r.Results)
                    {
                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = callIdToName.GetValueOrDefault(res.ToolCallId, res.ToolCallId),
                                ["response"] = WrapResponse(res.Content),
                            },
                        });
                    }
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unknown ToolLoopMessage type: {msg.GetType()}");
            }
        }
        return contents;
    }

    /// <summary>functionResponse.response must be a JSON OBJECT — parse the tool's JSON result,
    /// wrapping non-object payloads (bare strings/arrays) under a "result" key.</summary>
    private static JsonNode WrapResponse(string content)
    {
        try
        {
            var parsed = JsonNode.Parse(content);
            if (parsed is JsonObject obj) return obj;
            return new JsonObject { ["result"] = parsed };
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject { ["result"] = content };
        }
    }

    internal static List<AssistantPart> FromGeminiParts(JsonArray parts)
    {
        var result = new List<AssistantPart>();
        var callIndex = 0;
        foreach (var part in parts)
        {
            if (part is null) continue;
            if (part["text"]?.GetValue<string>() is { Length: > 0 } text)
                result.Add(new AssistantPart.Text(text));
            if (part["functionCall"] is JsonObject call)
            {
                var name = call["name"]?.GetValue<string>() ?? "";
                var args = call["args"]?.ToJsonString() ?? "{}";
                // Gemini has no call ids — synthesize one; the reverse mapping in
                // ToGeminiContents recovers the name when the result comes back.
                result.Add(new AssistantPart.ToolCall($"gemini-call-{callIndex++}", name, args));
            }
        }
        return result;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        var backoff = TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, attempt));
        return backoff > MaxDelay ? MaxDelay : backoff;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

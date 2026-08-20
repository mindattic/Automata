using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Automata.Core.Operator;

/// <summary>
/// Minimal Anthropic Messages API client with tool-use support. Reuses the credential
/// resolution MindAttic.Legion already does (the shared MindAtticCredentialStore, itself
/// backed by MindAttic.Vault), so users don't have to set up auth twice. We bypass Legion's own
/// chat client here because Legion is text-only — it has no concept of tools or tool_use
/// response blocks. Ported verbatim from Prose.KdpPublish/Prose.Core.
///
/// One method, one shape: post a (system + messages + tools) request, get back the raw
/// content-blocks array. The operator loop interprets them.
/// </summary>
public class AnthropicToolClient
{
    private readonly HttpClient http;
    private readonly ILogger<AnthropicToolClient> log;
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    // Rate limits (429) and transient capacity errors (529 overloaded) are retried with
    // backoff rather than failing immediately — the OAuth credential pool (shared with an
    // active Claude Code session) legitimately contends for the same account's rate-limit
    // budget, and a short wait is often enough to clear it.
    private const int MaxRetries = 5;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public AnthropicToolClient(HttpClient http, ILogger<AnthropicToolClient> log)
    {
        this.http = http;
        this.log = log;
    }

    /// <summary>
    /// One Messages API round-trip. Returns the raw response JSON (content blocks as a
    /// JsonNode array, plus the stop_reason). Caller parses the content.
    /// </summary>
    public async Task<AnthropicTurnResponse> CreateAsync(
        string apiKey,
        string model,
        string systemPrompt,
        JsonArray messages,
        JsonArray tools,
        int maxTokens,
        CancellationToken ct)
    {
        // DeepClone before assigning so successive iterations of the operator tool-use loop
        // don't fail with "the node already has a parent" — a JsonNode retains its Parent
        // reference even after the previous body goes out of scope.
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = systemPrompt,
            ["messages"] = messages.DeepClone(),
        };
        if (tools.Count > 0) body["tools"] = tools.DeepClone();

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(body),
            };
            // OAuth access tokens (Claude Code CLI's Team session, prefix "sk-ant-oat")
            // authenticate via Authorization: Bearer; raw pay-per-token API keys use
            // x-api-key. Same convention as MindAttic.Legion's LegionClient.AddClaudeAuth —
            // kept in sync manually since that helper is internal to the Legion package.
            if (apiKey.StartsWith("sk-ant-oat", StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else
                req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var retryable = resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || (int)resp.StatusCode == 529; // Anthropic's "overloaded_error"
                if (retryable && attempt < MaxRetries)
                {
                    var delay = ResolveRetryDelay(resp, attempt);
                    log.LogWarning(
                        "Anthropic {Status} (attempt {Attempt}/{Max}) — retrying in {Delay}s: {Body}",
                        (int)resp.StatusCode, attempt + 1, MaxRetries, delay.TotalSeconds, Truncate(raw, 200));
                    await Task.Delay(delay, ct);
                    continue;
                }
                log.LogWarning("Anthropic {Status}: {Body}", (int)resp.StatusCode, raw);
                throw new InvalidOperationException(
                    $"Anthropic API {(int)resp.StatusCode}: {Truncate(raw, 500)}");
            }

            var doc = JsonNode.Parse(raw)
                ?? throw new InvalidOperationException("Anthropic response was null JSON");
            var content = doc["content"] as JsonArray ?? new JsonArray();
            var stopReason = doc["stop_reason"]?.GetValue<string>() ?? "";
            return new AnthropicTurnResponse(content, stopReason);
        }
    }

    /// <summary>Honors a numeric Retry-After header (seconds) when Anthropic sends one;
    /// otherwise exponential backoff from <see cref="BaseDelay"/>, capped at <see cref="MaxDelay"/>.</summary>
    private static TimeSpan ResolveRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (resp.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(values.FirstOrDefault(), out var secs))
            return TimeSpan.FromSeconds(secs);

        var backoff = TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, attempt));
        return backoff > MaxDelay ? MaxDelay : backoff;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// One Anthropic Messages API turn. <see cref="Content"/> is the raw content-blocks array —
/// each block is either {type:"text", text:"..."} or {type:"tool_use", id, name, input:{...}}.
/// </summary>
public sealed record AnthropicTurnResponse(JsonArray Content, string StopReason);

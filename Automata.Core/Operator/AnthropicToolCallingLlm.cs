using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Legion;

namespace Automata.Core.Operator;

/// <summary>
/// <see cref="IToolCallingLlm"/> adapter over <see cref="AnthropicToolClient"/> — reference
/// implementation of the interface: every other vendor adapter translates to/from this same
/// neutral shape. Ported verbatim from Prose.KdpPublish/Prose.Core.
/// </summary>
public class AnthropicToolCallingLlm : IToolCallingLlm
{
    private readonly AnthropicToolClient client;
    private readonly string model;
    private readonly Func<IReadOnlyList<string>> resolveApiKeys;

    public string Name => "Claude";

    public AnthropicToolCallingLlm(AnthropicToolClient client, string model = "claude-opus-4-7")
        : this(client, ResolveApiKey, model) { }

    /// <summary>Test-friendly constructor — injects a single-key resolver instead of reading the
    /// real Claude Code OAuth session / shared credential store.</summary>
    public AnthropicToolCallingLlm(AnthropicToolClient client, Func<string?> resolveApiKey, string model = "claude-opus-4-7")
        : this(client, AsPool(resolveApiKey), model) { }

    /// <summary>
    /// Key-pool constructor: when more than one key is configured, a key that fails with an
    /// auth/rate-limit/server error causes the NEXT key to be tried ("sticky failover" — the
    /// first working key keeps being used across calls; only an actual failure advances to the
    /// next one). Each key still gets <see cref="AnthropicToolClient"/>'s own 429/529 retry
    /// budget before being considered "failed".
    /// </summary>
    public AnthropicToolCallingLlm(AnthropicToolClient client, Func<IReadOnlyList<string>> resolveApiKeys, string model = "claude-opus-4-7")
    {
        this.client = client;
        this.resolveApiKeys = resolveApiKeys;
        this.model = model;
    }

    private static Func<IReadOnlyList<string>> AsPool(Func<string?> resolveApiKey) => () =>
    {
        var key = resolveApiKey();
        return string.IsNullOrWhiteSpace(key) ? Array.Empty<string>() : new[] { key };
    };

    public Task<bool> IsConfiguredAsync() =>
        Task.FromResult(resolveApiKeys().Count > 0);

    public async Task<ToolTurnResult> CreateTurnAsync(
        string systemPrompt,
        IReadOnlyList<ToolLoopMessage> history,
        IReadOnlyList<ToolDefinition> tools,
        int maxTokens,
        CancellationToken ct)
    {
        var keys = resolveApiKeys();
        if (keys.Count == 0)
            throw new InvalidOperationException(
                "No Anthropic API key configured — set one in Settings, or add a 'claude' " +
                "provider key to the shared credential store.");

        var messages = ToAnthropicMessages(history);
        var toolsArray = ToAnthropicTools(tools);

        return await KeyPoolFailover.ExecuteAsync(keys, ct, async key =>
        {
            var turn = await client.CreateAsync(key, model, systemPrompt, messages, toolsArray, maxTokens, ct);
            return new ToolTurnResult(FromAnthropicContent(turn.Content));
        });
    }

    /// <summary>
    /// Default credential chain: the shared MindAttic credential store, under the "claude" id
    /// every MindAttic app uses. Public so DI can compose it behind a user-supplied BYO-key
    /// override.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT fall back to a Claude Code Team OAuth session — a Team-subscription
    /// OAuth token authenticates the Claude Code CLI itself, not arbitrary calls to the public
    /// Anthropic Messages API; substituting one for the other here silently produced a token that
    /// looked resolved but didn't actually authenticate real API calls. Only an actual API key
    /// works (MindAttic.Legion 25.0.0 removed this OAuth path entirely for the same reason).
    /// </remarks>
    public static string? DefaultResolveApiKey() =>
        MindAtticCredentialStore.GetKey("claude");

    /// <summary>Pool-aware sibling of <see cref="DefaultResolveApiKey"/> — every key currently
    /// configured under the shared "claude" id, in priority order.</summary>
    public static IReadOnlyList<string> DefaultResolveApiKeys() =>
        MindAtticCredentialStore.GetKeys("claude").Select(k => k.Key).ToList();

    private static IReadOnlyList<string> ResolveApiKey() => DefaultResolveApiKeys();

    private static JsonArray ToAnthropicTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone(),
            });
        }
        return arr;
    }

    private static JsonArray ToAnthropicMessages(IReadOnlyList<ToolLoopMessage> history)
    {
        var messages = new JsonArray();
        foreach (var msg in history)
        {
            switch (msg)
            {
                case ToolLoopMessage.UserText u:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = u.Text } },
                    });
                    break;

                case ToolLoopMessage.AssistantTurn a:
                    var assistantContent = new JsonArray();
                    foreach (var part in a.Parts)
                    {
                        assistantContent.Add(part switch
                        {
                            AssistantPart.Text t => new JsonObject { ["type"] = "text", ["text"] = t.Value },
                            AssistantPart.ToolCall c => new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = c.Id,
                                ["name"] = c.Name,
                                ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson),
                            },
                            _ => throw new InvalidOperationException($"Unknown AssistantPart type: {part.GetType()}"),
                        });
                    }
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
                    break;

                case ToolLoopMessage.ToolResults r:
                    var resultsContent = new JsonArray();
                    foreach (var res in r.Results)
                    {
                        resultsContent.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = res.ToolCallId,
                            ["content"] = res.Content,
                            ["is_error"] = res.IsError,
                        });
                    }
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = resultsContent });
                    break;

                default:
                    throw new InvalidOperationException($"Unknown ToolLoopMessage type: {msg.GetType()}");
            }
        }
        return messages;
    }

    private static List<AssistantPart> FromAnthropicContent(JsonArray content)
    {
        var parts = new List<AssistantPart>();
        foreach (var block in content)
        {
            if (block is null) continue;
            var type = block["type"]?.GetValue<string>();
            if (type == "text")
            {
                var text = block["text"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(text)) parts.Add(new AssistantPart.Text(text));
            }
            else if (type == "tool_use")
            {
                var id = block["id"]?.GetValue<string>() ?? "";
                var name = block["name"]?.GetValue<string>() ?? "";
                var input = block["input"];
                parts.Add(new AssistantPart.ToolCall(id, name, input?.ToJsonString() ?? "{}"));
            }
        }
        return parts;
    }
}

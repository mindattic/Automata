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
    private readonly Func<string?> resolveApiKey;

    public string Name => "Claude";

    public AnthropicToolCallingLlm(AnthropicToolClient client, string model = "claude-opus-4-7")
        : this(client, ResolveApiKey, model) { }

    /// <summary>Test-friendly constructor — injects the API-key resolver instead of reading the
    /// real Claude Code OAuth session / shared credential store.</summary>
    public AnthropicToolCallingLlm(AnthropicToolClient client, Func<string?> resolveApiKey, string model = "claude-opus-4-7")
    {
        this.client = client;
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
        var apiKey = resolveApiKey()
            ?? throw new InvalidOperationException(
                "No Anthropic credentials configured — neither a Claude Code Team OAuth session " +
                "(~/.claude/.credentials.json) nor a 'claude-api' provider key in the credential store.");

        var messages = ToAnthropicMessages(history);
        var toolsArray = ToAnthropicTools(tools);

        var turn = await client.CreateAsync(apiKey, model, systemPrompt, messages, toolsArray, maxTokens, ct);
        return new ToolTurnResult(FromAnthropicContent(turn.Content));
    }

    private static string? ResolveApiKey() =>
        LegionClient.GetClaudeTeamOAuthToken() ?? MindAtticCredentialStore.GetKey("claude-api");

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

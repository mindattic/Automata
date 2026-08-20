using System.Text.Json.Nodes;

namespace Automata.Core.Operator;

/// <summary>
/// Provider-neutral agentic tool-calling contract. <see cref="BrowserOperatorService"/> (and any
/// future operator loop) speaks only this shape — plain text turns, tool calls, tool results —
/// so the loop mechanics never change when the underlying LLM vendor does. Each implementation
/// owns the translation to/from its own wire format: Anthropic's <c>tool_use</c>/<c>tool_result</c>
/// content blocks vs. OpenAI's <c>tool_calls</c>/<c>role:"tool"</c> messages are structurally
/// different envelopes for the same idea, and that translation is exactly what belongs behind
/// this interface. Ported from Prose.KdpPublish's KDP-publishing operator loop.
/// </summary>
public interface IToolCallingLlm
{
    /// <summary>Display name for logging/diagnostics (e.g. "Claude", "OpenAI").</summary>
    string Name { get; }

    /// <summary>True if this provider has usable credentials right now.</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>
    /// One turn: given the system prompt, the full conversation so far, and the tool
    /// catalog, get back everything the assistant wants to do this turn — any text it
    /// said, plus any tool calls it wants made. The caller invokes the tools and appends
    /// a <see cref="ToolLoopMessage.ToolResults"/> message before calling this again.
    /// </summary>
    Task<ToolTurnResult> CreateTurnAsync(
        string systemPrompt,
        IReadOnlyList<ToolLoopMessage> history,
        IReadOnlyList<ToolDefinition> tools,
        int maxTokens,
        CancellationToken ct);
}

/// <summary>One callable tool. <see cref="InputSchema"/> is a standard JSON Schema object —
/// identical content works for both Anthropic's <c>input_schema</c> and OpenAI's
/// <c>parameters</c>, only the enclosing envelope differs per vendor.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonNode InputSchema);

/// <summary>One entry in the conversation history, in whichever order they occurred.</summary>
public abstract record ToolLoopMessage
{
    /// <summary>The initial (or any subsequent) plain-text instruction from the caller.</summary>
    public sealed record UserText(string Text) : ToolLoopMessage;

    /// <summary>Everything the assistant did in one turn — text said and/or tools called.</summary>
    public sealed record AssistantTurn(IReadOnlyList<AssistantPart> Parts) : ToolLoopMessage;

    /// <summary>The results of every tool call from the immediately preceding assistant turn.</summary>
    public sealed record ToolResults(IReadOnlyList<ToolResultPart> Results) : ToolLoopMessage;
}

/// <summary>One piece of an assistant turn.</summary>
public abstract record AssistantPart
{
    public sealed record Text(string Value) : AssistantPart;

    /// <summary><see cref="ArgumentsJson"/> is the tool's arguments as a JSON object string —
    /// Anthropic hands this back as a parsed object (re-serialized here for a uniform shape),
    /// OpenAI hands it back as a JSON string already.</summary>
    public sealed record ToolCall(string Id, string Name, string ArgumentsJson) : AssistantPart;
}

/// <summary>One tool's result, keyed back to the <see cref="AssistantPart.ToolCall"/> that requested it.</summary>
public sealed record ToolResultPart(string ToolCallId, string Content, bool IsError);

/// <summary>Everything the assistant did in the turn just completed.</summary>
public sealed record ToolTurnResult(IReadOnlyList<AssistantPart> Parts);

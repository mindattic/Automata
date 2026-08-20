namespace Automata.Core.Operator;

/// <summary>
/// Streaming event contract for a tool-use loop. Ported verbatim (namespace only) from
/// Prose.KdpPublish's KDP-publishing operator loop — fully generic, no site-specific shape.
/// </summary>
public abstract record OperatorEvent
{
    public sealed record AssistantText(string Text) : OperatorEvent;
    public sealed record ToolStarted(string Name, string ArgsJson) : OperatorEvent;
    public sealed record ToolCompleted(string Name, string ResultJson, bool IsError) : OperatorEvent;
    public sealed record Error(string Message) : OperatorEvent;

    /// <summary>A non-error status line — e.g. a hard-gate check that correctly skipped a step
    /// rather than something that went wrong.</summary>
    public sealed record Info(string Message) : OperatorEvent;
}

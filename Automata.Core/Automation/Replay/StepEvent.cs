namespace Automata.Core.Automation.Replay;

public enum StepStatus
{
    Passed,

    /// <summary>Action or post-condition failed; the run aborts.</summary>
    Failed,

    /// <summary>Passed, but only via a fallback strategy — the step's fingerprint was refreshed
    /// (self-heal) and the task should be re-saved.</summary>
    Healed,

    /// <summary>Not executed (dry run stopped at this commit point).</summary>
    Skipped,
}

/// <summary>
/// Streaming progress contract for a replay run — the deterministic sibling of
/// <see cref="Operator.OperatorEvent"/>, consumed the same way by whatever UI drives the run.
/// </summary>
public abstract record StepEvent
{
    public sealed record RunStarted(string TaskId, string TaskName, ReplayMode Mode) : StepEvent;

    public sealed record StepStarted(string StepId, string Label) : StepEvent;

    public sealed record StepCompleted(string StepId, StepStatus Status, string? Message, string? ExtractedText = null) : StepEvent;

    /// <summary>The step is flagged pauseForUser — the run is parked until Continue.</summary>
    public sealed record StepPaused(string StepId, string Label) : StepEvent;

    public sealed record RunCompleted(bool Success, string Summary) : StepEvent;

    public sealed record Log(string Message) : StepEvent;
}

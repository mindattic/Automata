using Automata.Core.Automation.Execution;

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
    public sealed record RunStarted(string TaskId, string TaskName) : StepEvent;

    public sealed record StepStarted(string StepId, string Label) : StepEvent;

    public sealed record StepCompleted(string StepId, StepStatus Status, string? Message, string? ExtractedText = null) : StepEvent;

    /// <summary>The step is flagged pauseForUser — the run is parked until Continue.</summary>
    public sealed record StepPaused(string StepId, string Label) : StepEvent;

    /// <summary>
    /// The run reached a wait too long to hold a browser through, and has checkpointed instead.
    /// <para>
    /// Terminal for this pass, and NOT a <see cref="RunCompleted"/>: the run has neither passed
    /// nor failed, so a caller that collapsed the two would report a success or a failure that
    /// has not happened yet. A caller that persists the checkpoint gets the run finished later;
    /// one that ignores this event simply loses the rest of the run, which is why both the runner
    /// and the app handle it explicitly.
    /// </para>
    /// </summary>
    public sealed record RunParked(ParkCheckpoint Checkpoint) : StepEvent;

    /// <summary>
    /// What a task declared it publishes, resolved, emitted just before <see cref="RunCompleted"/>.
    /// <para>
    /// This is how one task hands a value to the next: the caller running a collection keeps these
    /// and offers them to the tasks that follow. A task with no declared outputs never emits it,
    /// and a run that parked never reaches it — an unfinished task has published nothing.
    /// </para>
    /// </summary>
    public sealed record TaskPublished(
        string TaskId, string TaskName, IReadOnlyDictionary<string, string> Values) : StepEvent;

    public sealed record RunCompleted(bool Success, string Summary) : StepEvent;

    public sealed record Log(string Message) : StepEvent;
}

using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Execution;

/// <summary>One value a step published, flattened for storage.</summary>
/// <remarks>
/// A list of these rather than the engine's own dictionary, whose keys join a step id and a field
/// name with a NUL. That key shape is right in memory and wrong in storage: it would serialise as
/// <c>"abc\0text"</c> and turn an export someone may well open into a puzzle.
/// </remarks>
public sealed record OutputValue(string StepId, string Field, string Value);

/// <summary>
/// Everything needed to pick a run back up where a long wait left it.
/// <para>
/// Emitted by the workflow engine, which knows the walk, and persisted by whoever owns the run
/// record — the runner or the app. The engine deliberately does not write it: it reports what
/// happened, exactly as it does with every other <see cref="Replay.StepEvent"/>, and the caller
/// decides what that means for the run's identity and storage.
/// </para>
/// </summary>
/// <param name="ResumeAtUtc">When the wait is over and the run may continue.</param>
/// <param name="Reason">Why it parked, in words, for the Runs tab and <c>status</c>.</param>
/// <param name="ResumePath">
/// Index path from the task's root to the wait step that parked — <c>[3]</c> is the fourth
/// top-level step, <c>[3, 1]</c> the second child of it. A path rather than a step id because
/// resuming has to continue with what FOLLOWS the wait, which an id alone cannot locate.
/// </param>
/// <param name="ResumeStepId">
/// The id of the step at that path, checked before resuming. A task edited during the wait would
/// otherwise resume into whatever step now sits at that index.
/// </param>
/// <param name="StepLabel">
/// What that step is called. Stored rather than looked up, so a run record still says what it is
/// waiting on after the step has been renamed — or deleted.
/// </param>
/// <param name="Outputs">Values published before the wait, so bindings still resolve afterwards.</param>
/// <param name="Variables">Row and other variables in scope at the wait.</param>
/// <param name="Passed">Steps already passed, so the resumed run's summary counts the whole run.</param>
/// <param name="Healed">Steps already self-healed, for the same reason.</param>
/// <param name="FreshenedDatasets">
/// Datasets this run has already started fresh (<c>resetOnFirstWrite</c>). Carried across the wait
/// because "first write of the run" has to mean the whole run: a resumed run that forgot them
/// would clear a dataset it had spent the first half of the run filling. Optional so a checkpoint
/// written before this existed still loads.
/// </param>
/// <param name="Inputs">
/// The values the run was started with for the task's declared inputs — the CLI's
/// <c>--input name=value</c>, or a wiring from an earlier task in the collection. Carried for the
/// same reason as the datasets: the tick that resumes the run is a fresh process with no command
/// line of its own, so an input left out here reverts to the input's own default and the half of
/// the run that happens after the wait quietly does something else while reporting success.
/// Optional, so a checkpoint written before this existed still loads.
/// </param>
public sealed record ParkCheckpoint(
    DateTimeOffset ResumeAtUtc,
    string Reason,
    IReadOnlyList<int> ResumePath,
    string ResumeStepId,
    string StepLabel,
    IReadOnlyList<OutputValue> Outputs,
    IReadOnlyDictionary<string, string> Variables,
    int Passed,
    int Healed,
    IReadOnlyList<string>? FreshenedDatasets = null,
    IReadOnlyDictionary<string, string>? Inputs = null);

/// <summary>
/// A run waiting out a long pause with no browser held: its checkpoint plus the identity of the
/// run it belongs to.
/// <para>
/// Kept in its own table rather than on the run record, because the question asked of it is
/// "what is due to resume now?" — asked on every scheduler tick, and best answered by a small table
/// instead of a scan of every run that has ever happened.
/// </para>
/// </summary>
public sealed class ParkedRun
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The run record this continues. Its manifest stays open until the run finishes.</summary>
    public string RunId { get; set; } = "";

    /// <summary>Whether the run was launched against one task or a whole collection.</summary>
    public RunTargetKind Target { get; set; } = RunTargetKind.Task;

    /// <summary>Name of what the run was launched against, for the summary it will eventually write.</summary>
    public string TargetName { get; set; } = "";

    /// <summary>How the original run was started, carried through so a resumed run is not
    /// relabelled "manual" when it finishes.</summary>
    public string Trigger { get; set; } = "manual";

    /// <summary>The task that parked.</summary>
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string CollectionId { get; set; } = "";

    /// <summary>
    /// The tasks still to run after the parked one, in order. A collection whose second task parks
    /// resumes that task and then carries on through the rest — the alternative, refusing to park
    /// inside a collection at all, would make the feature useless for exactly the overnight batch
    /// it exists for.
    /// </summary>
    public List<string> RemainingTaskIds { get; set; } = [];

    /// <summary>Tasks that had already passed before this one, so the final summary counts the
    /// whole run rather than only the part that ran after resuming.</summary>
    public int TasksPassed { get; set; }

    /// <summary>Tasks in the run altogether, for the same reason.</summary>
    public int TotalTasks { get; set; } = 1;

    public DateTimeOffset ParkedAtUtc { get; set; }

    /// <summary>How many times this run has already parked. A task with two long waits parks twice.</summary>
    public int ResumeCount { get; set; }

    public ParkCheckpoint Checkpoint { get; set; } =
        new(default, "", [], "", "", [], new Dictionary<string, string>(), 0, 0);

    public DateTimeOffset ResumeAtUtc => Checkpoint.ResumeAtUtc;
}

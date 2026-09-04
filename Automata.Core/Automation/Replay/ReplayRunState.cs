using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Replay;

/// <summary>
/// Mutable state threaded through one run: what has passed, whether to stop, the values steps have
/// published, and the row variables an enclosing ForEach has in scope.
/// <para>
/// Shared between <see cref="ReplayEngine"/> (which executes one step) and
/// <see cref="Execution.WorkflowEngine"/> (which owns the walk), which is why it lives here rather
/// than nested inside either of them.
/// </para>
/// </summary>
internal sealed class ReplayRunState
{
    public bool Stop;
    public bool Failed;
    public int Passed;
    public int Healed;

    /// <summary>Outcome of the most recent step. The walker reads it to decide whether to descend:
    /// a failed step's children never run, even when ContinueOnStepError keeps its siblings going.</summary>
    public StepStatus? LastStatus;

    /// <summary>Values published by steps that have already run, keyed by step id + output name.
    /// Step ids are GUIDs, so this stays unambiguous even when a RunTask pulls in another task.</summary>
    public readonly Dictionary<string, string> Outputs = new(StringComparer.Ordinal);

    /// <summary>
    /// Row variables from an enclosing ForEach. Each column is published twice — bare
    /// (<c>sku</c>) and qualified (<c>row.sku</c>) — so a nested loop can be disambiguated by
    /// naming its row variable while the common single-loop case stays short.
    /// </summary>
    public readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal);

    /// <summary>Task ids currently on the RunTask stack, so a task cannot invoke itself forever.</summary>
    public readonly HashSet<string> TaskStack = new(StringComparer.Ordinal);

    /// <summary>
    /// Set when a long wait checkpointed the run instead of holding a browser through it. Non-null
    /// means the walk stopped early and the run is neither passed nor failed — it is unfinished.
    /// </summary>
    public Execution.ParkCheckpoint? Parked;

    public static string OutputKey(string? stepId, string? field) => stepId + "\0" + field;

    /// <summary>The inverse of <see cref="OutputKey"/>, for writing outputs somewhere readable.</summary>
    public static (string StepId, string Field) SplitOutputKey(string key)
    {
        var separator = key.IndexOf('\0');
        return separator < 0 ? (key, "") : (key[..separator], key[(separator + 1)..]);
    }

    /// <summary>
    /// A child scope for one iteration of a for-each: it can read everything published before the
    /// loop, and its own writes stay local.
    /// <para>
    /// Isolation applies whether the loop runs one row at a time or many, on purpose. If a row's
    /// outputs leaked out sequentially but could not in parallel, raising the concurrency of a
    /// working loop would change its results — which is the kind of surprise that makes people
    /// distrust the concurrency setting entirely.
    /// </para>
    /// </summary>
    public ReplayRunState ForkForRow(string rowVariable, IReadOnlyDictionary<string, string> row)
    {
        var child = new ReplayRunState();
        foreach (var (key, value) in Outputs) child.Outputs[key] = value;
        foreach (var (key, value) in Variables) child.Variables[key] = value;
        foreach (var taskId in TaskStack) child.TaskStack.Add(taskId);

        // Each column twice: bare for the common single loop, qualified so a nested loop can be
        // disambiguated by naming its row variable.
        foreach (var (column, value) in row)
        {
            child.Variables[column] = value;
            child.Variables[rowVariable + "." + column] = value;
        }
        return child;
    }

    /// <summary>Folds a finished row's tallies back into the loop that spawned it.</summary>
    public void MergeFrom(ReplayRunState child)
    {
        Passed += child.Passed;
        Healed += child.Healed;
        if (child.Failed) Failed = true;
        if (child.Stop) Stop = true;
    }
}

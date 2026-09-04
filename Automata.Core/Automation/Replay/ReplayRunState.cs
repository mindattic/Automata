using System.Globalization;
using Automata.Core.Automation.Data;
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
    public ReplayRunState() : this(new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { }

    private ReplayRunState(HashSet<string> freshened) => this.freshened = freshened;

    public bool Stop;
    public bool Failed;
    public int Passed;
    public int Healed;

    /// <summary>Outcome of the most recent step. The walker reads it to decide whether to descend:
    /// a failed step's children never run, even when ContinueOnStepError keeps its siblings going.</summary>
    public StepStatus? LastStatus;

    /// <summary>
    /// What each <see cref="StepAction.If"/> decided, keyed by ITS OWN step id.
    /// <para>
    /// Keyed rather than "the last one", because an <c>if</c> runs its children before the walker
    /// looks at the answer — so a nested <c>if</c> inside the then-branch would be the last one to
    /// have decided anything, and the outer <c>else</c> would pair with the wrong verdict entirely.
    /// </para>
    /// </summary>
    public readonly Dictionary<string, bool> IfVerdicts = new(StringComparer.Ordinal);

    /// <summary>
    /// What the step IMMEDIATELY BEFORE the one now running decided, when that step was an
    /// <see cref="StepAction.If"/>. Null in every other case, including two steps after an if —
    /// which is what keeps an <see cref="StepAction.Else"/> paired with its own if rather than
    /// with whichever one happened to run last.
    /// </summary>
    public bool? PreviousIfHeld;

    /// <summary>
    /// The id of that <see cref="StepAction.If"/>, so an <see cref="StepAction.Else"/> can check it
    /// is the one it was actually written for and not merely the one that ended up in front of it.
    /// </summary>
    public string? PreviousIfId;

    /// <summary>Values published by steps that have already run, keyed by step id + output name.
    /// Step ids are GUIDs, so this stays unambiguous even when a RunTask pulls in another task.</summary>
    public readonly Dictionary<string, string> Outputs = new(StringComparer.Ordinal);

    /// <summary>
    /// True inside a for-each row. It is what separates "this binding is in the wrong place" from
    /// "this row does not carry that column" — the same absent value, two entirely different
    /// mistakes, and only one of them has 'exists' as its answer.
    /// </summary>
    public bool InRowScope;

    /// <summary>
    /// Row variables from an enclosing ForEach. Each column is published twice — bare
    /// (<c>sku</c>) and qualified (<c>row.sku</c>) — so a nested loop can be disambiguated by
    /// naming its row variable while the common single-loop case stays short.
    /// <para>
    /// The row's POSITION joins them under the name <c>#</c>, published both ways for the same
    /// reason. It is not a column — nothing can read it off the dataset — but a binding should not
    /// have to know that, and every binding written anywhere names a column bare.
    /// </para>
    /// </summary>
    public readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal);

    /// <summary>
    /// The row each enclosing for-each is on, as JSON, keyed by the dataset it came from.
    /// <para>
    /// Keyed rather than "the current one" so a whole-row binding inside a nested loop can name
    /// which loop it means — the same disambiguation <c>row.sku</c> gets from its row variable.
    /// </para>
    /// </summary>
    public readonly Dictionary<string, string> Rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The innermost of those, so a binding that names no dataset still resolves to the
    /// loop it is written inside.</summary>
    public string? InnermostRow;

    /// <summary>
    /// The current row of a named dataset, or of the innermost loop when the binding names none.
    /// False means there is no such row — which is a different answer from a broken binding, and
    /// the caller reports it as one.
    /// </summary>
    public bool TryRow(string? datasetName, out string? json)
    {
        if (string.IsNullOrWhiteSpace(datasetName))
        {
            json = InnermostRow;
            return json != null;
        }
        return Rows.TryGetValue(datasetName, out json);
    }

    /// <summary>Task ids currently on the RunTask stack, so a task cannot invoke itself forever.</summary>
    public readonly HashSet<string> TaskStack = new(StringComparer.Ordinal);

    /// <summary>
    /// The inputs of the task currently running. A STACK, not a dictionary: a called task's inputs
    /// are its own, and letting them leak back to the caller — or the caller's leak in — would make
    /// the same binding mean different things depending on who called whom.
    /// </summary>
    private readonly List<Dictionary<string, string>> inputScopes =
        [new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The value of a named input, or null when nothing supplied one.</summary>
    public string? Input(string name) =>
        inputScopes[^1].TryGetValue(name, out var value) ? value : null;

    /// <summary>Enters a called task's own input scope. Every push needs its <see cref="PopInputs"/>.</summary>
    public void PushInputs(IReadOnlyDictionary<string, string> values) =>
        inputScopes.Add(new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));

    public void PopInputs()
    {
        // Never the last one: the outermost scope is the run's own inputs, and a task with none
        // still has to have somewhere to look.
        if (inputScopes.Count > 1) inputScopes.RemoveAt(inputScopes.Count - 1);
    }

    /// <summary>Sets the inputs of the task the run STARTED with — the CLI's --input, or the
    /// defaults the task declared.</summary>
    public void SetRunInputs(IReadOnlyDictionary<string, string> values)
    {
        inputScopes[0].Clear();
        foreach (var (name, value) in values) inputScopes[0][name] = value;
    }

    /// <summary>
    /// The zoom a <see cref="StepAction.SetZoom"/> step asked for, as a percentage. Held on the run
    /// rather than on the page because a navigation wipes the page's own zoom, and a task that
    /// zoomed out to see a wide layout means it for the pages that follow too — being silently
    /// returned to 100% by a link is the surprise this avoids.
    /// </summary>
    public int ZoomPercent = 100;

    /// <summary>
    /// Datasets a "start fresh each run" write has already claimed. <b>Shared by reference with
    /// every forked row state</b> — deliberately the one thing a fork does not isolate, because
    /// "has this run started this dataset yet?" is a question about the run and there is no other
    /// scope that can answer it. Guarded, since a parallel for-each asks from several lanes at once.
    /// </summary>
    private readonly HashSet<string> freshened;

    /// <summary>
    /// Set when a long wait checkpointed the run instead of holding a browser through it. Non-null
    /// means the walk stopped early and the run is neither passed nor failed — it is unfinished.
    /// </summary>
    public Execution.ParkCheckpoint? Parked;

    /// <summary>
    /// True the FIRST time this run is asked about a dataset and false every time after, including
    /// from another row running at the same moment. The caller acts on it, so it is a claim rather
    /// than a question — asking twice would report two firsts.
    /// </summary>
    public bool ClaimFirstWrite(string datasetName)
    {
        lock (freshened) return freshened.Add(datasetName);
    }

    /// <summary>The datasets claimed so far, for a checkpoint to carry across a park.</summary>
    public IReadOnlyList<string> ClaimedDatasets()
    {
        lock (freshened) return [.. freshened];
    }

    /// <summary>Re-seeds the claims a parked run had already made. A resumed run that forgot them
    /// would clear a dataset it had spent the first half of the run filling.</summary>
    public void RestoreClaims(IEnumerable<string>? names)
    {
        if (names == null) return;
        lock (freshened) foreach (var name in names) freshened.Add(name);
    }

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
    public ReplayRunState ForkForRow(
        string rowVariable, IReadOnlyDictionary<string, string> row, int rowNumber, string? datasetName)
    {
        // The freshened set is passed by reference, not copied: it is the one thing rows must
        // agree on, or every row would think itself the first and clear the dataset again.
        var child = new ReplayRunState(freshened);
        child.InRowScope = true;
        foreach (var (key, value) in Outputs) child.Outputs[key] = value;
        foreach (var (key, value) in Variables) child.Variables[key] = value;
        foreach (var (name, json) in Rows) child.Rows[name] = json;
        foreach (var taskId in TaskStack) child.TaskStack.Add(taskId);

        // Before the columns, so a dataset that really has a column called "#" overrides it. The
        // row's own data is what the author is looking at; the position is this loop's bookkeeping,
        // and losing an argument to a real column is the right way round.
        var position = rowNumber.ToString(CultureInfo.InvariantCulture);
        child.Variables[ForEachSpec.RowNumberKey] = position;
        child.Variables[rowVariable + "." + ForEachSpec.RowNumberKey] = position;

        // Each column twice: bare for the common single loop, qualified so a nested loop can be
        // disambiguated by naming its row variable.
        foreach (var (column, value) in row)
        {
            child.Variables[column] = value;
            child.Variables[rowVariable + "." + column] = value;
        }

        var rowJson = DatasetIO.RowJson(row);
        child.InnermostRow = rowJson;
        if (!string.IsNullOrWhiteSpace(datasetName)) child.Rows[datasetName] = rowJson;
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

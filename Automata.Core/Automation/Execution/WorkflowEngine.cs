using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Execution;

/// <summary>
/// Runs a task the same way <see cref="ReplayEngine"/> does, except that it owns the tree walk and
/// therefore can execute the control-flow steps — <c>if</c>, <c>forEach</c>, <c>runTask</c>,
/// <c>writeDataset</c>, and a wait on a condition.
/// <para>
/// The split is deliberate. A control-flow step decides for itself whether and how many times its
/// children run, which a walker embedded in the step executor cannot express. So the executor
/// (<see cref="ReplayEngine.ExecuteOneAsync"/>) does exactly one step — pause gate, bindings,
/// retry, masking, bookkeeping — and everything about ordering lives here. Nothing is duplicated.
/// </para>
/// <para>
/// It emits the same <see cref="StepEvent"/> stream as a plain replay, so it is a drop-in
/// replacement for the caller. Lane-level events arrive with the lane pool, not before there is
/// more than one lane to talk about.
/// </para>
/// </summary>
public sealed partial class WorkflowEngine
{
    private readonly ReplayEngine replay;
    private readonly CollectionStore collections;
    private readonly DatasetStore datasets;
    private readonly ILogger<WorkflowEngine> log;

    /// <summary>
    /// Everything one branch of a walk needs. Passed down rather than held in a field because the
    /// engine is a singleton: two runs can be in flight at once, and a parallel for-each swaps the
    /// browser per row, so this must not be shared mutable state.
    /// </summary>
    private sealed record RunScope(ReplayOptions Options, IBrowserSurface Browser, BrowserLanePool? Lanes);

    /// <summary>
    /// Where in the tree the walk is, and what it is allowed to do there.
    /// </summary>
    /// <param name="Path">
    /// Index path from the task's root to the step being walked. Only exists so a wait that parks
    /// can write down an address the resumed run can find its way back to.
    /// </param>
    /// <param name="Resume">
    /// While fast-forwarding to a parked wait, the remaining path to it — relative to the list
    /// currently being walked. Null during an ordinary walk.
    /// </param>
    /// <param name="CanPark">
    /// False inside a for-each or a called task. A checkpoint is one address plus one set of
    /// values; it cannot say which of a loop's rows had finished, and resuming a called task would
    /// need the caller's position too. Rather than resume approximately — re-running rows or
    /// skipping them — a wait in those places holds its browser and says so.
    /// </param>
    private sealed record Walk(IReadOnlyList<int> Path, IReadOnlyList<int>? Resume, bool CanPark)
    {
        public static Walk Root(IReadOnlyList<int>? resume) => new([], resume, true);

        /// <summary>The walk for the step at <paramref name="index"/> of the current list.</summary>
        public Walk Into(int index, IReadOnlyList<int>? resume) => new([.. Path, index], resume, CanPark);

        /// <summary>The walk for children whose position a checkpoint could not describe.</summary>
        public Walk Unparkable() => this with { Resume = null, CanPark = false };
    }

    public WorkflowEngine(
        ReplayEngine replay,
        CollectionStore collections,
        DatasetStore datasets,
        ILogger<WorkflowEngine>? log = null)
    {
        this.replay = replay;
        this.collections = collections;
        this.datasets = datasets;
        this.log = log ?? NullLogger<WorkflowEngine>.Instance;
    }

    /// <summary>
    /// Runs a task. <paramref name="lanes"/> is what makes a for-each able to run rows at once:
    /// without a pool there is one browser, so every loop runs in order and says so.
    /// <para>
    /// <paramref name="resume"/> continues a run that parked on a long wait: its values are
    /// restored and the walk fast-forwards to the step after the wait. The browser is NOT restored
    /// — there is none to restore, that being the point of parking — so the page starts again from
    /// the task's start URL, and the run says so rather than letting it be discovered.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<StepEvent> RunAsync(
        TaskDefinition task,
        ReplayOptions options,
        IBrowserSurface browser,
        [EnumeratorCancellation] CancellationToken ct = default,
        BrowserLanePool? lanes = null,
        ParkCheckpoint? resume = null)
    {
        var scope = new RunScope(options, browser, lanes);
        yield return new StepEvent.RunStarted(task.Id, task.Name);
        var state = new ReplayRunState();
        state.TaskStack.Add(task.Id);
        state.SetRunInputs(ReplayEngine.SeedInputs(task, options.Inputs));

        if (resume != null)
        {
            // Checked before anything runs: the task may have been edited during a wait that
            // lasted hours, and resuming into whatever step now occupies that index would run the
            // wrong thing while claiming to have resumed correctly.
            var parkedAt = ResolveByPath(task.Steps, resume.ResumePath);
            if (parkedAt == null || !string.Equals(parkedAt.Id, resume.ResumeStepId, StringComparison.Ordinal))
            {
                yield return new StepEvent.RunCompleted(false,
                    $"Cannot resume: the step '{task.Name}' parked on is no longer at that position — " +
                    "the task was edited while it waited.");
                yield break;
            }

            Restore(state, resume);
            yield return new StepEvent.Log(
                $"Resuming after {resume.Reason} — {resume.Passed} step(s) had already passed. " +
                (string.IsNullOrWhiteSpace(task.StartUrl)
                    ? "The browser was released while it waited, so this run starts on a blank page."
                    : "The browser was released while it waited, so the page starts again from the start URL — " +
                      "anything done to the page before the wait no longer applies."));
        }

        if (!string.IsNullOrWhiteSpace(task.StartUrl))
        {
            yield return new StepEvent.Log($"Navigating to start URL {task.StartUrl}");
            var navError = await ReplayEngine.TryNavigateAsync(browser, task.StartUrl!, ct);
            if (navError != null)
            {
                yield return new StepEvent.RunCompleted(false, $"Start URL failed: {navError}");
                yield break;
            }
        }

        await foreach (var evt in RunStepsAsync(task.Steps, scope, state, Walk.Root(resume?.ResumePath), ct))
            yield return evt;

        // A parked run is neither passed nor failed — it is unfinished. RunParked was already
        // emitted where it happened; completing it here would tell the caller an outcome that has
        // not occurred.
        if (state.Parked != null) yield break;

        var success = !state.Failed;
        var summary =
            state.Failed ? $"Failed after {state.Passed} passed step(s)." :
            state.Healed > 0 ? $"{state.Passed} step(s) passed, {state.Healed} self-healed — task should be re-saved." :
            $"{state.Passed} step(s) passed.";
        yield return new StepEvent.RunCompleted(success, summary);
    }

    private async IAsyncEnumerable<StepEvent> RunStepsAsync(
        IReadOnlyList<Step> steps,
        RunScope scope,
        ReplayRunState state,
        Walk walk,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Whether the `if` immediately before the step being walked held — the ONE piece of state
        // an `else` needs, and it is local to this list because that is exactly how far the pairing
        // reaches. Null means the step before was not an `if` at all, which is what makes an
        // orphaned `else` a failure with a name rather than a branch that quietly never runs.
        bool? previousIfHeld = null;

        // While resuming, everything before the parked wait's branch already ran in an earlier
        // pass. -1 during an ordinary walk, which starts at 0 and skips nothing.
        var resumeIndex = walk.Resume is { Count: > 0 } ? walk.Resume[0] : -1;

        for (var index = 0; index < steps.Count; index++)
        {
            if (index < resumeIndex) continue;

            var tail = index == resumeIndex ? walk.Resume!.Skip(1).ToList() : null;

            // The end of the resume path IS the wait that parked. Its time is up by definition —
            // that is why this pass is running — so it is finished, not re-run.
            if (tail is { Count: 0 })
            {
                yield return new StepEvent.Log($"'{steps[index].Label}' is over — carrying on from here.");
                continue;
            }

            state.PreviousIfHeld = previousIfHeld;
            await foreach (var evt in RunStepAsync(steps[index], scope, state, walk.Into(index, tail), ct))
                yield return evt;
            previousIfHeld = steps[index].Action == StepAction.If
                && state.IfVerdicts.TryGetValue(steps[index].Id, out var verdict) ? verdict : null;
            if (state.Stop) yield break;
        }
    }

    private async IAsyncEnumerable<StepEvent> RunStepAsync(
        Step step,
        RunScope scope,
        ReplayRunState state,
        Walk walk,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Still fast-forwarding: the parked wait is somewhere inside this step. Its own action is
        // deliberately not re-performed — an `if` already decided, a group already opened, and
        // re-running either would double-count the step or act on the page a second time.
        if (walk.Resume is { Count: > 0 })
        {
            if (step.Action is StepAction.ForEach or StepAction.RunTask)
            {
                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Failed,
                    $"cannot resume inside a {step.Action} step — a checkpoint cannot describe where in it the run had got to", null);
                state.Failed = true;
                state.Stop = true;
                yield break;
            }
            await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                yield return evt;
            yield break;
        }

        if (step.Action == StepAction.Wait)
        {
            await foreach (var evt in TryParkAsync(step, scope, state, walk))
                yield return evt;
            if (state.Parked != null || state.Stop) yield break;
        }

        if (!IsOrchestrated(step))
        {
            await foreach (var evt in replay.ExecuteOneAsync(step, scope.Options, scope.Browser, state, ct))
                yield return evt;
            if (state.Stop || state.LastStatus == StepStatus.Failed) yield break;

            await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                yield return evt;
            yield break;
        }

        // Control-flow steps honour the pause gate too; the step executor does it for the rest.
        if (step.PauseForUser || step.Id == scope.Options.PauseBeforeStepId)
        {
            yield return new StepEvent.StepPaused(step.Id, step.Label);
            await scope.Options.Control.WaitAsync(ct);
        }
        yield return new StepEvent.StepStarted(step.Id, step.Label);

        await foreach (var evt in RunControlFlowAsync(step, scope, state, walk, ct))
            yield return evt;
    }

    /// <summary>
    /// Decides what to do about a wait: park the run, or hold the browser and say why it must.
    /// Sets <see cref="ReplayRunState.Parked"/> when it parks; leaves it null when the wait should
    /// just be performed by whoever handles it next.
    /// </summary>
    private async IAsyncEnumerable<StepEvent> TryParkAsync(
        Step step, RunScope scope, ReplayRunState state, Walk walk)
    {
        var spec = step.Wait ?? new WaitSpec();
        var (plan, error) = WaitPlan.For(spec, scope.Options.Clock());
        // A spec error, or a wait with no knowable end (a condition or a signal), is not this
        // method's business — the executor reports the error and the orchestrator polls the
        // condition.
        if (error != null || plan == null || !WaitPlan.ShouldPark(spec, plan.Remaining))
        {
            await Task.CompletedTask;
            yield break;
        }

        if (!scope.Options.AllowParking)
        {
            yield return new StepEvent.Log(
                $"'{step.Label}' waits {Describe(plan.Remaining)}, and this run cannot be resumed later — " +
                "it holds its browser for the whole wait.");
            yield break;
        }
        if (!walk.CanPark)
        {
            yield return new StepEvent.Log(
                $"'{step.Label}' waits {Describe(plan.Remaining)} inside a loop or a called task, so it holds " +
                "its browser throughout — parking can only checkpoint a wait at the task's own level.");
            yield break;
        }

        yield return new StepEvent.StepStarted(step.Id, step.Label);
        state.Parked = Checkpoint(state, plan, walk.Path, step);
        yield return new StepEvent.Log(
            $"Parked until {plan.EndsAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} — the browser is released for " +
            $"the {Describe(plan.Remaining)} wait, and a later scheduler tick picks the run back up.");
        yield return new StepEvent.RunParked(state.Parked);
        state.Stop = true;
    }

    private static ParkCheckpoint Checkpoint(
        ReplayRunState state, WaitPlan.Plan plan, IReadOnlyList<int> path, Step step) =>
        new(plan.EndsAtUtc,
            $"a wait {plan.Description}",
            [.. path],
            step.Id,
            step.Label,
            state.Outputs.Select(entry =>
            {
                var (id, field) = ReplayRunState.SplitOutputKey(entry.Key);
                return new OutputValue(id, field, entry.Value);
            }).ToList(),
            new Dictionary<string, string>(state.Variables, StringComparer.Ordinal),
            state.Passed,
            state.Healed,
            state.ClaimedDatasets());

    private static void Restore(ReplayRunState state, ParkCheckpoint checkpoint)
    {
        foreach (var output in checkpoint.Outputs)
            state.Outputs[ReplayRunState.OutputKey(output.StepId, output.Field)] = output.Value;
        foreach (var (name, value) in checkpoint.Variables) state.Variables[name] = value;
        state.RestoreClaims(checkpoint.FreshenedDatasets);
        state.Passed = checkpoint.Passed;
        state.Healed = checkpoint.Healed;
    }

    /// <summary>The step an index path names, or null when the path no longer fits the tree.</summary>
    internal static Step? ResolveByPath(IReadOnlyList<Step> steps, IReadOnlyList<int> path)
    {
        if (path.Count == 0) return null;
        var list = steps;
        Step? current = null;
        foreach (var index in path)
        {
            if (index < 0 || index >= list.Count) return null;
            current = list[index];
            list = current.Children;
        }
        return current;
    }

    private static string Describe(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{span.TotalHours:0.#}h";
        return $"{span.TotalDays:0.#}d";
    }

    private static bool IsOrchestrated(Step step) =>
        step.Action is StepAction.If or StepAction.Else or StepAction.ForEach or StepAction.RunTask
            or StepAction.WriteDataset or StepAction.ExtractAll or StepAction.Aggregate
        || (step.Action == StepAction.Wait
            && step.Wait?.Mode is WaitMode.UntilCondition or WaitMode.UntilSignal);

    private async IAsyncEnumerable<StepEvent> RunControlFlowAsync(
        Step step,
        RunScope scope,
        ReplayRunState state,
        Walk walk,
        [EnumeratorCancellation] CancellationToken ct)
    {
        switch (step.Action)
        {
            case StepAction.If:
            {
                var (holds, error) = Evaluate(step.Condition, state);
                if (error != null)
                {
                    // No verdict, so an `else` after this one has nothing to be the other half of.
                    state.IfVerdicts.Remove(step.Id);
                    await foreach (var e in FailAsync(step, scope, state, error)) yield return e;
                    yield break;
                }

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    holds ? "condition holds — running substeps" : "condition does not hold — substeps skipped", null);
                state.LastStatus = StepStatus.Passed;
                state.IfVerdicts[step.Id] = holds;
                state.Passed++;
                if (!holds) yield break;

                // An `if` keeps the walk parkable: it is one branch taken once, so a wait inside
                // it still has an address a checkpoint can name and a resume can re-enter.
                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
                yield break;
            }

            case StepAction.Else:
            {
                if (state.PreviousIfHeld is not { } held)
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        "an 'otherwise' has to come straight after an 'if' — there is none before this one"))
                        yield return e;
                    yield break;
                }

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    held ? "the 'if' before this held — substeps skipped" : "the 'if' before this did not hold — running substeps",
                    null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;
                if (held) yield break;

                // Parkable for the same reason an `if` is: one branch, taken once, at an address a
                // checkpoint can name.
                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
                yield break;
            }

            case StepAction.ForEach:
            {
                var spec = step.ForEach;
                var name = spec?.Source?.DatasetName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    await foreach (var e in FailAsync(step, scope, state, "no dataset chosen")) yield return e;
                    yield break;
                }
                if (!datasets.Exists(name))
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        $"dataset '{name}' not found in {datasets.RootPath}")) yield return e;
                    yield break;
                }

                var rows = datasets.Read(name);
                var spec2 = spec!;
                var ceiling = scope.Options.EffectiveFor(step).MaxConcurrency;

                // Two gates have to open: the resolved ceiling (a machine-resource decision, and
                // the reason one task cannot starve the box) and the loop's own request. Saying
                // which one is closed beats silently running in order.
                if (spec2.MaxConcurrency > 1 && scope.Lanes == null)
                {
                    yield return new StepEvent.Log(
                        $"'{step.Label}' asks for {spec2.MaxConcurrency} rows at once, but this run has a single browser — running them in order.");
                }
                else if (spec2.MaxConcurrency > 1 && ceiling <= 1)
                {
                    yield return new StepEvent.Log(
                        $"'{step.Label}' asks for {spec2.MaxConcurrency} rows at once, but Max concurrency resolves to 1 here — raise it in settings to let rows run together.");
                }

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    $"{rows.Count} row(s) from {name}", null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;

                var rowVar = string.IsNullOrWhiteSpace(spec2.RowVariableName) ? "row" : spec2.RowVariableName;
                var effective = scope.Options.EffectiveFor(step);
                var wanted = Math.Min(Math.Max(1, spec2.MaxConcurrency), effective.MaxConcurrency);

                if (wanted > 1 && scope.Lanes != null)
                {
                    await foreach (var evt in RunRowsInParallelAsync(step, rows, rowVar, wanted, effective, scope, state, walk, ct))
                        yield return evt;
                    yield break;
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new StepEvent.Log($"{step.Label}: row {i + 1} of {rows.Count}");

                    var rowState = state.ForkForRow(rowVar, rows[i]);
                    await foreach (var evt in RunStepsAsync(step.Children, scope, rowState, walk.Unparkable(), ct))
                        yield return evt;
                    state.MergeFrom(rowState);
                    if (state.Stop) yield break;
                }
                yield break;
            }

            case StepAction.RunTask:
            {
                var target = string.IsNullOrWhiteSpace(step.RunTaskId) ? null : collections.GetTask(step.RunTaskId!);
                if (target == null)
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        $"task '{step.RunTaskId}' not found")) yield return e;
                    yield break;
                }
                if (!state.TaskStack.Add(target.Id))
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        $"'{target.Name}' is already running further up the stack — a task cannot invoke itself")) yield return e;
                    yield break;
                }

                // Resolved in the CALLER's scope, before the callee's is entered — that is what
                // lets one task hand another something it read off a page, or pass its own input
                // straight through.
                var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, binding) in step.RunTaskInputs ?? [])
                {
                    var (value, inputError) = BindingResolver.Resolve(binding, state);
                    if (inputError != null)
                    {
                        state.TaskStack.Remove(target.Id);
                        await foreach (var e in FailAsync(step, scope, state,
                            $"input '{name}': {inputError}")) yield return e;
                        yield break;
                    }
                    supplied[name] = value ?? "";
                }

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed, $"running '{target.Name}'", null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;
                state.PushInputs(ReplayEngine.SeedInputs(target, supplied));
                try
                {
                    // Another task's tree, so an index path from THIS task's root would not
                    // address anything in it — a wait in there cannot park.
                    await foreach (var evt in RunStepsAsync(target.Steps, scope, state, walk.Unparkable(), ct))
                        yield return evt;
                }
                finally
                {
                    state.PopInputs();
                    state.TaskStack.Remove(target.Id);
                }
                yield break;
            }

            case StepAction.WriteDataset:
            {
                var spec = step.WriteDataset;
                if (spec == null || string.IsNullOrWhiteSpace(spec.DatasetName))
                {
                    await foreach (var e in FailAsync(step, scope, state, "no dataset name set")) yield return e;
                    yield break;
                }

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (column, binding) in spec.Columns)
                {
                    var (value, error) = BindingResolver.Resolve(binding, state);
                    if (error != null)
                    {
                        await foreach (var e in FailAsync(step, scope, state, $"column '{column}': {error}")) yield return e;
                        yield break;
                    }
                    row[column] = value ?? "";
                }

                // The claim is made inside the dataset's write lock, not here, so two rows
                // finishing at once cannot both be told they are the first writer of the run.
                var startedFresh = false;
                datasets.Write(spec.DatasetName, [row], spec.Append,
                    spec.ResetOnFirstWrite
                        ? () => startedFresh = state.ClaimFirstWrite(spec.DatasetName)
                        : null);

                var what =
                    startedFresh ? $"started {spec.DatasetName} fresh for this run and wrote the first row" :
                    spec.Append ? $"appended to {spec.DatasetName}" :
                    $"wrote {spec.DatasetName}";
                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed, what, null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;
                yield break;
            }

            case StepAction.Aggregate:
            {
                var spec = step.Aggregate;
                if (spec == null || string.IsNullOrWhiteSpace(spec.DatasetName))
                {
                    await foreach (var e in FailAsync(step, scope, state, "no dataset chosen")) yield return e;
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(spec.ColumnName))
                {
                    await foreach (var e in FailAsync(step, scope, state, "no column chosen")) yield return e;
                    yield break;
                }
                if (!datasets.Exists(spec.DatasetName))
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        $"dataset '{spec.DatasetName}' not found in {datasets.RootPath}")) yield return e;
                    yield break;
                }

                // A column that is not there at all is a mistake, not an empty result. Counting it
                // as zero would answer a question nobody asked and look like a working step.
                var columns = datasets.Columns(spec.DatasetName);
                if (!columns.Contains(spec.ColumnName, StringComparer.Ordinal))
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        $"'{spec.DatasetName}' has no column '{spec.ColumnName}' — it has {string.Join(", ", columns)}"))
                        yield return e;
                    yield break;
                }

                var (text, aggError) = Reduce(datasets.Read(spec.DatasetName), spec);
                if (aggError != null)
                {
                    await foreach (var e in FailAsync(step, scope, state, aggError)) yield return e;
                    yield break;
                }

                // Fixed name, like a harvest's "count": one step, one answer, nothing to type and
                // nothing for a binding to get wrong.
                state.Outputs[ReplayRunState.OutputKey(step.Id, AggregateOutput)] = text!;
                foreach (var output in step.Outputs ?? [])
                    if (!string.IsNullOrWhiteSpace(output.Name))
                        state.Outputs[ReplayRunState.OutputKey(step.Id, output.Name)] = text!;

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    $"{spec.Op.ToString().ToLowerInvariant()} of {spec.ColumnName} in {spec.DatasetName}", text);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;
                yield break;
            }

            case StepAction.ExtractAll:
            {
                var spec = step.Harvest;
                var result = await HarvestRunner.RunAsync(scope.Browser, spec!, ct);

                // A harvest that read nothing usable is a failure, not an empty success. Writing an
                // empty dataset here would let the ForEach that consumes it loop zero times and
                // report a clean pass, which is the most expensive kind of wrong this engine can be.
                if (!result.Ok)
                {
                    await foreach (var e in FailAsync(step, scope, state, result.Error ?? "harvest failed"))
                        yield return e;
                    yield break;
                }

                datasets.Write(spec!.DatasetName, result.Rows, spec.Append);

                // Published so an `if` can branch on how much was found, and so a later step can
                // name the dataset without the author retyping it.
                state.Outputs[ReplayRunState.OutputKey(step.Id, "count")] =
                    result.Rows.Count.ToString();
                state.Outputs[ReplayRunState.OutputKey(step.Id, "dataset")] = spec.DatasetName;

                var dropped = result.MatchedRows - result.Rows.Count;
                var note = dropped > 0 ? $" ({dropped} duplicate or excess row(s) dropped)" : "";
                var partial = result.EmptyFields.Count > 0
                    ? $"; nothing found for {string.Join(", ", result.EmptyFields)}"
                    : "";
                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    $"harvested {result.Rows.Count} row(s) into {spec.DatasetName}{note}{partial}", null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;

                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
                yield break;
            }

            default:
            {
                // A wait on a condition. Signals belong to the scheduler — nothing produces one yet,
                // so offering it here would be offering a wait that never ends.
                var spec = step.Wait!;
                if (spec.Mode == WaitMode.UntilSignal)
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        "waiting for a signal needs the scheduler, which is not built yet")) yield return e;
                    yield break;
                }

                var pollMs = Math.Max(50, spec.PollMs);
                var deadline = spec.TimeoutMs is > 0 ? Environment.TickCount64 + spec.TimeoutMs.Value : (long?)null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var (holds, error) = Evaluate(spec.Condition, state);
                    if (error != null)
                    {
                        await foreach (var e in FailAsync(step, scope, state, error)) yield return e;
                        yield break;
                    }
                    if (holds)
                    {
                        yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed, "condition met", null);
                        state.LastStatus = StepStatus.Passed;
                        state.Passed++;
                        yield break;
                    }
                    if (deadline != null && Environment.TickCount64 >= deadline)
                    {
                        await foreach (var e in FailAsync(step, scope, state,
                            $"condition still not met after {spec.TimeoutMs}ms")) yield return e;
                        yield break;
                    }
                    await Task.Delay(pollMs, ct);
                }
            }
        }
    }

    /// <summary>
    /// Runs a for-each's rows concurrently, each on its own browser lane.
    /// <para>
    /// The rows produce events at the same time, so they are funnelled through a channel rather
    /// than interleaved by hand — an async iterator can only be driven by one consumer, and this
    /// keeps the caller's stream exactly the shape it already was.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<StepEvent> RunRowsInParallelAsync(
        Step step,
        IReadOnlyList<Dictionary<string, string>> rows,
        string rowVariable,
        int wanted,
        ResolvedSettings effective,
        RunScope scope,
        ReplayRunState state,
        Walk walk,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new StepEvent.Log(
            $"{step.Label}: running {rows.Count} row(s), up to {wanted} at a time");

        var channel = Channel.CreateUnbounded<StepEvent>();
        var gate = new SemaphoreSlim(wanted, wanted);
        var finished = new List<ReplayRunState>();
        var pool = scope.Lanes!;

        var work = rows.Select((row, index) => Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var rowState = state.ForkForRow(rowVariable, row);
                await using var lease = await pool.AcquireAsync(
                    effective.BrowserProfile ?? "default", taskName: step.Label, ct: ct);
                lease.Describe($"row {index + 1} of {rows.Count}");

                await channel.Writer.WriteAsync(
                    new StepEvent.Log($"{step.Label}: row {index + 1} of {rows.Count} started on lane {lease.LaneId}"), ct);

                await foreach (var evt in RunStepsAsync(
                    step.Children, scope with { Browser = lease.Surface }, rowState, walk.Unparkable(), ct))
                    await channel.Writer.WriteAsync(evt, ct);

                lock (finished) finished.Add(rowState);
            }
            finally
            {
                gate.Release();
            }
        }, ct)).ToList();

        var drain = Task.Run(async () =>
        {
            try { await Task.WhenAll(work); }
            finally { channel.Writer.Complete(); }
        }, CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            yield return evt;

        // Awaited after draining so every event a failing row produced has already been delivered;
        // surfacing the exception first would swallow the explanation.
        await drain;

        foreach (var rowState in finished) state.MergeFrom(rowState);
        gate.Dispose();
    }

    /// <summary>
    /// Reports a control-flow step's failure through the same bookkeeping an ordinary step uses, so
    /// ContinueOnStepError and the run summary behave identically either side of the split.
    /// </summary>
    private async IAsyncEnumerable<StepEvent> FailAsync(
        Step step, RunScope scope, ReplayRunState state, string message)
    {
        log.LogWarning("Step {Label} failed: {Message}", step.Label, message);
        yield return new StepEvent.StepCompleted(step.Id, StepStatus.Failed, message, null);
        state.LastStatus = StepStatus.Failed;
        state.Failed = true;
        if (!scope.Options.EffectiveFor(step).ContinueOnStepError) state.Stop = true;
        await Task.CompletedTask;
    }

    // ---- aggregates ------------------------------------------------------------------------

    /// <summary>The output name an aggregate step always publishes under.</summary>
    internal const string AggregateOutput = "value";

    /// <summary>
    /// Reduces a column to one number, or says why it cannot.
    /// <para>
    /// Blank cells are skipped and a non-blank cell that is not a number FAILS, rather than being
    /// skipped too. An average that quietly ignored half its rows is the most expensive kind of
    /// wrong this engine can be — it returns a plausible number nobody can tell is short.
    /// </para>
    /// </summary>
    private static (string? Text, string? Error) Reduce(
        IReadOnlyList<Dictionary<string, string>> rows, AggregateSpec spec)
    {
        var values = new List<double>();
        var present = 0;
        foreach (var row in rows)
        {
            if (!row.TryGetValue(spec.ColumnName, out var cell) || string.IsNullOrWhiteSpace(cell)) continue;
            present++;
            if (spec.Op == AggregateOp.Count) continue;
            if (!TryNumber(cell, out var number))
                return (null, $"'{cell}' in column '{spec.ColumnName}' is not a number");
            values.Add(number);
        }

        if (spec.Op == AggregateOp.Count) return (present.ToString(CultureInfo.InvariantCulture), null);
        if (values.Count == 0)
            return (null, $"nothing to work with — no row has a value in column '{spec.ColumnName}'");

        var result = spec.Op switch
        {
            AggregateOp.Sum => values.Sum(),
            AggregateOp.Min => values.Min(),
            AggregateOp.Max => values.Max(),
            AggregateOp.Average => values.Average(),
            _ => values.Sum(),
        };
        // Four decimals, invariant: an aggregate is a number, and how it should look with a
        // currency symbol on it is the business of whatever consumes it.
        return (result.ToString("0.####", CultureInfo.InvariantCulture), null);
    }

    // ---- conditions ------------------------------------------------------------------------

    internal static (bool Result, string? Error) Evaluate(ConditionSpec? spec, ReplayRunState state)
    {
        if (spec == null) return (false, "no condition set");

        // Asked BEFORE resolving, because these two are the only comparisons for which an absent
        // value is an answer rather than a problem. Everything below still refuses to compare
        // something that is not there — a mis-typed column name must not read as an empty one.
        if (spec.Op is ConditionOp.Exists or ConditionOp.NotExists)
        {
            var (found, _, lookupError) = BindingResolver.Lookup(spec.Left, state);
            if (lookupError != null) return (false, "left side: " + lookupError);
            return (spec.Op == ConditionOp.Exists ? found : !found, null);
        }

        var (left, leftError) = BindingResolver.Resolve(spec.Left, state);
        if (leftError != null) return (false, "left side: " + leftError);

        switch (spec.Op)
        {
            case ConditionOp.NotEmpty: return (!string.IsNullOrWhiteSpace(left), null);
            case ConditionOp.Empty: return (string.IsNullOrWhiteSpace(left), null);
            case ConditionOp.IsTrue: return (IsTruthy(left), null);
            case ConditionOp.IsFalse: return (!IsTruthy(left), null);
        }

        if (spec.Right == null) return (false, $"{spec.Op} needs a value to compare against");
        var (right, rightError) = BindingResolver.Resolve(spec.Right, state);
        if (rightError != null) return (false, "right side: " + rightError);

        switch (spec.Op)
        {
            case ConditionOp.Equals: return (string.Equals(left, right, StringComparison.Ordinal), null);
            case ConditionOp.NotEquals: return (!string.Equals(left, right, StringComparison.Ordinal), null);
            case ConditionOp.Contains:
                return (left != null && right != null
                    && left.Contains(right, StringComparison.OrdinalIgnoreCase), null);

            case ConditionOp.GreaterThan:
            case ConditionOp.LessThan:
            {
                if (!TryNumber(left, out var l)) return (false, $"'{left}' is not a number");
                if (!TryNumber(right, out var r)) return (false, $"'{right}' is not a number");
                return (spec.Op == ConditionOp.GreaterThan ? l > r : l < r, null);
            }

            default: return (false, $"unsupported comparison {spec.Op}");
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
         || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Text read off a page is almost never a bare number — it is "$19.99", "1,299" or "19.99 USD".
    /// Stripping everything but digits, a sign and a decimal point is what makes "price less than
    /// 20" behave the way anyone writing it expects.
    /// </summary>
    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = NonNumeric().Replace(text, "");
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"[^0-9.\-]")]
    private static partial Regex NonNumeric();
}

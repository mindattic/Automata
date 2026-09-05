using System.Globalization;
using System.Runtime.CompilerServices;
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
/// replacement for the caller.
/// </para>
/// <para>
/// <b>Everything here runs in order.</b> One run holds one browser, a loop walks its rows one at a
/// time, and a collection walks its tasks one at a time — so what a step sees is whatever the step
/// before it left behind, on a page that only one thing is touching.
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
    /// engine is a singleton: two runs can be in flight at once, so this must not be shared
    /// mutable state.
    /// </summary>
    private sealed record RunScope(ReplayOptions Options, IBrowserSurface Browser);

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
    /// Runs a task, on the one browser the caller hands it.
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
        ParkCheckpoint? resume = null)
    {
        var scope = new RunScope(options, browser);
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

        // Declared outputs, resolved from what the steps actually published. Emitted BEFORE the
        // completion event: an output nobody produced is left out rather than published empty, so
        // a task downstream falls back to its own default and says which input it is missing,
        // instead of running on a blank string that looks fine.
        //
        // A task that FAILED still publishes whatever it did produce, and that is deliberate. The
        // collection carries on past a failed task by default, so the choice is between handing the
        // next task the value that was actually read and handing it a default nobody chose — and
        // the run already reports the failure in its summary and its exit code.
        if (task.Outputs.Count > 0)
        {
            var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byId = Step.Flatten(task.Steps).ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var output in task.Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Name)) continue;

                // A step with one output is the ordinary case, and naming it in two places would
                // be one more thing to keep in step with a re-recording.
                var field = string.IsNullOrWhiteSpace(output.SourceOutputField)
                    ? byId.TryGetValue(output.SourceStepId ?? "", out var source)
                        ? source.Outputs?.FirstOrDefault()?.Name
                        : null
                    : output.SourceOutputField;

                if (!string.IsNullOrWhiteSpace(field)
                    && state.Outputs.TryGetValue(
                        ReplayRunState.OutputKey(output.SourceStepId, field), out var value))
                {
                    published[output.Name] = value;
                }
                else
                {
                    // Said out loud, every time. A declared output that produced nothing is the
                    // one failure in a pipeline that otherwise looks like a success: every task
                    // passes and the last one records a default nobody asked for.
                    yield return new StepEvent.Log(
                        $"'{task.Name}' declares an output '{output.Name}' that nothing produced this run — " +
                        "anything wired to it falls back to its own default.");
                }
            }
            if (published.Count > 0)
                yield return new StepEvent.TaskPublished(task.Id, task.Name, published);
        }

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
        string? previousIfId = null;

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
            state.PreviousIfId = previousIfId;
            await foreach (var evt in RunStepAsync(steps[index], scope, state, walk.Into(index, tail), ct))
                yield return evt;
            previousIfHeld = steps[index].Action == StepAction.If
                && state.IfVerdicts.TryGetValue(steps[index].Id, out var verdict) ? verdict : null;
            previousIfId = previousIfHeld == null ? null : steps[index].Id;
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
            // The run parked somewhere INSIDE this step, so if it is an `if` its condition held —
            // that is the only way the walk got in there. Written down rather than re-evaluated,
            // because an `otherwise` after it needs a verdict to be the other half of and there is
            // no page left to decide one against.
            if (step.Action == StepAction.If) state.IfVerdicts[step.Id] = true;

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

    /// <summary>
    /// Performs one orchestrated step.
    /// <para>
    /// <b>Every case that passes then runs its own children</b>, exactly as an ordinary step does —
    /// the tree lets any step hold children (drag one onto another, or Alt+Right), so a case that
    /// did not descend silently dropped whatever had been nested under it. Only <c>if</c>,
    /// <c>otherwise</c> and <c>forEach</c> differ, and they differ on purpose: their children are
    /// the branch or the loop body, and deciding whether and how often those run is what the step
    /// IS.
    /// </para>
    /// </summary>
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

                // Adjacency alone would accept whichever `if` ended up in front of this one, which
                // is how deleting a step can silently hand a branch to the wrong condition. When the
                // step records which `if` it was written for, that is the one it must still follow.
                if (!string.IsNullOrEmpty(step.PairedIfId)
                    && !string.Equals(step.PairedIfId, state.PreviousIfId, StringComparison.Ordinal))
                {
                    await foreach (var e in FailAsync(step, scope, state,
                        "this 'otherwise' belongs to a different 'if' than the one now before it — " +
                        "move it back, or delete it and add one here"))
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

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                    $"{rows.Count} row(s) from {name}", null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;

                var rowVar = string.IsNullOrWhiteSpace(spec2.RowVariableName) ? "row" : spec2.RowVariableName;

                // One row at a time, on the one browser this run holds. A row can therefore leave
                // the page somewhere the next row starts from, which is the whole reason a loop is
                // worth writing rather than four copies of the same steps.
                for (var i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new StepEvent.Log($"{step.Label}: row {i + 1} of {rows.Count}");

                    var rowState = state.ForkForRow(rowVar, rows[i], i + 1, name);
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

                // Asked for explicitly, never assumed: a called task normally belongs in its
                // caller's context, and navigating first would throw away the page the caller
                // spent its steps getting to.
                if (step.RunTaskOpensStartUrl && !string.IsNullOrWhiteSpace(target.StartUrl))
                {
                    yield return new StepEvent.Log(
                        $"Opening '{target.Name}' start URL {target.StartUrl}");
                    var navError = await ReplayEngine.TryNavigateAsync(scope.Browser, target.StartUrl!, ct);
                    if (navError != null)
                    {
                        state.TaskStack.Remove(target.Id);
                        await foreach (var e in FailAsync(step, scope, state,
                            $"could not open '{target.Name}' start URL: {navError}")) yield return e;
                        yield break;
                    }
                }

                yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed, $"running '{target.Name}'", null);
                state.LastStatus = StepStatus.Passed;
                state.Passed++;
                state.PushInputs(ReplayEngine.SeedInputs(target, supplied));
                var healedBefore = state.Healed;
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

                // The callee is loaded here and nowhere else, so this is the only place its repairs
                // can be written back — whoever started the run only ever sees, and only ever
                // saves, the tree it handed in. Without this a task called by another one
                // re-discovered the same drift on every single run.
                if (state.Healed > healedBefore)
                {
                    var count = state.Healed - healedBefore;
                    collections.SaveTask(target);
                    yield return new StepEvent.Log(
                        $"{count} step(s) self-healed in '{target.Name}' — saved back into it.");
                }

                if (state.Stop) yield break;
                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
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
                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
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
                await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                    yield return evt;
                yield break;
            }

            case StepAction.ExtractAll:
            {
                var spec = step.Harvest;
                if (spec == null || string.IsNullOrWhiteSpace(spec.DatasetName))
                {
                    await foreach (var e in FailAsync(step, scope, state, "no dataset name set")) yield return e;
                    yield break;
                }

                var result = await HarvestRunner.RunAsync(scope.Browser, spec, ct);

                // A harvest that read nothing usable is a failure, not an empty success. Writing an
                // empty dataset here would let the ForEach that consumes it loop zero times and
                // report a clean pass, which is the most expensive kind of wrong this engine can be.
                if (!result.Ok)
                {
                    await foreach (var e in FailAsync(step, scope, state, result.Error ?? "harvest failed"))
                        yield return e;
                    yield break;
                }

                datasets.Write(spec.DatasetName, result.Rows, spec.Append);

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
                // Always bounded. A spec with no timeout on it — one saved before the default
                // existed, or hand-edited — used to poll forever, which is a run that never
                // finishes and never says why.
                var timeoutMs = WaitSpec.EffectiveTimeoutMs(spec.TimeoutMs);
                var deadline = Environment.TickCount64 + timeoutMs;
                // A wait with a TARGET is watching the page; one without is re-asking a question
                // about values the run already holds. Both are useful and they are not the same
                // thing, so the presence of a target is what says which this is.
                var live = step.Target;
                var lastRead = live == null ? null : "(not read yet)";

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (live != null)
                    {
                        // Published under this step's own id, so the condition names it the way it
                        // names any other captured value and the picker has nothing new to learn.
                        // Re-published every poll, which is the whole difference: until this, the
                        // loop compared the same captured string to itself until it timed out.
                        var reading = await replay.ReadLiveAsync(scope.Browser, live, OneReadTimeoutMs, ct);
                        var key = ReplayRunState.OutputKey(step.Id, LiveWaitOutput);
                        if (reading == null) state.Outputs.Remove(key);
                        else state.Outputs[key] = reading;
                        lastRead = reading == null ? "(not on the page)" : $"'{reading}'";
                    }

                    var (holds, error) = Evaluate(spec.Condition, state);
                    if (holds)
                    {
                        var saw = live == null ? "" : $" — {DescribeTarget(live)} read {lastRead}";
                        yield return new StepEvent.StepCompleted(step.Id, StepStatus.Passed,
                            $"condition met{saw}", null);
                        state.LastStatus = StepStatus.Passed;
                        state.Passed++;
                        await foreach (var evt in RunStepsAsync(step.Children, scope, state, walk, ct))
                            yield return evt;
                        yield break;
                    }
                    if (error != null)
                    {
                        // An element that has not appeared yet is exactly what a wait waits for, so
                        // a condition that cannot be evaluated for want of a reading is "not yet"
                        // rather than a failure. A condition that cannot be evaluated for any OTHER
                        // reason — a mis-typed column, a value that is not a number — is a mistake
                        // in the task and fails now rather than at the end of the timeout.
                        if (live == null || state.Outputs.ContainsKey(
                                ReplayRunState.OutputKey(step.Id, LiveWaitOutput)))
                        {
                            await foreach (var e in FailAsync(step, scope, state, error)) yield return e;
                            yield break;
                        }
                    }
                    if (Environment.TickCount64 >= deadline)
                    {
                        // What it last saw, not just that it gave up: "still not met" alone leaves a
                        // person guessing between a selector that matched nothing and a value that
                        // never became the one they asked for.
                        var saw = live == null ? "" : $"; {DescribeTarget(live)} last read {lastRead}";
                        await foreach (var e in FailAsync(step, scope, state,
                            $"condition still not met after {timeoutMs}ms{saw}")) yield return e;
                        yield break;
                    }
                    await Task.Delay(pollMs, ct);
                }
            }
        }
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

    // ---- a wait that watches the page ---------------------------------------------------------

    /// <summary>The output name a condition wait publishes its live reading under.</summary>
    internal const string LiveWaitOutput = "value";

    /// <summary>
    /// How long ONE poll's read may take. Short on purpose: the wait already has its own poll
    /// interval and its own overall timeout, and a resolve that sat here for the step's full
    /// timeout would turn a 250ms poll into a ten-second one and blow through the deadline the
    /// task actually asked for.
    /// </summary>
    private const int OneReadTimeoutMs = 400;

    /// <summary>The shortest true thing that can be said about which element a wait is watching.</summary>
    private static string DescribeTarget(ElementFingerprint target) =>
        target.CssSelector ?? (target.Id != null ? "#" + target.Id : null) ??
        target.VisibleText ?? target.AriaLabel ?? target.Tag ?? "the target";

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

using System.IO;
using System.Runtime.CompilerServices;
using Automata.Core.Automation.Model;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Replay;

/// <summary>
/// Executes a task's step tree sequentially: resolve fingerprint (cascade) → perform action
/// (shared <see cref="BrowserActions"/> mechanics) → verify post-condition → settle-wait →
/// recurse into substeps. Streams <see cref="StepEvent"/>s the same way the LLM loop streams
/// OperatorEvents. A failed step aborts the run.
/// </summary>
public class ReplayEngine
{
    private readonly FingerprintResolver resolver;
    private readonly BrowserOperatorService? repairService;
    private readonly ILogger<ReplayEngine> log;

    public ReplayEngine(
        FingerprintResolver resolver,
        BrowserOperatorService? repairService = null,
        ILogger<ReplayEngine>? log = null)
    {
        this.resolver = resolver;
        this.repairService = repairService;
        this.log = log ?? NullLogger<ReplayEngine>.Instance;
    }

    /// <summary>The range a browser's own zoom menu offers. Outside it a step is far more likely
    /// to be a typo — 6 for 60 — than an intention, and a page at 6% is not automatable.</summary>
    internal const int MinZoomPercent = 25;
    internal const int MaxZoomPercent = 500;

    /// <summary>What a masked step reports instead of its value. Total withholding, not a scrub:
    /// a partial scrub that misses one interpolation is worse than a generic message.</summary>
    private const string Redacted = "••••••";

    public async IAsyncEnumerable<StepEvent> RunAsync(
        TaskDefinition task,
        ReplayOptions options,
        IBrowserSurface browser,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StepEvent.RunStarted(task.Id, task.Name);
        var state = new ReplayRunState();
        state.SetRunInputs(SeedInputs(task, options.Inputs));

        if (!string.IsNullOrWhiteSpace(task.StartUrl))
        {
            yield return new StepEvent.Log($"Navigating to start URL {task.StartUrl}");
            var navError = await TryNavigateAsync(browser, task.StartUrl!, ct);
            if (navError != null)
            {
                yield return new StepEvent.RunCompleted(false, $"Start URL failed: {navError}");
                yield break;
            }
        }

        foreach (var step in task.Steps)
        {
            await foreach (var evt in ExecuteStepAsync(step, options, browser, state, ct))
                yield return evt;
            if (state.Stop) break;
        }

        var success = !state.Failed;
        var summary =
            state.Failed ? $"Failed after {state.Passed} passed step(s)." :
            state.Healed > 0 ? $"{state.Passed} step(s) passed, {state.Healed} self-healed — task should be re-saved." :
            $"{state.Passed} step(s) passed.";
        yield return new StepEvent.RunCompleted(success, summary);
    }

    /// <summary>
    /// This step and then its children, which is what a plain task replay needs.
    /// <see cref="Execution.WorkflowEngine"/> does its own walking instead, because control-flow
    /// steps decide for themselves whether and how often their children run.
    /// </summary>
    private async IAsyncEnumerable<StepEvent> ExecuteStepAsync(
        Step step,
        ReplayOptions options,
        IBrowserSurface browser,
        ReplayRunState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ExecuteOneAsync(step, options, browser, state, ct))
            yield return evt;
        if (state.Stop || state.LastStatus == StepStatus.Failed) yield break;

        foreach (var child in step.Children)
        {
            await foreach (var evt in ExecuteStepAsync(child, options, browser, state, ct))
                yield return evt;
            if (state.Stop) yield break;
        }
    }

    /// <summary>
    /// Exactly one step: the pause gate, binding resolution, the retry loop, output publication,
    /// masking, and the pass/fail bookkeeping. Never touches <see cref="Step.Children"/> — the
    /// caller decides what to do with those.
    /// </summary>
    internal async IAsyncEnumerable<StepEvent> ExecuteOneAsync(
        Step step,
        ReplayOptions options,
        IBrowserSurface browser,
        ReplayRunState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        state.LastStatus = null;

        if (step.PauseForUser || step.Id == options.PauseBeforeStepId)
        {
            yield return new StepEvent.StepPaused(step.Id, step.Label);
            await options.Control.WaitAsync(ct);
        }

        yield return new StepEvent.StepStarted(step.Id, step.Label);

        var effective = options.EffectiveFor(step);
        var attempts = Math.Max(1, effective.Retry.MaxAttempts);

        var (value, url, bindingError) = BindingResolver.ResolveValues(step, state);
        if (bindingError != null)
        {
            yield return new StepEvent.StepCompleted(step.Id, StepStatus.Failed, bindingError, null);
            state.Failed = true;
            state.LastStatus = StepStatus.Failed;
            if (!effective.ContinueOnStepError) state.Stop = true;
            yield break;
        }

        StepStatus status;
        string? message;
        string? extracted;
        var attempt = 1;
        while (true)
        {
            (status, message, extracted) = await PerformAsync(step, options, effective, (value, url), browser, state, ct);
            if (status != StepStatus.Failed || attempt >= attempts) break;

            var delayMs = (int)Math.Round(
                effective.Retry.DelayMs * Math.Pow(effective.Retry.BackoffMultiplier, attempt - 1));
            yield return new StepEvent.Log(
                $"Step '{step.Label}' failed ({message}) — attempt {attempt} of {attempts}, retrying in {delayMs}ms");
            await Task.Delay(delayMs, ct);
            attempt++;
        }

        // Publish before redacting, so a later step can still bind to a masked value even though
        // nobody watching the run ever sees it.
        if (status != StepStatus.Failed && extracted != null && step.Outputs is { Count: > 0 })
        {
            foreach (var output in step.Outputs)
                if (!string.IsNullOrWhiteSpace(output.Name))
                    state.Outputs[ReplayRunState.OutputKey(step.Id, output.Name)] = extracted;
        }

        if (step.Masked)
        {
            if (extracted != null) extracted = Redacted;
            message = status == StepStatus.Failed
                ? $"{step.Action} failed — details withheld because this step is masked"
                : null;
        }

        yield return new StepEvent.StepCompleted(step.Id, status, message, extracted);
        state.LastStatus = status;

        if (status == StepStatus.Failed)
        {
            log.LogWarning("Step {Label} failed: {Message}", step.Label, message);
            state.Failed = true;
            // A failed step's own children never run — its post-condition did not hold, so its
            // substeps have no footing. ContinueOnStepError only decides whether its SIBLINGS do.
            if (!effective.ContinueOnStepError) state.Stop = true;
            yield break;
        }
        state.Passed++;
        if (status == StepStatus.Healed) state.Healed++;
    }

    private async Task<(StepStatus Status, string? Message, string? Extracted)> PerformAsync(
        Step step, ReplayOptions options, ResolvedSettings effective,
        (string? Value, string? Url) values, IBrowserSurface browser, ReplayRunState state,
        CancellationToken ct)
    {
        // Shadow the literals so every action below reads the RESOLVED value, whether it came
        // from the step itself or from a binding.
        var value = values.Value;
        var url = values.Url;

        // A per-step TimeoutMs still beats the resolved scope chain — it is the most specific
        // statement anyone made about this one step.
        var timeoutMs = step.TimeoutMs ?? effective.DefaultStepTimeoutMs;

        if (step.Action == StepAction.Group)
            return (StepStatus.Passed, "container", null);

        // Orchestrated actions are modelled but not executable here: they need dataset access,
        // cross-task lookups and a lane pool, none of which a single-task replay has. Rejecting
        // them explicitly beats a misleading "unsupported action" from the switch default.
        if (step.Action is StepAction.ForEach or StepAction.If or StepAction.RunTask
            or StepAction.WriteDataset or StepAction.Aggregate)
        {
            return (StepStatus.Failed,
                $"{step.Action} is a control-flow step — run this task through the workflow engine", null);
        }

        if (step.Action == StepAction.Wait)
            return await PerformWaitAsync(step, options, ct);

        if (step.Action == StepAction.SetZoom)
        {
            var percent = step.ZoomPercent ?? 100;
            if (percent is < MinZoomPercent or > MaxZoomPercent)
            {
                return (StepStatus.Failed,
                    $"a zoom of {percent}% is outside the {MinZoomPercent}-{MaxZoomPercent}% a browser offers", null);
            }
            var zoomed = await BrowserActions.SetZoomAsync(browser, percent, ct);
            if (!zoomed.Ok) return (StepStatus.Failed, zoomed.Error, null);
            state.ZoomPercent = percent;
            return (StepStatus.Passed, $"zoomed to {percent}%", null);
        }

        if (step.Action == StepAction.Navigate)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (StepStatus.Failed, "navigate step has no URL", null);
            var navError = await TryNavigateAsync(browser, url!, ct);
            if (navError != null) return (StepStatus.Failed, navError, null);
            await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);

            // A fresh document is at 100% again. Re-applying is what makes the zoom a property of
            // the run rather than of one page — a task that zoomed out to reach a wide layout
            // means it for the pages that follow, and finding out otherwise by a click landing on
            // nothing is the worst way to learn it.
            if (state.ZoomPercent != 100)
            {
                var restored = await BrowserActions.SetZoomAsync(browser, state.ZoomPercent, ct);
                if (!restored.Ok)
                    return (StepStatus.Failed, $"could not restore the {state.ZoomPercent}% zoom: {restored.Error}", null);
                return (StepStatus.Passed, $"navigated, back to {state.ZoomPercent}% zoom", null);
            }
            return (StepStatus.Passed, null, null);
        }

        // Targetless PressEnter goes to whatever already has focus (typically the field the
        // previous TypeText step just typed into).
        if (step.Action == StepAction.PressEnter && step.Target == null)
        {
            await browser.PressEnterAsync(ct);
            await Task.Delay(300, ct);
            await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);
            return (StepStatus.Passed, "pressed Enter", null);
        }

        if (step.Target == null)
            return (StepStatus.Failed, $"{step.Action} step has no target fingerprint", null);

        var resolved = await resolver.ResolveAsync(
            browser, step.Target, highlight: true, refingerprint: effective.SelfHeal, timeoutMs, ct);
        if (!resolved.Found)
        {
            var reason = resolved.Ambiguous
                ? $"element ambiguous ({resolved.CandidateCount} near-tie candidates)"
                : "element not found by any strategy";

            // Last resort: hand this one step's intent to the LLM tool loop.
            if (effective.AllowLlmRepair && repairService != null && IsRepairable(step.Action))
            {
                log.LogInformation("Step '{Label}' unresolvable ({Reason}) — attempting LLM repair", step.Label, reason);
                if (await TryLlmRepairAsync(step, browser, ct))
                    return (StepStatus.Passed,
                        $"{reason} — completed via LLM repair; consider re-recording this step", null);
                return (StepStatus.Failed, $"{reason}; LLM repair also failed", null);
            }
            return (StepStatus.Failed, reason, null);
        }

        var healed = false;
        if (effective.SelfHeal && resolved.Refreshed != null)
        {
            step.Target = resolved.Refreshed;
            healed = true;
        }
        var passStatus = healed ? StepStatus.Healed : StepStatus.Passed;
        var healNote = healed ? $" (healed via {resolved.Strategy})" : "";

        switch (step.Action)
        {
            case StepAction.WaitForElement:
                return (passStatus, $"element present{healNote}", null);

            case StepAction.AssertElement:
            {
                if (string.IsNullOrEmpty(value))
                    return (passStatus, $"element present{healNote}", null);
                var actual = await BrowserActions.ReadResolvedTextAsync(browser, ct);
                if (!actual.Ok)
                    return (StepStatus.Failed, actual.Error, null);
                return actual.Value?.Contains(value, StringComparison.OrdinalIgnoreCase) == true
                    ? (passStatus, $"matched '{value}'{healNote}", null)
                    : (StepStatus.Failed, $"expected text '{value}' but found '{actual.Value}'", null);
            }

            case StepAction.ExtractText:
            {
                var text = await BrowserActions.ReadResolvedTextAsync(browser, ct);
                return text.Ok
                    ? (passStatus, $"extracted{healNote}", text.Value)
                    : (StepStatus.Failed, text.Error, null);
            }
        }

        switch (step.Action)
        {
            case StepAction.Click:
                await browser.ClickAtPointAsync(resolved.CenterX, resolved.CenterY, ct);
                await Task.Delay(300, ct);
                await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);
                return (passStatus, $"clicked{healNote}", null);

            case StepAction.PressEnter:
                await browser.ClickAtPointAsync(resolved.CenterX, resolved.CenterY, ct); // focus the field
                await Task.Delay(150, ct);
                await browser.PressEnterAsync(ct);
                await Task.Delay(300, ct);
                await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);
                return (passStatus, $"pressed Enter{healNote}", null);

            case StepAction.TypeText:
            {
                var typed = await BrowserActions.TypeViaKeystrokesAsync(
                    browser, resolved.CenterX, resolved.CenterY, value ?? "", ct);
                return typed == (value ?? "")
                    ? (passStatus, $"typed{healNote}", null)
                    : (StepStatus.Failed, $"typed value read back as '{typed}'", null);
            }

            case StepAction.SetValue:
            {
                var set = await BrowserActions.SetValueOnResolvedAsync(browser, value ?? "", ct);
                if (!set.Ok) return (StepStatus.Failed, set.Error, null);
                return set.Value == (value ?? "")
                    ? (passStatus, $"value set{healNote}", null)
                    : (StepStatus.Failed, $"value read back as '{set.Value}'", null);
            }

            case StepAction.Check:
            case StepAction.Uncheck:
            case StepAction.SelectRadio:
            {
                var desired = step.Action != StepAction.Uncheck;
                var probe = await BrowserActions.ProbeResolvedCheckStateAsync(browser, ct);
                if (!probe.Ok) return (StepStatus.Failed, probe.Error, null);
                if (probe.Checked == desired)
                    return (passStatus, $"already in desired state{healNote}", null);

                if (probe.Native)
                    await BrowserActions.ClickResolvedNativelyAsync(browser, ct);
                else
                    await browser.ClickAtPointAsync(probe.CenterX, probe.CenterY, ct);
                await Task.Delay(300, ct);

                var after = await BrowserActions.ProbeResolvedCheckStateAsync(browser, ct);
                return after.Ok && after.Checked == desired
                    ? (passStatus, $"{step.Action}{healNote}", null)
                    : (StepStatus.Failed, $"state did not change to {(desired ? "checked" : "unchecked")}", null);
            }

            case StepAction.SelectOption:
            {
                var selected = await BrowserActions.SelectOptionOnResolvedAsync(browser, value ?? "", ct);
                return selected.Ok
                    ? (passStatus, $"selected '{selected.Value}'{healNote}", null)
                    : (StepStatus.Failed, selected.Error, null);
            }

            case StepAction.UploadFile:
            {
                if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
                    return (StepStatus.Failed, $"file not found: '{value}' — set a local path on this step", null);
                await BrowserActions.UploadToResolvedAsync(browser, value!, ct);
                var count = await BrowserActions.CountResolvedFilesAsync(browser, ct);
                return count > 0
                    ? (passStatus, $"attached {Path.GetFileName(value)}{healNote}", null)
                    : (StepStatus.Failed, "file input reports no attached file", null);
            }

            default:
                return (StepStatus.Failed, $"unsupported action {step.Action}", null);
        }
    }

    /// <summary>
    /// Performs a clock-based wait, holding the browser for its whole length.
    /// <para>
    /// A wait long enough to be worth parking never reaches here when parking is available — the
    /// workflow engine intercepts it, checkpoints the run and releases the lane. So arriving here
    /// with a long wait means parking was unavailable (a dry run, a wait inside a for-each), and
    /// the message says the pane stays occupied rather than letting it look like a hang.
    /// </para>
    /// </summary>
    private static async Task<(StepStatus Status, string? Message, string? Extracted)> PerformWaitAsync(
        Step step, ReplayOptions options, CancellationToken ct)
    {
        var spec = step.Wait ?? new WaitSpec();
        var (plan, error) = WaitPlan.For(spec, options.Clock());
        if (error != null) return (StepStatus.Failed, error, null);
        if (plan == null)
            return (StepStatus.Failed,
                $"a {spec.Mode} wait needs the workflow engine, which is not wired up yet", null);

        var note = WaitPlan.ShouldPark(spec, plan.Remaining)
            ? " — the browser stays occupied for the whole wait, because this run cannot park"
            : "";
        await Task.Delay(plan.Remaining, ct);
        return (StepStatus.Passed, $"waited {plan.Description}{note}", null);
    }

    private static bool IsRepairable(StepAction action) => action is
        StepAction.Click or StepAction.TypeText or StepAction.SetValue or
        StepAction.Check or StepAction.Uncheck or StepAction.SelectRadio or
        StepAction.SelectOption or StepAction.UploadFile;

    private const string RepairSystemPrompt = """
        You are repairing ONE step of a recorded browser automation whose element could not be
        located by its saved selectors — the page's markup likely changed. Perform exactly that
        one step and NOTHING else: no extra navigation, no other clicks, no exploring. Use
        get_page_status first if you need orientation. When the step is done (or truly
        impossible), reply with a one-line summary and stop.
        """;

    /// <summary>True when the LLM loop performed at least one successful tool action and
    /// reported no errors — the closest verifiable proxy for "the step got done".</summary>
    private async Task<bool> TryLlmRepairAsync(Step step, IBrowserSurface browser, CancellationToken ct)
    {
        var hints = new List<string>();
        if (step.Target?.NearbyLabelText != null) hints.Add($"its label reads \"{step.Target.NearbyLabelText}\"");
        if (step.Target?.VisibleText != null) hints.Add($"its visible text was \"{step.Target.VisibleText}\"");
        if (step.Target?.AriaLabel != null) hints.Add($"its aria-label was \"{step.Target.AriaLabel}\"");
        if (step.Target?.Placeholder != null) hints.Add($"its placeholder was \"{step.Target.Placeholder}\"");

        var instruction =
            $"The step to perform: {step.Action} — \"{step.Label}\"." +
            (string.IsNullOrEmpty(step.Value) ? "" : $" The value involved: \"{step.Value}\".") +
            (hints.Count > 0 ? $" The original element: {string.Join("; ", hints)}." : "");

        var ctx = new BrowserOperatorContext { Browser = browser };
        var anyToolSucceeded = false;
        var fatalError = false;
        try
        {
            await foreach (var evt in repairService!.RunAsync(RepairSystemPrompt, instruction, ctx, maxIterations: 6, ct))
            {
                switch (evt)
                {
                    case OperatorEvent.ToolCompleted c when !c.IsError:
                        anyToolSucceeded = true;
                        break;
                    case OperatorEvent.Error e:
                        fatalError = true;
                        log.LogWarning("LLM repair error: {Message}", e.Message);
                        break;
                    case OperatorEvent.AssistantText t:
                        log.LogInformation("LLM repair: {Text}", t.Text);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "LLM repair threw");
            return false;
        }
        return anyToolSucceeded && !fatalError;
    }


    /// <summary>
    /// The values a run starts with: every input the task declares, taking what the caller supplied
    /// and otherwise its default. An input with neither is left out entirely rather than blanked —
    /// a binding to it must fail saying nothing supplied it, not resolve to an empty string that
    /// types nothing into a search box and reports success.
    /// </summary>
    internal static Dictionary<string, string> SeedInputs(
        TaskDefinition task, IReadOnlyDictionary<string, string> supplied)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in task.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name)) continue;
            if (supplied.TryGetValue(input.Name, out var given)) values[input.Name] = given;
            else if (input.Default != null) values[input.Name] = input.Default;
        }
        return values;
    }

    internal static async Task<string?> TryNavigateAsync(IBrowserSurface browser, string url, CancellationToken ct)
    {
        try
        {
            await browser.NavigateAsync(url, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"navigation to {url} failed: {ex.Message}";
        }
    }
}

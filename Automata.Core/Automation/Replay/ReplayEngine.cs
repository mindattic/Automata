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
/// OperatorEvents. A failed step aborts the run; DryRun stops before the first commit point;
/// Validate resolves and highlights without mutating.
/// </summary>
public class ReplayEngine
{
    private readonly FingerprintResolver resolver;
    private readonly ILogger<ReplayEngine> log;

    public ReplayEngine(FingerprintResolver resolver, ILogger<ReplayEngine>? log = null)
    {
        this.resolver = resolver;
        this.log = log ?? NullLogger<ReplayEngine>.Instance;
    }

    private sealed class RunState
    {
        public bool Stop;
        public bool Failed;
        public bool DryRunStopped;
        public int Passed;
        public int Healed;
    }

    public async IAsyncEnumerable<StepEvent> RunAsync(
        TaskDefinition task,
        ReplayOptions options,
        IBrowserSurface browser,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StepEvent.RunStarted(task.Id, task.Name, options.Mode);
        var state = new RunState();

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
            state.DryRunStopped ? $"Dry run: {state.Passed} step(s) passed, stopped at commit point." :
            state.Healed > 0 ? $"{state.Passed} step(s) passed, {state.Healed} self-healed — task should be re-saved." :
            $"{state.Passed} step(s) passed.";
        yield return new StepEvent.RunCompleted(success, summary);
    }

    private async IAsyncEnumerable<StepEvent> ExecuteStepAsync(
        Step step,
        ReplayOptions options,
        IBrowserSurface browser,
        RunState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (step.PauseForUser)
        {
            yield return new StepEvent.StepPaused(step.Id, step.Label);
            await options.Control.WaitAsync(ct);
        }

        if (options.Mode == ReplayMode.DryRun && step.IsCommitPoint)
        {
            yield return new StepEvent.StepCompleted(step.Id, StepStatus.Skipped,
                "commit point — dry run stopped here");
            state.Stop = true;
            state.DryRunStopped = true;
            yield break;
        }

        yield return new StepEvent.StepStarted(step.Id, step.Label);

        var (status, message, extracted) = await PerformAsync(step, options, browser, ct);
        yield return new StepEvent.StepCompleted(step.Id, status, message, extracted);

        if (status == StepStatus.Failed)
        {
            log.LogWarning("Step {Label} failed: {Message}", step.Label, message);
            state.Stop = true;
            state.Failed = true;
            yield break;
        }
        state.Passed++;
        if (status == StepStatus.Healed) state.Healed++;

        foreach (var child in step.Children)
        {
            await foreach (var evt in ExecuteStepAsync(child, options, browser, state, ct))
                yield return evt;
            if (state.Stop) yield break;
        }
    }

    private async Task<(StepStatus Status, string? Message, string? Extracted)> PerformAsync(
        Step step, ReplayOptions options, IBrowserSurface browser, CancellationToken ct)
    {
        var timeoutMs = step.TimeoutMs ?? options.DefaultStepTimeoutMs;

        if (step.Action == StepAction.Group)
            return (StepStatus.Passed, "container", null);

        if (step.Action == StepAction.Navigate)
        {
            if (string.IsNullOrWhiteSpace(step.Url))
                return (StepStatus.Failed, "navigate step has no URL", null);
            // Validate mode still navigates — multi-page tasks can't be validated otherwise.
            var navError = await TryNavigateAsync(browser, step.Url!, ct);
            if (navError != null) return (StepStatus.Failed, navError, null);
            await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);
            return (StepStatus.Passed, null, null);
        }

        if (step.Target == null)
            return (StepStatus.Failed, $"{step.Action} step has no target fingerprint", null);

        var resolved = await resolver.ResolveAsync(
            browser, step.Target, highlight: true, refingerprint: options.SelfHeal, timeoutMs, ct);
        if (!resolved.Found)
        {
            var reason = resolved.Ambiguous
                ? $"element ambiguous ({resolved.CandidateCount} near-tie candidates)"
                : "element not found by any strategy";
            return (StepStatus.Failed, reason, null);
        }

        var healed = false;
        if (options.SelfHeal && resolved.Refreshed != null)
        {
            step.Target = resolved.Refreshed;
            healed = true;
        }
        var passStatus = healed ? StepStatus.Healed : StepStatus.Passed;
        var healNote = healed ? $" (healed via {resolved.Strategy})" : "";

        // Non-mutating actions run in every mode; mutations are skipped by Validate.
        switch (step.Action)
        {
            case StepAction.WaitForElement:
                return (passStatus, $"element present{healNote}", null);

            case StepAction.AssertElement:
            {
                if (string.IsNullOrEmpty(step.Value))
                    return (passStatus, $"element present{healNote}", null);
                var actual = await BrowserActions.ReadResolvedTextAsync(browser, ct);
                if (!actual.Ok)
                    return (StepStatus.Failed, actual.Error, null);
                return actual.Value?.Contains(step.Value, StringComparison.OrdinalIgnoreCase) == true
                    ? (passStatus, $"matched '{step.Value}'{healNote}", null)
                    : (StepStatus.Failed, $"expected text '{step.Value}' but found '{actual.Value}'", null);
            }

            case StepAction.ExtractText:
            {
                var text = await BrowserActions.ReadResolvedTextAsync(browser, ct);
                return text.Ok
                    ? (passStatus, $"extracted{healNote}", text.Value)
                    : (StepStatus.Failed, text.Error, null);
            }
        }

        if (options.Mode == ReplayMode.Validate)
            return (passStatus, $"resolved via {resolved.Strategy} (validate — not performed)", null);

        switch (step.Action)
        {
            case StepAction.Click:
                await browser.ClickAtPointAsync(resolved.CenterX, resolved.CenterY, ct);
                await Task.Delay(300, ct);
                await BrowserActions.WaitForSettleAsync(browser, timeoutMs, options.SettlePollMs, ct);
                return (passStatus, $"clicked{healNote}", null);

            case StepAction.TypeText:
            {
                var typed = await BrowserActions.TypeViaKeystrokesAsync(
                    browser, resolved.CenterX, resolved.CenterY, step.Value ?? "", ct);
                return typed == (step.Value ?? "")
                    ? (passStatus, $"typed{healNote}", null)
                    : (StepStatus.Failed, $"typed value read back as '{typed}'", null);
            }

            case StepAction.SetValue:
            {
                var set = await BrowserActions.SetValueOnResolvedAsync(browser, step.Value ?? "", ct);
                if (!set.Ok) return (StepStatus.Failed, set.Error, null);
                return set.Value == (step.Value ?? "")
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
                var selected = await BrowserActions.SelectOptionOnResolvedAsync(browser, step.Value ?? "", ct);
                return selected.Ok
                    ? (passStatus, $"selected '{selected.Value}'{healNote}", null)
                    : (StepStatus.Failed, selected.Error, null);
            }

            case StepAction.UploadFile:
            {
                if (string.IsNullOrWhiteSpace(step.Value) || !File.Exists(step.Value))
                    return (StepStatus.Failed, $"file not found: '{step.Value}' — set a local path on this step", null);
                await BrowserActions.UploadToResolvedAsync(browser, step.Value!, ct);
                var count = await BrowserActions.CountResolvedFilesAsync(browser, ct);
                return count > 0
                    ? (passStatus, $"attached {Path.GetFileName(step.Value)}{healNote}", null)
                    : (StepStatus.Failed, "file input reports no attached file", null);
            }

            default:
                return (StepStatus.Failed, $"unsupported action {step.Action}", null);
        }
    }

    private static async Task<string?> TryNavigateAsync(IBrowserSurface browser, string url, CancellationToken ct)
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

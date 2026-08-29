namespace Automata.Core.Automation.Replay;

/// <summary>
/// The pauseForUser gate: the engine parks on <see cref="WaitAsync"/> when a step is flagged;
/// the sidebar's Continue button calls <see cref="Continue"/>. A Continue that lands BEFORE the
/// engine reaches WaitAsync (the StepPaused event is yielded first, so the UI can win that race)
/// is latched rather than lost — otherwise the run would hang on a fresh, never-completed gate.
/// </summary>
public sealed class ReplayControl
{
    private readonly object sync = new();
    private TaskCompletionSource? gate;
    private bool pendingContinue;

    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (sync)
        {
            if (pendingContinue)
            {
                pendingContinue = false;
                return;
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = tcs;
        }
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    public void Continue()
    {
        lock (sync)
        {
            if (gate != null)
            {
                gate.TrySetResult();
                gate = null;
            }
            else
            {
                pendingContinue = true;
            }
        }
    }
}

public sealed class ReplayOptions
{
    /// <summary>Write a refreshed fingerprint back into a step that only resolved via a fallback
    /// strategy, so the next run starts from the healed identity.</summary>
    public bool SelfHeal { get; init; } = true;

    /// <summary>Last-resort: hand an unresolvable step to the LLM tool-calling loop.</summary>
    public bool AllowLlmRepair { get; init; } = false;

    public int DefaultStepTimeoutMs { get; init; } = 10_000;

    /// <summary>Delay between page-settle polls. Lowered in tests.</summary>
    public int SettlePollMs { get; init; } = 500;

    public ReplayControl Control { get; init; } = new();

    /// <summary>Transient, run-scoped: pause before the step with this Id, exactly like
    /// <see cref="Automata.Core.Automation.Model.Step.PauseForUser"/> but never persisted — a
    /// fresh ReplayOptions is built per run. Used by the record-at-gap flow to park replay right
    /// before the step occupying the insertion point.</summary>
    public string? PauseBeforeStepId { get; init; }
}

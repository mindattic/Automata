using Automata.Core.Automation.Model;
using Automata.Core.Automation.Settings;

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

    /// <summary>
    /// Whether a wait longer than its <see cref="WaitSpec.ParkAfterMs"/> may checkpoint the run and
    /// let its browser go, to be resumed by a later scheduler tick.
    /// <para>
    /// Off for callers who cannot finish what they start — a dry run, a validation pass, or the
    /// record-at-gap flow, which is driving the run to one specific step and would be left holding
    /// nothing. Those hold the browser through the wait instead, and the run says so.
    /// </para>
    /// </summary>
    public bool AllowParking { get; init; } = true;

    /// <summary>
    /// The clock the engine reads for wait arithmetic. Injected so parking — whose whole subject
    /// is "how long is left" — is testable without waiting for real hours to pass.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Transient, run-scoped: pause before the step with this Id, exactly like
    /// <see cref="Automata.Core.Automation.Model.Step.PauseForUser"/> but never persisted — a
    /// fresh ReplayOptions is built per run. Used by the record-at-gap flow to park replay right
    /// before the step occupying the insertion point.</summary>
    public string? PauseBeforeStepId { get; init; }

    /// <summary>
    /// Resolves the effective settings for one step through the global → collection → task → step
    /// chain. Left null by callers that have no scoped settings (every existing test does), in
    /// which case <see cref="EffectiveFor"/> falls back to the scalar properties above — so an
    /// options object built the old way behaves exactly as it always did.
    /// </summary>
    public Func<Step, ResolvedSettings>? ResolveForStep { get; init; }

    private ResolvedSettings? runLevel;

    /// <summary>The settings the engine should actually apply to <paramref name="step"/>.</summary>
    public ResolvedSettings EffectiveFor(Step step) =>
        ResolveForStep?.Invoke(step) ?? (runLevel ??= EngineSettingsResolver.Floor() with
        {
            DefaultStepTimeoutMs = DefaultStepTimeoutMs,
            SelfHeal = SelfHeal,
            AllowLlmRepair = AllowLlmRepair,
        });
}

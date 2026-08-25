namespace Automata.Core.Automation.Replay;

public enum ReplayMode
{
    /// <summary>Execute every step for real.</summary>
    Run,

    /// <summary>Execute for real but stop BEFORE the first step flagged isCommitPoint — tests
    /// the flow without committing any permanent submission/write.</summary>
    DryRun,

    /// <summary>Resolve and highlight every step's element, mutating nothing. Navigate steps
    /// still execute so multi-page tasks can be validated end-to-end.</summary>
    Validate,
}

/// <summary>
/// The pauseForUser gate: the engine parks on <see cref="WaitAsync"/> when a step is flagged;
/// the sidebar's Continue button calls <see cref="Continue"/>.
/// </summary>
public sealed class ReplayControl
{
    private volatile TaskCompletionSource? gate;

    public async Task WaitAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate = tcs;
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    public void Continue() => gate?.TrySetResult();
}

public sealed class ReplayOptions
{
    public ReplayMode Mode { get; init; } = ReplayMode.Run;

    /// <summary>Write a refreshed fingerprint back into a step that only resolved via a fallback
    /// strategy, so the next run starts from the healed identity.</summary>
    public bool SelfHeal { get; init; } = true;

    /// <summary>Last-resort: hand an unresolvable step to the LLM tool-calling loop.</summary>
    public bool AllowLlmRepair { get; init; } = false;

    public int DefaultStepTimeoutMs { get; init; } = 10_000;

    /// <summary>Delay between page-settle polls. Lowered in tests.</summary>
    public int SettlePollMs { get; init; } = 500;

    public ReplayControl Control { get; init; } = new();
}

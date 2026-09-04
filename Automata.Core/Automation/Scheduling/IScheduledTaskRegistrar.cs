namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// Registers (or removes) the recurring heartbeat with the operating system's scheduler.
/// <para>
/// Behind an interface so the CLI that uses it stays testable: the real implementation shells out
/// to <c>schtasks.exe</c> and lives in the runner, which is Windows-only.
/// </para>
/// <para>
/// The registered task is deliberately dumb — "run this exe every N minutes". Every cron
/// expression, interval and after-this-finishes chain is worked out in-process by
/// <see cref="TriggerEvaluator"/>, because <c>schtasks</c> has no vocabulary for any of it.
/// </para>
/// </summary>
public interface IScheduledTaskRegistrar
{
    /// <summary>Registers the heartbeat. Returns what to tell the user.</summary>
    Task<string> InstallAsync(int intervalMinutes, CancellationToken ct);

    Task<string> UninstallAsync(CancellationToken ct);
}

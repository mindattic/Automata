namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// What a registration attempt did, and whether it actually worked.
/// <para>
/// The success flag exists because the operating system can refuse part of the job — most often
/// for want of a privilege — and a half-registered heartbeat is the worst possible outcome: the
/// user is told it is scheduled, and nothing ever fires. Anything that cannot be registered is
/// reported as a failure with the scheduler's own words in <see cref="Report"/>.
/// </para>
/// </summary>
public readonly record struct RegistrationResult(bool Succeeded, string Report)
{
    public static RegistrationResult Ok(string report) => new(true, report);

    public static RegistrationResult Failed(string report) => new(false, report);
}

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
    /// <summary>Registers the heartbeat. Returns what to tell the user, and whether it took.</summary>
    Task<RegistrationResult> InstallAsync(int intervalMinutes, CancellationToken ct);

    Task<RegistrationResult> UninstallAsync(CancellationToken ct);
}

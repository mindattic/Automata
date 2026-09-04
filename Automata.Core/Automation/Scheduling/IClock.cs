namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// The current time, injected rather than read from <see cref="DateTimeOffset.UtcNow"/> directly.
/// <para>
/// Scheduling is almost entirely about "what happens next", and testing that against the real
/// clock means either waiting or accepting flaky assertions. A fake clock makes every case —
/// including the awkward ones like a spring-forward gap — deterministic and instant.
/// </para>
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

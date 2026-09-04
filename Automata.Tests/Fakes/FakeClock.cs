using Automata.Core.Automation.Scheduling;

namespace Automata.Tests.Fakes;

/// <summary>A clock a test drives directly, so scheduling cases are instant and deterministic.</summary>
public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public FakeClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}

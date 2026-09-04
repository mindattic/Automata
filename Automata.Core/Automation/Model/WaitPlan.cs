namespace Automata.Core.Automation.Model;

/// <summary>
/// How long a wait still has to run, and whether that is long enough to be worth parking the run
/// rather than holding a browser idle through it.
/// <para>
/// Pure, and deliberately the ONE definition of both answers. Three places need them and must
/// agree: the replay engine, which performs a wait; the workflow engine, which decides before the
/// wait starts whether to checkpoint and let the lane go; and the runner, which decides when a
/// parked run has come due. If they disagreed by even a little, a parked run would resume early
/// and then wait a second time — the sort of bug that looks like the scheduler being flaky.
/// </para>
/// </summary>
public static class WaitPlan
{
    /// <summary>A clock-based wait's end, and how it should be described in a log or a run record.</summary>
    /// <param name="Remaining">Time left from the "now" the plan was made against. Never negative.</param>
    /// <param name="EndsAtUtc">The instant the wait is over.</param>
    /// <param name="Description">
    /// Human-readable and self-contained, e.g. <c>until 09:00 (Europe/London)</c> or
    /// <c>for 70000ms</c> — so both "waited &lt;description&gt;" and "a wait &lt;description&gt;"
    /// read as English wherever they are shown.
    /// </param>
    public sealed record Plan(TimeSpan Remaining, DateTimeOffset EndsAtUtc, string Description);

    /// <summary>
    /// Works out when a wait ends.
    /// <para>
    /// Returns <c>(null, null)</c> for a condition or signal wait: those have no knowable end, so
    /// there is nothing to plan around and nothing to park until. An error string means the spec
    /// itself is unusable (no time of day set, an unknown zone) and the step should fail rather
    /// than wait forever.
    /// </para>
    /// </summary>
    public static (Plan? Plan, string? Error) For(WaitSpec spec, DateTimeOffset now)
    {
        switch (spec.Mode)
        {
            case WaitMode.Duration:
            {
                var ms = Math.Max(0, spec.DurationMs ?? 0);
                return (new Plan(TimeSpan.FromMilliseconds(ms), now.AddMilliseconds(ms).ToUniversalTime(),
                    $"for {ms}ms"), null);
            }

            case WaitMode.UntilTimeOfDay:
            {
                if (spec.TimeOfDay is not { } timeOfDay)
                    return (null, "wait step has no time of day set");
                if (!TryResolveZone(spec.TimeZoneId, out var zone))
                    return (null, $"unknown time zone '{spec.TimeZoneId}'");

                var ms = MillisecondsUntil(timeOfDay, zone, now);
                return (new Plan(TimeSpan.FromMilliseconds(ms), now.AddMilliseconds(ms).ToUniversalTime(),
                    $"until {timeOfDay:HH\\:mm} ({zone.Id})"), null);
            }

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Whether a wait this long is worth parking for. <see cref="WaitSpec.ParkAfterMs"/> at or
    /// below zero means never park — which is how a task that must keep its page state (a session
    /// it logged into before the wait) opts out, since parking releases the browser.
    /// </summary>
    public static bool ShouldPark(WaitSpec spec, TimeSpan remaining) =>
        spec.ParkAfterMs > 0 && remaining.TotalMilliseconds > spec.ParkAfterMs;

    public static bool TryResolveZone(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id)) { zone = TimeZoneInfo.Local; return true; }
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Local;
            return false;
        }
    }

    /// <summary>
    /// Milliseconds from <paramref name="now"/> to the next occurrence of <paramref name="timeOfDay"/>
    /// in <paramref name="zone"/> — today if it is still ahead, otherwise tomorrow. Pure, so the
    /// DST cases are testable without waiting for anything.
    /// </summary>
    public static double MillisecondsUntil(TimeOnly timeOfDay, TimeZoneInfo zone, DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = localNow.Date + timeOfDay.ToTimeSpan();
        if (candidate <= localNow.DateTime) candidate = candidate.AddDays(1);

        // A spring-forward gap means that wall-clock time does not exist that day; take the first
        // instant after the gap rather than throwing.
        while (zone.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);

        var targetUtc = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
        return Math.Max(0, (targetUtc - now.ToUniversalTime()).TotalMilliseconds);
    }
}

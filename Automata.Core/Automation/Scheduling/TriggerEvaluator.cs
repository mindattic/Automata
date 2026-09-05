namespace Automata.Core.Automation.Scheduling;

/// <summary>Why an entry is (or is not) due.</summary>
/// <param name="Due">True when it should run now.</param>
/// <param name="NextUtc">When it should next run, or null when nothing will start it on a clock.</param>
/// <param name="Reason">Human-readable, for <c>status</c> and for diagnosing a schedule that never fires.</param>
public sealed record DueVerdict(bool Due, DateTimeOffset? NextUtc, string Reason);

/// <summary>
/// Decides whether a schedule entry should run now, and when it should run next.
/// <para>
/// Pure: it takes the clock as an argument and touches no store, so every case — a missed firing,
/// a DST gap, an expression that can never match — is testable without waiting for a real minute
/// to pass.
/// </para>
/// <para>
/// All clock-based triggers are evaluated against <see cref="ScheduleEntry.NextDueUtc"/> rather
/// than recomputed from "now". That is what makes a firing survive the process exiting between
/// ticks: the due time is written down, so a tick that arrives late still sees it as due.
/// </para>
/// </summary>
public static class TriggerEvaluator
{
    public static DueVerdict Evaluate(ScheduleEntry entry, IClock clock)
    {
        var now = clock.UtcNow;

        if (!entry.Enabled) return new DueVerdict(false, null, "disabled");

        var clockTriggers = entry.Triggers
            .Where(t => t.Enabled && t.Kind is TriggerKind.Cron or TriggerKind.Interval or TriggerKind.OneShot)
            .ToList();

        if (clockTriggers.Count == 0)
        {
            var waiting = entry.Triggers.Any(t => t.Enabled && t.Kind == TriggerKind.AfterEntry);
            return new DueVerdict(false, null,
                waiting ? "waits for another entry to finish" : "runs only when started by hand");
        }

        // A due time already written down wins over recomputing: a tick that arrives late must
        // still see a firing it missed while nothing was running.
        if (entry.NextDueUtc is { } scheduled)
        {
            if (scheduled <= now)
            {
                var late = now - scheduled;
                var skip = late > TimeSpan.FromMinutes(5)
                    && clockTriggers.All(t => t.CatchUp == CatchUpPolicy.Skip);

                if (skip)
                {
                    // The default. A batch of missed runs all firing at once after a machine was
                    // off is rarely what anyone meant by "every hour".
                    var rescheduled = NextAcross(clockTriggers, now);
                    return new DueVerdict(false, rescheduled,
                        $"missed by {Describe(late)} while nothing was running — skipped to the next firing");
                }
                return new DueVerdict(true, NextAcross(clockTriggers, now), "due");
            }
            return new DueVerdict(false, scheduled, $"next in {Describe(scheduled - now)}");
        }

        // Never scheduled before: work out the first firing rather than running immediately.
        var first = NextAcross(clockTriggers, now);
        return new DueVerdict(false, first,
            first == null ? "no trigger will ever fire — check the expression" : $"first run in {Describe(first.Value - now)}");
    }

    /// <summary>The soonest firing across every clock trigger on an entry.</summary>
    public static DateTimeOffset? NextAcross(IEnumerable<TriggerDefinition> triggers, DateTimeOffset after)
    {
        DateTimeOffset? soonest = null;
        foreach (var trigger in triggers)
        {
            var next = Next(trigger, after);
            if (next == null) continue;
            if (soonest == null || next < soonest) soonest = next;
        }
        return soonest;
    }

    /// <summary>The next firing of one trigger strictly after <paramref name="after"/>.</summary>
    public static DateTimeOffset? Next(TriggerDefinition trigger, DateTimeOffset after)
    {
        if (!trigger.Enabled) return null;

        switch (trigger.Kind)
        {
            case TriggerKind.OneShot:
                return trigger.FireAtUtc > after ? trigger.FireAtUtc : null;

            case TriggerKind.Interval:
            {
                var seconds = trigger.IntervalSeconds ?? 0;
                if (seconds <= 0) return null;
                var anchor = trigger.AnchorUtc ?? after;
                if (anchor > after) return anchor;
                // Land on the anchor's grid rather than "now + interval", so an hourly job stays
                // on the hour instead of drifting a little later after every restart.
                var elapsed = after - anchor;
                var steps = (long)Math.Floor(elapsed.TotalSeconds / seconds) + 1;
                return anchor.AddSeconds(steps * seconds);
            }

            case TriggerKind.Cron:
            {
                if (!CronSchedule.TryParse(trigger.CronExpression, out var cron, out _)) return null;
                var zone = ResolveZone(trigger.TimeZoneId);
                return cron!.Next(after, zone);
            }

            default:
                return null;
        }
    }

    /// <summary>Entries an upstream run's outcome should start.</summary>
    public static IEnumerable<ScheduleEntry> Dependents(
        IReadOnlyList<ScheduleEntry> entries, string finishedEntryId, bool succeeded) =>
        entries.Where(e => e.Enabled && e.Triggers.Any(t =>
            t.Enabled
            && t.Kind == TriggerKind.AfterEntry
            && t.AfterEntryId == finishedEntryId
            && t.RequiredOutcome switch
            {
                UpstreamOutcome.Succeeded => succeeded,
                UpstreamOutcome.Failed => !succeeded,
                _ => true,
            }));

    /// <summary>
    /// The chain of entries an outcome sets off, in order, stopping at a cycle.
    /// <para>
    /// Chains are followed rather than forbidden — "after the ingest, reconcile; after that,
    /// publish" is the whole point — but an entry may appear only once per chain, so a loop
    /// exhausts itself immediately instead of running forever.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScheduleEntry> Chain(
        IReadOnlyList<ScheduleEntry> entries, string startedFromEntryId, bool succeeded)
    {
        var ordered = new List<ScheduleEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { startedFromEntryId };
        var queue = new Queue<(string EntryId, bool Succeeded)>();
        queue.Enqueue((startedFromEntryId, succeeded));

        while (queue.Count > 0)
        {
            var (entryId, outcome) = queue.Dequeue();
            foreach (var dependent in Dependents(entries, entryId, outcome))
            {
                if (!seen.Add(dependent.Id)) continue;
                ordered.Add(dependent);
                // A dependent's own dependents only make sense once it has run; assume success so
                // the chain can be shown ahead of time.
                queue.Enqueue((dependent.Id, true));
            }
        }
        return ordered;
    }

    /// <summary>
    /// Whether saving <paramref name="candidate"/> would close a loop of after-triggers, in which
    /// every entry sits waiting for another entry in the same loop and none of them ever starts.
    /// <para>
    /// The question is asked FORWARD FROM THE CANDIDATE, not from the entries it follows: if
    /// something the candidate sets off is already what the candidate is waiting for, the link
    /// closes the ring. Asking it the other way round — walking down from the upstream entry and
    /// looking for the candidate — cannot work, because the candidate is new or is the very entry
    /// being replaced, so it is never in the list being walked and the answer is always "no".
    /// </para>
    /// </summary>
    public static bool WouldFormACycle(IReadOnlyList<ScheduleEntry> saved, ScheduleEntry candidate)
    {
        var upstreamIds = candidate.Triggers
            .Where(t => t.Kind == TriggerKind.AfterEntry && !string.IsNullOrWhiteSpace(t.AfterEntryId))
            .Select(t => t.AfterEntryId!)
            .ToHashSet(StringComparer.Ordinal);
        if (upstreamIds.Count == 0) return false;

        // The candidate stands in for whatever version of it is on disk: a loop is a property of
        // the triggers about to be saved, not of the ones already there.
        var withCandidate = saved.Where(e => e.Id != candidate.Id).Append(candidate).ToList();

        // Structural, unlike Chain and Dependents: every after-link is followed whatever outcome
        // it wants and whether or not its entry is switched on. A ring made of "after this fails"
        // links is still a ring, and one that is only broken by a disabled entry becomes real the
        // moment somebody ticks that box — by which point nothing is asking this question.
        var seen = new HashSet<string>(StringComparer.Ordinal) { candidate.Id };
        var queue = new Queue<string>();
        queue.Enqueue(candidate.Id);
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var entry in withCandidate)
            {
                if (!entry.Triggers.Any(t => t.Kind == TriggerKind.AfterEntry && t.AfterEntryId == from))
                    continue;
                if (upstreamIds.Contains(entry.Id)) return true;
                if (seen.Add(entry.Id)) queue.Enqueue(entry.Id);
            }
        }
        return false;
    }

    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = span.Negate();
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }
}

namespace Automata.Core.Automation.Scheduling;

public enum TriggerKind
{
    /// <summary>Only when someone asks. The default, and what every existing task has.</summary>
    Manual,

    /// <summary>A cron expression, evaluated in <see cref="TriggerDefinition.TimeZoneId"/>.</summary>
    Cron,

    /// <summary>Every N seconds from an anchor.</summary>
    Interval,

    /// <summary>Once, at a fixed instant.</summary>
    OneShot,

    /// <summary>After another schedule entry finishes.</summary>
    AfterEntry,
}

/// <summary>Which outcome of the upstream run counts as "finished".</summary>
public enum UpstreamOutcome { Succeeded, Failed, Completed }

/// <summary>What to do about a firing that was missed because nothing was running.</summary>
public enum CatchUpPolicy
{
    /// <summary>Forget it and wait for the next one. The safe default: a batch of missed runs
    /// firing all at once after a machine was off is rarely what anyone wanted.</summary>
    Skip,

    /// <summary>Run once, promptly, then resume the normal cadence.</summary>
    RunOnceImmediately,
}

public sealed class TriggerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public TriggerKind Kind { get; set; } = TriggerKind.Manual;
    public bool Enabled { get; set; } = true;

    /// <summary>Cron: the expression, and the zone it is read in (null = this machine's zone).</summary>
    public string? CronExpression { get; set; }
    public string? TimeZoneId { get; set; }

    /// <summary>Interval: seconds between firings, counted from <see cref="AnchorUtc"/>.</summary>
    public int? IntervalSeconds { get; set; }
    public DateTimeOffset? AnchorUtc { get; set; }

    /// <summary>OneShot: when.</summary>
    public DateTimeOffset? FireAtUtc { get; set; }

    /// <summary>AfterEntry: whose completion starts this, and which outcome counts.</summary>
    public string? AfterEntryId { get; set; }
    public UpstreamOutcome RequiredOutcome { get; set; } = UpstreamOutcome.Succeeded;

    public CatchUpPolicy CatchUp { get; set; } = CatchUpPolicy.Skip;
}

public enum ScheduleTargetKind { Collection, Task }

/// <summary>
/// One scheduled thing: what to run, and what starts it.
/// <para>
/// Kept apart from <c>Collection</c> and <c>TaskDefinition</c> on purpose. Those files are
/// hand-editable in Explorer and describe what a workflow *is*; when it runs is a different
/// concern with a different lifetime, and mixing them would make every collection.json carry
/// scheduler bookkeeping it does not need.
/// </para>
/// </summary>
public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public ScheduleTargetKind Target { get; set; } = ScheduleTargetKind.Collection;

    /// <summary>Id of the collection or task to run.</summary>
    public string TargetId { get; set; } = "";

    public List<TriggerDefinition> Triggers { get; set; } = [];

    /// <summary>Bookkeeping the scheduler maintains; not user-authored.</summary>
    public DateTimeOffset? NextDueUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastOutcome { get; set; }
}

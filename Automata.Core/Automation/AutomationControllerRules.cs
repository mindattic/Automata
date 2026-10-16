using System.Text.Json;
using System.Text.Json.Nodes;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using AutoWebNav;

namespace Automata.Core.Automation;

/// <summary>
/// The rules <c>AutomationController</c> applies before anything reaches the scheduler, the
/// settings store, or a saved task's step tree. Pulled out of that WPF-hosted class — which has
/// no tests of its own — so each rule can be exercised directly instead of only through the panel
/// that calls it.
/// </summary>
public static class AutomationControllerRules
{
    /// <summary>
    /// How many triggers one schedule entry may carry. Matches the sidebar's own cap, and exists
    /// so a hand-edited schedule.json cannot be saved back as something unreadable.
    /// </summary>
    public const int MaxTriggersPerEntry = 8;

    /// <summary>
    /// The reason an entry cannot be saved, or null when it is sound.
    /// </summary>
    /// <param name="entry">The entry being saved.</param>
    /// <param name="saved">
    /// The entry as it currently stands in the store, or null when this is a new one. Consulted
    /// for one rule only — see the one-shot case below — and deliberately not for anything else:
    /// every other check is about the entry being saved, not about how it got that way.
    /// </param>
    /// <param name="entries">Every entry currently in the store, used for AfterEntry existence and
    /// cycle detection.</param>
    /// <param name="clock">The current time, for cron/one-shot evaluation.</param>
    /// <param name="targetExists">Whether <paramref name="entry"/>'s task or collection target can
    /// still be found. Kept as a delegate rather than a store dependency so this rule stays pure.</param>
    public static string? ValidateScheduleEntry(
        ScheduleEntry entry,
        ScheduleEntry? saved,
        IReadOnlyList<ScheduleEntry> entries,
        IClock clock,
        Func<ScheduleEntry, bool> targetExists)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return "A schedule needs a name.";
        if (!targetExists(entry))
            return entry.Target == ScheduleTargetKind.Task
                ? "Pick a task for this schedule to run."
                : "Pick a collection for this schedule to run.";
        if (entry.Triggers.Count == 0) return "A schedule needs at least one trigger.";
        // Several triggers are the point — "every weekday at 09:00 or once the ingest finishes" —
        // but a bound keeps a hand-edited or scripted entry from turning into something nobody can
        // read, and every one of them is evaluated on every tick.
        if (entry.Triggers.Count > MaxTriggersPerEntry)
            return $"A schedule can have at most {MaxTriggersPerEntry} triggers; past that it is " +
                   "easier to read as two schedules.";

        foreach (var trigger in entry.Triggers)
        {
            switch (trigger.Kind)
            {
                case TriggerKind.Cron:
                    if (!CronSchedule.TryParse(trigger.CronExpression, out _, out var cronError))
                        return $"That cron expression won't work: {cronError}.";
                    if (TriggerEvaluator.Next(trigger, clock.UtcNow) == null)
                        return "That cron expression never matches a real date — nothing would ever run.";
                    break;

                case TriggerKind.Interval:
                    if (trigger.IntervalSeconds is not > 0)
                        return "An interval needs to be at least one minute.";
                    break;

                case TriggerKind.OneShot:
                    if (trigger.FireAtUtc == null) return "Pick the date and time to run once at.";
                    // Refused only when the edit is what PUT it in the past. Checked unconditionally
                    // it made a one-off that had already fired unsaveable for good: the entry could
                    // not be renamed, given a second trigger, or switched off from the panel that
                    // created it, because saving it back re-offered a time nobody was changing.
                    if (trigger.FireAtUtc <= clock.UtcNow
                        && TriggerEvaluator.IsNewTrigger(saved, trigger))
                        return "That one-off time has already passed — pick a later one.";
                    break;

                case TriggerKind.AfterEntry:
                    if (string.IsNullOrWhiteSpace(trigger.AfterEntryId))
                        return "Pick the schedule this one should follow.";
                    if (trigger.AfterEntryId == entry.Id)
                        return "A schedule cannot wait for itself to finish.";
                    if (entries.All(e => e.Id != trigger.AfterEntryId))
                        return "The schedule this one follows no longer exists.";
                    break;
            }
        }

        // A chain is allowed — "after the ingest, reconcile" is the point — but a cycle is not,
        // because every entry in one would sit waiting for another entry in the same loop and none
        // of them would ever start.
        if (TriggerEvaluator.WouldFormACycle(entries, entry))
            return "That would make a loop — the schedule it follows already waits for this one.";

        return null;
    }

    /// <summary>
    /// Deserializes a scope's engine overrides, collapsing one that overrides nothing to null so
    /// an untouched entity never gains an empty settings node on disk.
    /// </summary>
    public static EngineSettingsOverride? ParseOverride(JsonNode? node)
    {
        if (node == null) return null;
        var parsed = JsonSerializer.Deserialize<EngineSettingsOverride>(node.ToJsonString(), AutomataJson.Options);
        return parsed is null || parsed.IsEmpty ? null : parsed;
    }

    /// <summary>Maps AutoWebNav's portable, app-agnostic recording output onto Automata's own
    /// richer <see cref="Step"/> (flow control, bindings and task-tree fields default; recording
    /// never touches them). A new <see cref="Step.Id"/> is assigned by <see cref="Step"/>'s own
    /// default constructor, exactly as it was when these steps were built in place in the
    /// controller.</summary>
    public static Step ToAutomataStep(RecordedStep r) => new()
    {
        Action = r.Kind switch
        {
            RecordedStepKind.Navigate => StepAction.Navigate,
            RecordedStepKind.Click => StepAction.Click,
            RecordedStepKind.TypeText => StepAction.TypeText,
            RecordedStepKind.SetValue => StepAction.SetValue,
            RecordedStepKind.PressEnter => StepAction.PressEnter,
            RecordedStepKind.Check => StepAction.Check,
            RecordedStepKind.Uncheck => StepAction.Uncheck,
            RecordedStepKind.SelectRadio => StepAction.SelectRadio,
            RecordedStepKind.SelectOption => StepAction.SelectOption,
            RecordedStepKind.UploadFile => StepAction.UploadFile,
            _ => throw new ArgumentOutOfRangeException(nameof(r), r.Kind, "Unmapped recorded step kind."),
        },
        Label = r.Label,
        Target = r.Target,
        Value = r.Value,
        Url = r.Url,
        Masked = r.Masked,
        IsCommitPoint = r.IsCommitPoint,
    };
}

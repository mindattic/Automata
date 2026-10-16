using System.Text.Json.Nodes;
using Automata.Core.Automation;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Automata.Tests.Fakes;
using AutoWebNav;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers the rules <see cref="AutomationControllerRules"/> pulled out of
/// <c>AutomationController</c> — what guards a schedule entry before it reaches the scheduler, an
/// engine override before it reaches disk, and a recorded step before it joins a task tree.
/// </summary>
[TestFixture]
public class AutomationControllerRulesTests
{
    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static readonly FakeClock Clock = new(At(2026, 5, 4, 7, 0));

    private static ScheduleEntry Entry(string id = "e1", params TriggerDefinition[] triggers) => new()
    {
        Id = id, Name = "Nightly", Target = ScheduleTargetKind.Collection, TargetId = "c1",
        Triggers = [.. triggers],
    };

    private static TriggerDefinition Cron(string expression) => new()
    {
        Kind = TriggerKind.Cron, CronExpression = expression, TimeZoneId = "UTC",
    };

    private static TriggerDefinition Interval(int? seconds) => new()
    {
        Kind = TriggerKind.Interval, IntervalSeconds = seconds,
    };

    private static TriggerDefinition OneShot(DateTimeOffset? fireAt) => new()
    {
        Kind = TriggerKind.OneShot, FireAtUtc = fireAt,
    };

    private static TriggerDefinition AfterEntry(string? afterEntryId) => new()
    {
        Kind = TriggerKind.AfterEntry, AfterEntryId = afterEntryId,
    };

    private static string? Validate(
        ScheduleEntry entry, ScheduleEntry? saved = null, IReadOnlyList<ScheduleEntry>? entries = null,
        Func<ScheduleEntry, bool>? targetExists = null) =>
        AutomationControllerRules.ValidateScheduleEntry(
            entry, saved, entries ?? [], Clock, targetExists ?? (_ => true));

    // ---- schedule entry validation ---------------------------------------------------------

    [Test]
    public void ASoundEntryIsValid()
    {
        Assert.That(Validate(Entry("e1", Cron("0 9 * * *"))), Is.Null);
    }

    [Test]
    public void AMissingNameIsRefused()
    {
        var entry = Entry("e1", Cron("0 9 * * *"));
        entry.Name = " ";

        Assert.That(Validate(entry), Does.Contain("needs a name"));
    }

    [Test]
    public void AMissingTaskTargetIsRefused()
    {
        var entry = Entry("e1", Cron("0 9 * * *"));
        entry.Target = ScheduleTargetKind.Task;

        var result = Validate(entry, targetExists: _ => false);

        Assert.That(result, Is.EqualTo("Pick a task for this schedule to run."));
    }

    [Test]
    public void AMissingCollectionTargetIsRefused()
    {
        var entry = Entry("e1", Cron("0 9 * * *"));
        entry.Target = ScheduleTargetKind.Collection;

        var result = Validate(entry, targetExists: _ => false);

        Assert.That(result, Is.EqualTo("Pick a collection for this schedule to run."));
    }

    [Test]
    public void ZeroTriggersAreRefused()
    {
        Assert.That(Validate(Entry("e1")), Does.Contain("at least one trigger"));
    }

    [Test]
    public void TooManyTriggersAreRefused()
    {
        var triggers = Enumerable.Range(0, AutomationControllerRules.MaxTriggersPerEntry + 1)
            .Select(_ => Cron("0 9 * * *")).ToArray();

        Assert.That(Validate(Entry("e1", triggers)), Does.Contain("at most"));
    }

    [Test]
    public void ABadCronExpressionIsRefused()
    {
        Assert.That(Validate(Entry("e1", Cron("not a cron"))), Does.Contain("won't work"));
    }

    [Test]
    public void ACronThatNeverMatchesIsRefused()
    {
        // February never has a 31st.
        Assert.That(Validate(Entry("e1", Cron("0 0 31 2 *"))), Does.Contain("never matches a real date"));
    }

    [Test]
    public void AnIntervalUnderOneMinuteIsRefused()
    {
        Assert.That(Validate(Entry("e1", Interval(0))), Does.Contain("at least one minute"));
    }

    [Test]
    public void AMissingOneShotTimeIsRefused()
    {
        Assert.That(Validate(Entry("e1", OneShot(null))), Does.Contain("Pick the date and time"));
    }

    [Test]
    public void ANewOneShotInThePastIsRefused()
    {
        var entry = Entry("e1", OneShot(Clock.UtcNow.AddDays(-1)));

        Assert.That(Validate(entry, saved: null), Does.Contain("already passed"));
    }

    [Test]
    public void AOneShotAlreadySavedInThePastIsLeftAlone()
    {
        var trigger = OneShot(Clock.UtcNow.AddDays(-1));
        var entry = Entry("e1", trigger);
        var saved = Entry("e1", trigger);

        Assert.That(Validate(entry, saved: saved), Is.Null);
    }

    [Test]
    public void AnAfterEntryWithNoTargetIsRefused()
    {
        Assert.That(Validate(Entry("e1", AfterEntry(null))), Does.Contain("Pick the schedule"));
    }

    [Test]
    public void AnAfterEntryThatFollowsItselfIsRefused()
    {
        Assert.That(Validate(Entry("e1", AfterEntry("e1"))), Does.Contain("wait for itself"));
    }

    [Test]
    public void AnAfterEntryWhoseTargetIsGoneIsRefused()
    {
        var entries = new List<ScheduleEntry> { Entry("e1", AfterEntry("missing")) };

        Assert.That(Validate(entries[0], entries: entries), Does.Contain("no longer exists"));
    }

    [Test]
    public void ACycleOfAfterEntriesIsRefused()
    {
        // e2 already waits for e1; saving e1 to wait for e2 would close the loop.
        var e2 = Entry("e2", AfterEntry("e1"));
        var e1 = Entry("e1", AfterEntry("e2"));

        var result = Validate(e1, entries: [e2, e1]);

        Assert.That(result, Does.Contain("loop"));
    }

    // ---- engine override parsing ------------------------------------------------------------

    [Test]
    public void ANullNodeParsesToNull()
    {
        Assert.That(AutomationControllerRules.ParseOverride(null), Is.Null);
    }

    [Test]
    public void AnOverrideThatSetsNothingCollapsesToNull()
    {
        var node = JsonNode.Parse("{}");

        Assert.That(AutomationControllerRules.ParseOverride(node), Is.Null);
    }

    [Test]
    public void AnOverrideThatSetsSomethingSurvives()
    {
        var node = JsonNode.Parse("""{"selfHeal": true}""");

        var parsed = AutomationControllerRules.ParseOverride(node);

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.SelfHeal, Is.True);
    }

    // ---- recorded step mapping --------------------------------------------------------------

    [Test]
    public void ARecordedClickMapsToAClickStep()
    {
        var step = AutomationControllerRules.ToAutomataStep(new RecordedStep
        {
            Kind = RecordedStepKind.Click, Label = "Submit",
        });

        Assert.That(step.Action, Is.EqualTo(StepAction.Click));
        Assert.That(step.Label, Is.EqualTo("Submit"));
    }

    [Test]
    public void AnUnmappedRecordedStepKindThrows()
    {
        var recorded = new RecordedStep { Kind = (RecordedStepKind)(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => AutomationControllerRules.ToAutomataStep(recorded));
    }
}

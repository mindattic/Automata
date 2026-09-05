using System.IO;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Parking: a wait too long to hold a browser through checkpoints the run and lets the lane go, and
/// a later tick picks it up from the step after the wait.
/// <para>
/// Every case here runs against a fixed clock. The subject is entirely "how long is left", so a
/// test that read the real clock would either have to wait hours or assert something weaker than
/// the thing that matters.
/// </para>
/// </summary>
[TestFixture]
public class ParkAndResumeTests
{
    private string root = null!;
    private CollectionStore collections = null!;
    private DatasetStore datasets = null!;

    /// <summary>Midnight, so a 09:00 wait is nine hours out and a 00:05 wait is five minutes.</summary>
    private static readonly DateTimeOffset Midnight = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
        datasets = new DatasetStore(Path.Combine(root, "datasets"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private WorkflowEngine Engine() =>
        new(new ReplayEngine(new FingerprintResolver { PollIntervalMs = 10 }), collections, datasets);

    private static ReplayOptions Options(bool allowParking = true) => new()
    {
        DefaultStepTimeoutMs = 300,
        SettlePollMs = 1,
        Control = new ReplayControl(),
        AllowParking = allowParking,
        Clock = () => Midnight,
    };

    private static FakeBrowserSurface Browser() => new()
    {
        DefaultEvalResponse = script =>
            script.Contains("isProcessing") ? """{ "isProcessing": false }""" : "{}",
    };

    private static Step Nav(string id) =>
        new() { Id = id, Action = StepAction.Navigate, Label = id, Url = "https://x.example" };

    /// <summary>A wait until 09:00 UTC — nine hours from the fixed clock, well past the default
    /// 15-minute park threshold.</summary>
    private static Step LongWait(string id = "w") => new()
    {
        Id = id, Action = StepAction.Wait, Label = "Wait until 09:00",
        Wait = new WaitSpec { Mode = WaitMode.UntilTimeOfDay, TimeOfDay = new TimeOnly(9, 0), TimeZoneId = "UTC" },
    };

    private async Task<List<StepEvent>> Run(
        TaskDefinition task, FakeBrowserSurface browser, ReplayOptions? options = null,
        ParkCheckpoint? resume = null)
    {
        var events = new List<StepEvent>();
        await foreach (var evt in Engine().RunAsync(task, options ?? Options(), browser, default, resume))
            events.Add(evt);
        return events;
    }

    private static List<string> NavigatedUrls(FakeBrowserSurface browser) =>
        browser.Calls.Where(c => c.Method == "Navigate").Select(c => c.Args).ToList();

    // ---- parking --------------------------------------------------------------------------------

    [Test]
    public async Task ALongWait_ParksInsteadOfHoldingTheBrowser()
    {
        var task = new TaskDefinition { Name = "Nightly", Steps = [Nav("a"), LongWait(), Nav("c")] };

        var events = await Run(task, Browser());

        var parked = events.OfType<StepEvent.RunParked>().Single();
        Assert.That(parked.Checkpoint.ResumeAtUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
        Assert.That(parked.Checkpoint.ResumePath, Is.EqualTo(new[] { 1 }));
        Assert.That(parked.Checkpoint.ResumeStepId, Is.EqualTo("w"));
        Assert.That(parked.Checkpoint.Passed, Is.EqualTo(1), "the step before the wait had passed");

        // Neither passed nor failed: the run is unfinished, and saying otherwise would report an
        // outcome that has not happened.
        Assert.That(events.OfType<StepEvent.RunCompleted>(), Is.Empty);
        // And it stopped there rather than running the step after the wait.
        Assert.That(NavigatedUrls(Browser()), Is.Empty);
        Assert.That(events.OfType<StepEvent.StepCompleted>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AShortWait_DoesNotPark()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "w", Action = StepAction.Wait, Label = "Brief",
                    Wait = new WaitSpec { Mode = WaitMode.Duration, DurationMs = 5 },
                },
            ],
        };

        var events = await Run(task, Browser());

        Assert.That(events.OfType<StepEvent.RunParked>(), Is.Empty);
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task ParkAfterZero_HoldsTheBrowserInstead()
    {
        // How a task that must keep what it did before the wait opts out: parking resets the page,
        // so a run that logged in first has to hold its browser.
        var wait = LongWait();
        wait.Wait!.ParkAfterMs = 0;
        wait.Wait.Mode = WaitMode.Duration;
        wait.Wait.DurationMs = 5;
        var task = new TaskDefinition { Name = "T", Steps = [wait] };

        var events = await Run(task, Browser());

        Assert.That(events.OfType<StepEvent.RunParked>(), Is.Empty);
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task WhenParkingIsNotAllowed_TheRunSaysTheBrowserStaysOccupied()
    {
        var wait = LongWait();
        // A long wait the test can actually sit through: the point is the explanation, not the wait.
        wait.Wait = new WaitSpec { Mode = WaitMode.Duration, DurationMs = 5, ParkAfterMs = 1 };
        var task = new TaskDefinition { Name = "T", Steps = [wait] };

        var events = await Run(task, Browser(), Options(allowParking: false));

        Assert.That(events.OfType<StepEvent.RunParked>(), Is.Empty);
        Assert.That(
            events.OfType<StepEvent.Log>().Select(l => l.Message),
            Has.Some.Contains("cannot be resumed later"),
            "a run that cannot park must say the browser is held, not silently hang");
    }

    [Test]
    public async Task AWaitInsideAForEach_HoldsTheBrowserAndSaysWhy()
    {
        datasets.Write("rows.csv", [new Dictionary<string, string> { ["sku"] = "a" }], append: false);
        var wait = LongWait();
        wait.Wait = new WaitSpec { Mode = WaitMode.Duration, DurationMs = 5, ParkAfterMs = 1 };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach, Label = "Each row",
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "rows.csv" },
                        RowVariableName = "row",
                    },
                    Children = [wait],
                },
            ],
        };

        var events = await Run(task, Browser());

        Assert.That(events.OfType<StepEvent.RunParked>(), Is.Empty,
            "a checkpoint cannot say which row a loop had reached, so a wait in one must not park");
        Assert.That(
            events.OfType<StepEvent.Log>().Select(l => l.Message),
            Has.Some.Contains("inside a loop or a called task"));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task AWaitInsideAnIf_StillParks_AndRecordsThePathToIt()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "branch", Action = StepAction.If, Label = "If always",
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.Literal, Literal = "yes" },
                        Op = ConditionOp.NotEmpty,
                    },
                    Children = [LongWait("inner")],
                },
            ],
        };

        var events = await Run(task, Browser());

        var parked = events.OfType<StepEvent.RunParked>().Single();
        Assert.That(parked.Checkpoint.ResumePath, Is.EqualTo(new[] { 0, 0 }),
            "an `if` is one branch taken once, so a wait inside it has an address a resume can re-enter");
        Assert.That(parked.Checkpoint.ResumeStepId, Is.EqualTo("inner"));
    }

    [Test]
    public async Task ACheckpoint_CarriesTheValuesEarlierStepsPublished()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
                script.Contains("isProcessing") ? """{ "isProcessing": false }"""
                : script.Contains("__automataResolve(")
                    ? """
                      { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
                        "candidateCount": 1, "centerX": 1, "centerY": 2, "tag": "span", "text": "42" }
                      """
                : script.Contains("textContent") ? """{ "ok": true, "value": "42" }"""
                : "{}",
        };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Label = "Read total",
                    Target = new ElementFingerprint { Tag = "span", CssSelector = "#total" },
                    Outputs = [new OutputField { Name = "total" }],
                },
                LongWait(),
            ],
        };

        var events = await Run(task, browser);

        var parked = events.OfType<StepEvent.RunParked>().Single();
        Assert.That(parked.Checkpoint.Outputs.Select(o => (o.StepId, o.Field, o.Value)),
            Does.Contain(("read", "total", "42")),
            "a value captured before the wait has to survive it, or every binding after the wait breaks");
    }

    /// <summary>
    /// "First write of the run" has to mean the whole run, including the half that happens hours
    /// later. A resumed run that forgot which datasets it had already started fresh would clear one
    /// it spent the first half filling — and the run would report success while holding half its
    /// results.
    /// </summary>
    [Test]
    public async Task ACheckpoint_CarriesTheDatasetsTheRunHadAlreadyStartedFresh()
    {
        var task = new TaskDefinition { Name = "T", Steps = [Collect("first"), LongWait()] };

        var events = await Run(task, Browser());

        Assert.That(events.OfType<StepEvent.RunParked>().Single().Checkpoint.FreshenedDatasets,
            Does.Contain("collected.csv"));
    }

    [Test]
    public async Task Resuming_DoesNotClearADatasetTheRunHadAlreadyStartedFresh()
    {
        var task = new TaskDefinition { Name = "T", Steps = [Collect("first"), LongWait(), Collect("second")] };

        var parked = await Run(task, Browser());
        var checkpoint = parked.OfType<StepEvent.RunParked>().Single().Checkpoint;
        Assert.That(datasets.Read("collected.csv"), Has.Count.EqualTo(1), "the first half wrote its row");

        await Run(task, Browser(), Options(), checkpoint);

        var rows = datasets.Read("collected.csv");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2), "resuming cleared what the run had already collected");
            Assert.That(rows.Select(r => r["half"]), Is.EqualTo(new[] { "first", "second" }));
        });
    }

    /// <summary>A writeDataset step that starts collected.csv fresh on the run's first write.</summary>
    private static Step Collect(string half) => new()
    {
        Id = half, Action = StepAction.WriteDataset, Label = $"Record the {half} half",
        WriteDataset = new DatasetWriteSpec
        {
            DatasetName = "collected.csv",
            Append = true,
            ResetOnFirstWrite = true,
            Columns = new Dictionary<string, BindingRef>
            {
                ["half"] = new() { Kind = BindingKind.Literal, Literal = half },
            },
        },
    };

    // ---- resuming -------------------------------------------------------------------------------

    [Test]
    public async Task Resuming_CarriesOnAfterTheWaitWithoutRerunningWhatCameBefore()
    {
        var task = new TaskDefinition
        {
            Name = "T", StartUrl = "https://start.example",
            Steps = [Nav("a"), LongWait(), Nav("c")],
        };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait until 09:00 (UTC)", [1], "w", "Wait until 09:00", [], new Dictionary<string, string>(), 1, 0);

        var browser = Browser();
        var events = await Run(task, browser, Options(), checkpoint);

        // The start URL, then the step AFTER the wait — never the one before it.
        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[] { "https://start.example", "https://x.example" }));
        var completed = events.OfType<StepEvent.RunCompleted>().Single();
        Assert.That(completed.Success, Is.True);
        Assert.That(completed.Summary, Does.Contain("2 step(s) passed"),
            "the summary counts the whole run, not only the part after the wait");
    }

    [Test]
    public async Task Resuming_SaysThePageWasResetBecauseTheBrowserWasReleased()
    {
        var task = new TaskDefinition { Name = "T", StartUrl = "https://start.example", Steps = [Nav("a"), LongWait()] };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait until 09:00 (UTC)", [1], "w", "Wait until 09:00", [], new Dictionary<string, string>(), 1, 0);

        var events = await Run(task, Browser(), Options(), checkpoint);

        Assert.That(
            events.OfType<StepEvent.Log>().Select(l => l.Message),
            Has.Some.Contains("no longer applies"),
            "parking discards page state, and a resumed run must say so rather than let it be discovered");
    }

    [Test]
    public async Task Resuming_AnEditedTask_RefusesRatherThanResumingIntoTheWrongStep()
    {
        // The checkpoint says index 1 held step "w"; the task now has a different step there.
        var task = new TaskDefinition { Name = "T", Steps = [Nav("a"), Nav("moved-in")] };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait", [1], "w", "Wait", [], new Dictionary<string, string>(), 1, 0);

        var events = await Run(task, Browser(), Options(), checkpoint);

        var completed = events.OfType<StepEvent.RunCompleted>().Single();
        Assert.That(completed.Success, Is.False);
        Assert.That(completed.Summary, Does.Contain("edited"));
        Assert.That(events.OfType<StepEvent.StepStarted>(), Is.Empty, "nothing should have run");
    }

    [Test]
    public async Task Resuming_APathThatNoLongerExists_RefusesToo()
    {
        var task = new TaskDefinition { Name = "T", Steps = [Nav("a")] };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait", [7], "w", "Wait", [], new Dictionary<string, string>(), 1, 0);

        var events = await Run(task, Browser(), Options(), checkpoint);

        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
    }

    [Test]
    public async Task Resuming_IntoAnIf_ReentersTheBranchWithoutReevaluatingIt()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "branch", Action = StepAction.If, Label = "If",
                    // Would NOT hold now. Re-evaluating on resume would skip the rest of the
                    // branch the run was already inside.
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.Literal, Literal = "" },
                        Op = ConditionOp.NotEmpty,
                    },
                    Children = [LongWait("inner"), Nav("after")],
                },
            ],
        };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait", [0, 0], "inner", "Wait", [], new Dictionary<string, string>(), 1, 0);

        var browser = Browser();
        var events = await Run(task, browser, Options(), checkpoint);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[] { "https://x.example" }),
            "the step after the wait, inside the branch, should have run");
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task Resuming_TheValuesFromBeforeTheWaitAreAvailableAgain()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                LongWait(),
                new Step
                {
                    Id = "check", Action = StepAction.If, Label = "If total is 42",
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "total" },
                        Op = ConditionOp.Equals,
                        Right = new BindingRef { Kind = BindingKind.Literal, Literal = "42" },
                    },
                    Children = [Nav("matched")],
                },
            ],
        };
        var checkpoint = new ParkCheckpoint(
            Midnight, "a wait", [0], "w", "Wait",
            [new OutputValue("read", "total", "42")], new Dictionary<string, string>(), 1, 0);

        var browser = Browser();
        await Run(task, browser, Options(), checkpoint);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[] { "https://x.example" }),
            "the restored output should have made the condition hold");
    }

    // ---- the store ------------------------------------------------------------------------------

    [Test]
    public void ParkedRunStore_RoundTripsAndReportsOnlyWhatIsDue()
    {
        var store = new ParkedRunStore(Path.Combine(root, "parked"));
        var soon = Park("soon", Midnight.AddMinutes(-1));
        var later = Park("later", Midnight.AddHours(3));
        store.Save(soon);
        store.Save(later);

        Assert.That(store.List().Select(p => p.RunId), Is.EqualTo(new[] { "soon", "later" }),
            "soonest to resume first");
        Assert.That(store.Due(Midnight).Select(p => p.RunId), Is.EqualTo(new[] { "soon" }));
        Assert.That(store.Get("later")!.Checkpoint.ResumeStepId, Is.EqualTo("w"));

        Assert.That(store.Remove("soon"), Is.True);
        Assert.That(store.Remove("soon"), Is.False);
        Assert.That(store.List().Select(p => p.RunId), Is.EqualTo(new[] { "later" }));
    }

    [Test]
    public void ParkedRunStore_ListingIsEmptyBeforeAnythingParks()
    {
        // A fresh install has no Parked folder at all, and listing must not create one.
        var store = new ParkedRunStore(Path.Combine(root, "never-used"));
        Assert.That(store.List(), Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(root, "never-used")), Is.False);
    }

    private static ParkedRun Park(string runId, DateTimeOffset resumeAt) => new()
    {
        RunId = runId,
        TaskId = "t",
        TaskName = "T",
        TargetName = "T",
        Checkpoint = new ParkCheckpoint(
            resumeAt, "a wait", [1], "w", "Wait", [], new Dictionary<string, string>(), 1, 0),
    };

    // ---- the plan -------------------------------------------------------------------------------

    [Test]
    public void WaitPlan_AConditionWaitHasNoKnowableEnd()
    {
        var (plan, error) = WaitPlan.For(new WaitSpec { Mode = WaitMode.UntilCondition }, Midnight);

        Assert.That(plan, Is.Null);
        Assert.That(error, Is.Null, "not knowing when it ends is not an error — it just cannot be planned around");
    }

    [Test]
    public void WaitPlan_AMissingTimeOfDayIsAnError()
    {
        var (plan, error) = WaitPlan.For(new WaitSpec { Mode = WaitMode.UntilTimeOfDay }, Midnight);

        Assert.That(plan, Is.Null);
        Assert.That(error, Does.Contain("time of day"));
    }

    [Test]
    public void WaitPlan_ShouldParkComparesAgainstTheThreshold()
    {
        var spec = new WaitSpec { ParkAfterMs = 900_000 };

        Assert.That(WaitPlan.ShouldPark(spec, TimeSpan.FromMinutes(14)), Is.False);
        Assert.That(WaitPlan.ShouldPark(spec, TimeSpan.FromMinutes(16)), Is.True);

        // Zero is the documented opt-out, and it has to beat any remaining time at all.
        Assert.That(WaitPlan.ShouldPark(new WaitSpec { ParkAfterMs = 0 }, TimeSpan.FromDays(7)), Is.False);
    }
}

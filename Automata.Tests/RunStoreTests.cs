using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RunStoreTests
{
    private TestDb db = null!;

    [SetUp]
    public void SetUp() => db = new TestDb();

    [TearDown]
    public void TearDown() => db.Dispose();

    /// <summary>A fresh install has no run history, and asking for it must not invent any.</summary>
    [Test]
    public void AFreshDatabaseHasNoRuns()
    {
        Assert.That(db.Runs().ListRuns(), Is.Empty);
    }

    [Test]
    public void CreateRun_RecordsAManifestFindableById()
    {
        var store = db.Runs();

        var run = store.CreateRun(RunTargetKind.Task, "t1", "Wolf Tshirts");

        Assert.Multiple(() =>
        {
            Assert.That(run.RunId, Is.Not.Empty);
            Assert.That(run.Success, Is.Null, "an in-flight run has no outcome yet");
            Assert.That(store.GetRun(run.RunId)!.TargetName, Is.EqualTo("Wolf Tshirts"));
            Assert.That(db.Runs().GetRun(run.RunId)!.Target, Is.EqualTo(RunTargetKind.Task),
                "another store instance — another process — sees it too");
        });
    }

    [Test]
    public void CompleteRun_RecordsTheOutcome()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Collection, "c1", "Google Searches");

        store.CompleteRun(run.RunId, success: false, summary: "1/2 task(s) passed.");

        var back = store.GetRun(run.RunId)!;
        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.False);
            Assert.That(back.Summary, Is.EqualTo("1/2 task(s) passed."));
            Assert.That(back.EndedUtc, Is.Not.Null);
            Assert.That(back.Target, Is.EqualTo(RunTargetKind.Collection));
        });
    }

    /// <summary>
    /// The value ExtractText captures used to reach the log and stop there. Now it lands somewhere
    /// a later run — or the sidebar — can read it back.
    /// </summary>
    [Test]
    public void Outputs_RoundTripPerTask()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.SaveOutputs(run.RunId, "t1", new Dictionary<string, Dictionary<string, string>>
        {
            ["step-a"] = new() { ["price"] = "$19.99" },
            ["step-b"] = new() { ["title"] = "Wolf tee" },
        });

        var back = store.LoadOutputs(run.RunId, "t1");
        Assert.Multiple(() =>
        {
            Assert.That(back["step-a"]["price"], Is.EqualTo("$19.99"));
            Assert.That(back["step-b"]["title"], Is.EqualTo("Wolf tee"));
        });
    }

    [Test]
    public void SaveOutputs_ReplacesRatherThanAccumulates()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.SaveOutputs(run.RunId, "t1", new Dictionary<string, Dictionary<string, string>>
        {
            ["step-a"] = new() { ["price"] = "1" },
        });
        store.SaveOutputs(run.RunId, "t1", new Dictionary<string, Dictionary<string, string>>
        {
            ["step-a"] = new() { ["price"] = "2" },
        });

        Assert.That(store.LoadOutputs(run.RunId, "t1")["step-a"]["price"], Is.EqualTo("2"));
    }

    [Test]
    public void LoadOutputs_ForATaskThatNeverRanIsEmpty()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        Assert.That(store.LoadOutputs(run.RunId, "never-ran"), Is.Empty);
    }

    [Test]
    public void AppendEvent_RecordsOneJsonLinePerEvent_InOrder()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.AppendEvent(run.RunId, "t1", new { kind = "stepStarted", stepId = "s1" });
        store.AppendEvent(run.RunId, "t1", new { kind = "stepCompleted", stepId = "s1", status = "passed" });

        var lines = store.LoadEvents(run.RunId, "t1");
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[0], Does.Contain("stepStarted"));
            Assert.That(lines[1], Does.Contain("passed"));
            Assert.That(store.TaskIds(run.RunId), Is.EqualTo(new[] { "t1" }));
        });
    }

    /// <summary>A multi-line value must not break the one-event-per-line contract.</summary>
    [Test]
    public void AppendEvent_KeepsAMultiLineValueOnASingleLine()
    {
        var store = db.Runs();
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.AppendEvent(run.RunId, "t1", new { message = "line one\nline two" });

        var lines = store.LoadEvents(run.RunId, "t1");
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Not.Contain("\n"));
    }

    [Test]
    public void ListRuns_ReturnsNewestFirst()
    {
        var store = db.Runs();
        var first = store.CreateRun(RunTargetKind.Task, "t1", "First");
        Thread.Sleep(20);
        var second = store.CreateRun(RunTargetKind.Task, "t2", "Second");

        var runs = store.ListRuns();

        Assert.That(runs.Select(r => r.RunId).Take(2), Is.EqualTo(new[] { second.RunId, first.RunId }));
    }

    [Test]
    public void ListRuns_HonoursTheLimit()
    {
        var store = db.Runs();
        for (var i = 0; i < 3; i++) store.CreateRun(RunTargetKind.Task, "t" + i, "Run " + i);

        Assert.That(store.ListRuns(limit: 2), Has.Count.EqualTo(2));
    }

    [Test]
    public void UnknownRunIdsAreReportedRatherThanGuessed()
    {
        var store = db.Runs();
        store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        // Writes against a run that does not exist are dropped, not attached to something else.
        store.AppendEvent("nope", "t1", new { kind = "x" });
        store.SaveOutputs("nope", "t1", new Dictionary<string, Dictionary<string, string>> { ["s"] = new() { ["v"] = "1" } });
        store.CompleteRun("nope", true, "done");

        Assert.Multiple(() =>
        {
            Assert.That(store.GetRun("nope"), Is.Null);
            Assert.That(store.LoadEvents("nope", "t1"), Is.Empty);
            Assert.That(store.LoadOutputs("nope", "t1"), Is.Empty);
        });
    }

    [Test]
    public void ImportRun_KeepsItsIdentityAndNeverOverwritesHistory()
    {
        var store = db.Runs();
        var manifest = new RunManifest
        {
            RunId = "r1", Target = RunTargetKind.Task, TargetId = "t1", TargetName = "Old",
            StartedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Success = true,
        };
        var task = new ImportedRunTask("t1", ["{\"kind\":\"a\"}"],
            new Dictionary<string, Dictionary<string, string>> { ["s"] = new() { ["v"] = "1" } });

        Assert.That(store.ImportRun(manifest, [task]), Is.True);
        Assert.That(store.ImportRun(new RunManifest { RunId = "r1", TargetName = "Other" }, []), Is.False);

        var back = store.GetRun("r1")!;
        Assert.Multiple(() =>
        {
            Assert.That(back.TargetName, Is.EqualTo("Old"));
            Assert.That(back.StartedUtc, Is.EqualTo(manifest.StartedUtc));
            Assert.That(store.LoadEvents("r1", "t1"), Is.EqualTo(new[] { "{\"kind\":\"a\"}" }));
            Assert.That(store.LoadOutputs("r1", "t1")["s"]["v"], Is.EqualTo("1"));
        });
    }
}

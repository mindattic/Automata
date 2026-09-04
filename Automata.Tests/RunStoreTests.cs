using System.IO;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RunStoreTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Nothing exists until a run actually starts — a fresh install must not grow a Runs tree just
    /// by opening the app.
    /// </summary>
    [Test]
    public void ConstructingTheStoreCreatesNothingOnDisk()
    {
        _ = new RunStore(root);

        Assert.That(Directory.Exists(root), Is.False);
        Assert.That(new RunStore(root).ListRuns(), Is.Empty);
    }

    [Test]
    public void CreateRun_WritesAManifestAndIsFindableById()
    {
        var store = new RunStore(root);

        var run = store.CreateRun(RunTargetKind.Task, "t1", "Wolf Tshirts");

        Assert.Multiple(() =>
        {
            Assert.That(run.RunId, Is.Not.Empty);
            Assert.That(run.Success, Is.Null, "an in-flight run has no outcome yet");
            Assert.That(store.GetRun(run.RunId)!.TargetName, Is.EqualTo("Wolf Tshirts"));
            Assert.That(File.Exists(Path.Combine(store.DirectoryFor(run.RunId)!, RunStore.ManifestFileName)), Is.True);
        });
    }

    [Test]
    public void CompleteRun_RecordsTheOutcome()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Collection, "c1", "Google Searches");

        store.CompleteRun(run.RunId, success: false, summary: "1/2 task(s) passed.");

        var back = store.GetRun(run.RunId)!;
        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.False);
            Assert.That(back.Summary, Is.EqualTo("1/2 task(s) passed."));
            Assert.That(back.EndedUtc, Is.Not.Null);
        });
    }

    /// <summary>
    /// The value ExtractText captures used to reach the log and stop there. Now it lands somewhere
    /// a later run — or the sidebar — can read it back.
    /// </summary>
    [Test]
    public void Outputs_RoundTripPerTask()
    {
        var store = new RunStore(root);
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
    public void LoadOutputs_ForATaskThatNeverRanIsEmpty()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        Assert.That(store.LoadOutputs(run.RunId, "never-ran"), Is.Empty);
    }

    [Test]
    public void AppendEvent_WritesOneJsonLinePerEvent()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.AppendEvent(run.RunId, "t1", new { kind = "stepStarted", stepId = "s1" });
        store.AppendEvent(run.RunId, "t1", new { kind = "stepCompleted", stepId = "s1", status = "passed" });

        var lines = File.ReadAllLines(Path.Combine(store.DirectoryFor(run.RunId)!, "tasks", "t1", "events.jsonl"));
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That(lines[0], Does.Contain("stepStarted"));
            Assert.That(lines[1], Does.Contain("passed"));
        });
    }

    /// <summary>A multi-line value must not break the one-event-per-line contract.</summary>
    [Test]
    public void AppendEvent_KeepsAMultiLineValueOnASingleLine()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        store.AppendEvent(run.RunId, "t1", new { message = "line one\nline two" });

        var lines = File.ReadAllLines(Path.Combine(store.DirectoryFor(run.RunId)!, "tasks", "t1", "events.jsonl"));
        Assert.That(lines, Has.Length.EqualTo(1));
    }

    [Test]
    public void ListRuns_ReturnsNewestFirst()
    {
        var store = new RunStore(root);
        var first = store.CreateRun(RunTargetKind.Task, "t1", "First");
        Thread.Sleep(1100);   // the directory name is timestamped to the second
        var second = store.CreateRun(RunTargetKind.Task, "t2", "Second");

        var runs = store.ListRuns();

        Assert.That(runs.Select(r => r.RunId).Take(2), Is.EqualTo(new[] { second.RunId, first.RunId }));
    }

    [Test]
    public void ListRuns_HonoursTheLimit()
    {
        var store = new RunStore(root);
        for (var i = 0; i < 3; i++) store.CreateRun(RunTargetKind.Task, "t" + i, "Run " + i);

        Assert.That(store.ListRuns(limit: 2), Has.Count.EqualTo(2));
    }

    [Test]
    public void DatasetPath_LandsInsideTheRunAndKeepsItsExtension()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        var path = store.DatasetPath(run.RunId, "bought.csv");

        Assert.Multiple(() =>
        {
            Assert.That(Path.GetFileName(path), Is.EqualTo("bought.csv"));
            Assert.That(path, Does.StartWith(store.DirectoryFor(run.RunId)!));
        });
    }

    [Test]
    public void DatasetPath_SanitisesANameThatWouldEscapeTheRunDirectory()
    {
        var store = new RunStore(root);
        var run = store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        var path = store.DatasetPath(run.RunId, @"..\..\escape.csv");

        Assert.That(Path.GetFullPath(path), Does.StartWith(Path.GetFullPath(store.DirectoryFor(run.RunId)!)));
    }

    [Test]
    public void UnknownRunIdsAreReportedRatherThanGuessed()
    {
        var store = new RunStore(root);
        store.CreateRun(RunTargetKind.Task, "t1", "Scrape");

        Assert.That(store.GetRun("nope"), Is.Null);
        Assert.That(store.DirectoryFor("nope"), Is.Null);
        Assert.Throws<InvalidOperationException>(() => store.DatasetPath("nope", "x.csv"));
    }
}

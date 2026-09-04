using System.IO;
using Automata.Core.Automation.Execution;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The live lane view, and the one thing it must never do: show work that is not happening.
/// <para>
/// A file-per-process monitor is cheap and needs no server, but a killed process leaves its file
/// behind. Every case here is about the reader refusing to believe a file it can see.
/// </para>
/// </summary>
[TestFixture]
public class LiveLaneStoreTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static readonly DateTimeOffset Started = new(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);

    private LiveLaneStore Store(Func<int, DateTimeOffset, bool>? isAlive = null) =>
        new(Path.Combine(root, "live"), isAlive ?? ((_, _) => true));

    private static LiveLanes Process(int pid, params LaneStatus[] lanes) => new()
    {
        ProcessId = pid,
        ProcessStartedUtc = Started,
        ProcessName = "automata-runner",
        TargetName = "Nightly",
        MaxConcurrency = 4,
        UpdatedUtc = Started.AddMinutes(pid),
        Lanes = [.. lanes],
    };

    private static LaneStatus Busy(string laneId, string task, string? step = "Click Images") =>
        new(laneId, "default", true, "run-1", task, step, Started);

    private static LaneStatus Idle(string laneId) =>
        new(laneId, "default", false, null, null, null, null);

    [Test]
    public void PublishAndListRoundTripALiveProcess()
    {
        var store = Store();
        store.Publish(Process(101, Busy("lane-1", "Wolf Tshirts"), Idle("lane-2")));

        var listed = store.List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(listed.ProcessName, Is.EqualTo("automata-runner"));
            Assert.That(listed.MaxConcurrency, Is.EqualTo(4));
            Assert.That(listed.Lanes, Has.Count.EqualTo(2));
            Assert.That(listed.Lanes[0].CurrentStepLabel, Is.EqualTo("Click Images"));
        });
    }

    [Test]
    public void BusyLanesReportsOnlyTheLanesActuallyWorking()
    {
        var store = Store();
        store.Publish(Process(101, Busy("lane-1", "A"), Idle("lane-2"), Busy("lane-3", "B")));

        var busy = store.BusyLanes();

        Assert.That(busy.Select(x => x.Lane.LaneId), Is.EqualTo(new[] { "lane-1", "lane-3" }),
            "a warm-but-idle lane is not work in flight, and a strip that showed it would overstate what is running");
        Assert.That(busy[0].Process.ProcessId, Is.EqualTo(101), "each lane still says whose it is");
    }

    /// <summary>
    /// The failure this store exists to avoid: a monitor that keeps showing a killed run's lanes.
    /// </summary>
    [Test]
    public void AProcessThatIsGoneIsNeitherListedNorLeftOnDisk()
    {
        var store = Store(isAlive: (_, _) => false);
        store.Publish(Process(101, Busy("lane-1", "A")));

        Assert.That(store.List(), Is.Empty, "a dead process must not appear as running");
        Assert.That(Directory.GetFiles(Path.Combine(root, "live")), Is.Empty,
            "and its file should be tidied away, so the folder does not grow a phantom per crash");
    }

    /// <summary>
    /// Windows reuses process ids, so a pid alone is not an identity — the start time is what
    /// separates "still running" from "something else now has that number".
    /// </summary>
    [Test]
    public void LivenessIsCheckedAgainstTheStartTimeAsWellAsThePid()
    {
        var asked = new List<(int Pid, DateTimeOffset StartedUtc)>();
        var store = new LiveLaneStore(Path.Combine(root, "live"), (pid, started) =>
        {
            asked.Add((pid, started));
            return true;
        });
        store.Publish(Process(101, Busy("lane-1", "A")));

        store.List();

        Assert.That(asked.Single(), Is.EqualTo((101, Started)));
    }

    [Test]
    public void ClearRemovesOneProcessAndLeavesTheRest()
    {
        var store = Store();
        store.Publish(Process(101, Busy("lane-1", "A")));
        store.Publish(Process(202, Busy("lane-2", "B")));

        Assert.That(store.Clear(101), Is.True);
        Assert.That(store.Clear(101), Is.False, "clearing what is already gone is not an error");
        Assert.That(store.List().Select(p => p.ProcessId), Is.EqualTo(new[] { 202 }));
    }

    [Test]
    public void ListingIsEmptyBeforeAnythingRunsAndCreatesNoFolder()
    {
        var store = Store();

        Assert.That(store.List(), Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(root, "live")), Is.False,
            "a fresh install should have no Live folder at all");
    }

    [Test]
    public void ProcessesAreListedNewestFirst()
    {
        var store = Store();
        store.Publish(Process(101, Busy("lane-1", "A")));
        store.Publish(Process(202, Busy("lane-2", "B")));   // UpdatedUtc is later (Started + 202m)

        Assert.That(store.List().Select(p => p.ProcessId), Is.EqualTo(new[] { 202, 101 }));
    }

    // ---- the pool's side of it ------------------------------------------------------------------

    [Test]
    public async Task ThePoolReportsEveryChangeOfHands()
    {
        var snapshots = new List<LaneSnapshot>();
        await using var pool = new BrowserLanePool(
            new FakeBrowserSurfaceFactory(), maxConcurrency: 2, onChanged: snapshots.Add);

        await using (var lease = await pool.AcquireAsync("default", "run-1", "Wolf Tshirts"))
        {
            lease.Describe("Click Images");
        }

        Assert.That(snapshots, Has.Count.EqualTo(3), "acquire, describe, release");
        Assert.That(snapshots[0].MaxConcurrency, Is.EqualTo(2), "the ceiling travels with the snapshot");
        Assert.That(snapshots[0].Lanes.Single().Busy, Is.True);
        Assert.That(snapshots[1].Lanes.Single().CurrentStepLabel, Is.EqualTo("Click Images"),
            "Describe has to reach the monitor, or the strip would only ever say which task, not which step");
        Assert.That(snapshots[2].Lanes.Single().Busy, Is.False, "a returned lane stays open but idle");
    }

    /// <summary>A monitor must never be able to fail a run.</summary>
    [Test]
    public async Task AMonitorThatThrowsDoesNotBreakThePool()
    {
        await using var pool = new BrowserLanePool(
            new FakeBrowserSurfaceFactory(), maxConcurrency: 1,
            onChanged: _ => throw new InvalidOperationException("disk on fire"));

        await using var lease = await pool.AcquireAsync("default");

        Assert.That(lease.LaneId, Is.Not.Empty);
    }

    [Test]
    public async Task TheMonitorPublishesThePoolsLanesAndClearsUpAfterItself()
    {
        var store = Store();
        LaneMonitor monitor;
        using (monitor = new LaneMonitor(store, "automata-runner") { TargetName = "Nightly" })
        {
            await using var pool = new BrowserLanePool(
                new FakeBrowserSurfaceFactory(), maxConcurrency: 2, onChanged: monitor.OnChanged);
            await using var lease = await pool.AcquireAsync("default", "run-1", "Wolf Tshirts");
            lease.Describe("Click Images");

            var published = store.List().Single();
            Assert.Multiple(() =>
            {
                Assert.That(published.ProcessName, Is.EqualTo("automata-runner"));
                Assert.That(published.TargetName, Is.EqualTo("Nightly"));
                Assert.That(published.Lanes.Single().CurrentStepLabel, Is.EqualTo("Click Images"));
            });
        }

        Assert.That(store.List(), Is.Empty,
            "the file goes when the run does, so a watcher sees the work stop immediately");
    }
}

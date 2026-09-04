using Automata.Core.Automation.Execution;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class BrowserLanePoolTests
{
    [Test]
    public async Task ALaneIsReusedForTheSameProfileRatherThanReopened()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        await using (var first = await pool.AcquireAsync("work")) { }
        await using (var second = await pool.AcquireAsync("work")) { }

        Assert.That(factory.Requested, Has.Count.EqualTo(1),
            "a returned lane should stay warm so its login survives into the next lease");
    }

    [Test]
    public async Task DifferentProfilesGetDifferentBrowsers()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        await using (var work = await pool.AcquireAsync("work")) { }
        await using (var personal = await pool.AcquireAsync("personal")) { }

        Assert.That(factory.Requested, Is.EqualTo(new[] { "work", "personal" }));
    }

    [Test]
    public async Task ConcurrentLeasesOfOneProfileGetSeparateLanes()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 3);

        var a = await pool.AcquireAsync("work");
        var b = await pool.AcquireAsync("work");

        Assert.That(a.LaneId, Is.Not.EqualTo(b.LaneId));
        Assert.That(factory.Requested, Has.Count.EqualTo(2));

        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    /// <summary>
    /// The bound is the backpressure that stops a fifty-thousand-row dataset from trying to open
    /// fifty thousand browsers: the third caller waits until someone gives a lane back.
    /// </summary>
    [Test]
    public async Task TheThirdLeaseWaitsUntilASlotIsReturned()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        var first = await pool.AcquireAsync("work");
        var second = await pool.AcquireAsync("work");

        var third = pool.AcquireAsync("work");
        Assert.That(third.IsCompleted, Is.False, "the pool must block rather than exceed its ceiling");

        await first.DisposeAsync();
        var granted = await third.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(granted, Is.Not.Null);
        await granted.DisposeAsync();
        await second.DisposeAsync();
    }

    [Test]
    public async Task NeverOpensMoreBrowsersThanItsCeiling()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 3);

        var inFlight = 0;
        var peak = 0;
        var work = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await using var lease = await pool.AcquireAsync("work");
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref inFlight);
        }));

        await Task.WhenAll(work);

        Assert.That(peak, Is.LessThanOrEqualTo(3));
        Assert.That(factory.Requested, Has.Count.LessThanOrEqualTo(3));
    }

    [Test]
    public async Task SnapshotReportsWhichLaneIsRunningWhat()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        await using (var lease = await pool.AcquireAsync("work", runId: "r1", taskName: "Scrape prices"))
        {
            lease.Describe("row 2 of 9");
            var busy = pool.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(busy.Busy, Is.True);
                Assert.That(busy.ProfileKey, Is.EqualTo("work"));
                Assert.That(busy.RunId, Is.EqualTo("r1"));
                Assert.That(busy.TaskName, Is.EqualTo("Scrape prices"));
                Assert.That(busy.CurrentStepLabel, Is.EqualTo("row 2 of 9"));
                Assert.That(busy.StartedUtc, Is.Not.Null);
            });
        }

        var idle = pool.Snapshot().Single();
        Assert.That(idle.Busy, Is.False);
        Assert.That(idle.TaskName, Is.Null, "a returned lane should not still claim what it was doing");
    }

    [Test]
    public async Task DisposingTheCloseEveryLane()
    {
        var factory = new FakeBrowserSurfaceFactory();
        var pool = new BrowserLanePool(factory, maxConcurrency: 2);
        await using (var a = await pool.AcquireAsync("one")) { }
        await using (var b = await pool.AcquireAsync("two")) { }

        await pool.DisposeAsync();

        Assert.That(factory.Lanes.All(l => l.Disposed), Is.True);
    }

    [Test]
    public async Task DisposingTwiceIsHarmless()
    {
        var pool = new BrowserLanePool(new FakeBrowserSurfaceFactory(), maxConcurrency: 1);
        await pool.DisposeAsync();
        Assert.DoesNotThrowAsync(async () => await pool.DisposeAsync());
    }

    [Test]
    public async Task ReleasingALeaseTwiceDoesNotFreeTheSlotTwice()
    {
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 1);

        var lease = await pool.AcquireAsync("work");
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // If the double release had leaked a permit, this second lease plus a third would both be
        // granted and the ceiling would be silently broken.
        var again = await pool.AcquireAsync("work");
        var blocked = pool.AcquireAsync("work");
        Assert.That(blocked.IsCompleted, Is.False);

        await again.DisposeAsync();
        await (await blocked).DisposeAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) return;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}

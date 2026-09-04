using Automata.Core.Operator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Execution;

/// <summary>What one lane is doing right now — the answer to "which lane is running what".</summary>
public sealed record LaneStatus(
    string LaneId,
    string ProfileKey,
    bool Busy,
    string? RunId,
    string? TaskName,
    string? CurrentStepLabel,
    DateTimeOffset? StartedUtc);

/// <summary>
/// A lane borrowed from the pool. Returning it (disposing) frees the slot for the next waiter but
/// keeps the browser alive, so a login survives into the next lease.
/// </summary>
public sealed class LeasedLane : IAsyncDisposable
{
    private readonly BrowserLanePool pool;
    private readonly BrowserLanePool.LaneEntry entry;
    private bool released;

    internal LeasedLane(BrowserLanePool pool, BrowserLanePool.LaneEntry entry)
    {
        this.pool = pool;
        this.entry = entry;
    }

    public string LaneId => entry.Lane.LaneId;
    public IBrowserSurface Surface => entry.Lane.Surface;

    /// <summary>Updates what this lane reports it is doing, for the monitor.</summary>
    public void Describe(string? stepLabel) => pool.Touch(entry, stepLabel);

    public ValueTask DisposeAsync()
    {
        if (!released)
        {
            released = true;
            pool.Release(entry);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A bounded pool of browser lanes.
/// <para>
/// Lanes are pooled per profile rather than created per lease: a browser is expensive to start and
/// its cookies are the point of having named profiles at all, so returning a lane keeps it warm
/// for the next task that wants the same profile.
/// </para>
/// <para>
/// The bound is a hard ceiling on concurrent browsers, resolved from the settings chain — where a
/// deeper scope may only tighten it. Waiting for a slot is the backpressure that stops a
/// fifty-thousand-row dataset from trying to open fifty thousand browsers.
/// </para>
/// </summary>
public sealed class BrowserLanePool : IAsyncDisposable
{
    internal sealed class LaneEntry
    {
        public required IBrowserLane Lane { get; init; }
        public bool Busy;
        public string? RunId;
        public string? TaskName;
        public string? CurrentStepLabel;
        public DateTimeOffset? StartedUtc;
    }

    private readonly IBrowserSurfaceFactory factory;
    private readonly SemaphoreSlim slots;
    private readonly List<LaneEntry> lanes = [];
    private readonly object sync = new();
    private readonly ILogger<BrowserLanePool> log;
    private readonly Action<LaneSnapshot>? onChanged;
    private bool disposed;

    /// <param name="onChanged">
    /// Called whenever a lane changes hands or reports a new step. This is how the live lane strip
    /// is fed: the pool reports, and the caller decides whether that goes to a file another process
    /// can read, to a log, or nowhere. Leaving it null is the whole of "no monitoring", which is
    /// what every in-process run and every test wants.
    /// </param>
    public BrowserLanePool(
        IBrowserSurfaceFactory factory,
        int maxConcurrency,
        ILogger<BrowserLanePool>? log = null,
        Action<LaneSnapshot>? onChanged = null)
    {
        this.factory = factory;
        MaxConcurrency = Math.Max(1, maxConcurrency);
        slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        this.log = log ?? NullLogger<BrowserLanePool>.Instance;
        this.onChanged = onChanged;
    }

    public int MaxConcurrency { get; }

    /// <summary>Borrows a lane for the given profile, waiting when every slot is busy.</summary>
    public async Task<LeasedLane> AcquireAsync(
        string profileKey, string? runId = null, string? taskName = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await slots.WaitAsync(ct);
        try
        {
            LaneEntry? entry;
            lock (sync)
            {
                entry = lanes.FirstOrDefault(l => !l.Busy && l.Lane.ProfileKey == profileKey);
                if (entry != null) Claim(entry, runId, taskName);
            }

            if (entry == null)
            {
                // Created outside the lock: starting a browser is slow, and holding the lock would
                // serialise exactly the thing the pool exists to run in parallel.
                var lane = await factory.CreateLaneAsync(profileKey, ct);
                entry = new LaneEntry { Lane = lane };
                lock (sync)
                {
                    lanes.Add(entry);
                    Claim(entry, runId, taskName);
                }
                log.LogInformation("Opened browser lane {LaneId} for profile '{Profile}'", lane.LaneId, profileKey);
            }

            Notify();
            return new LeasedLane(this, entry);
        }
        catch
        {
            slots.Release();
            throw;
        }
    }

    private static void Claim(LaneEntry entry, string? runId, string? taskName)
    {
        entry.Busy = true;
        entry.RunId = runId;
        entry.TaskName = taskName;
        entry.CurrentStepLabel = null;
        entry.StartedUtc = DateTimeOffset.UtcNow;
    }

    internal void Release(LaneEntry entry)
    {
        lock (sync)
        {
            entry.Busy = false;
            entry.RunId = null;
            entry.TaskName = null;
            entry.CurrentStepLabel = null;
            entry.StartedUtc = null;
        }
        slots.Release();
        Notify();
    }

    internal void Touch(LaneEntry entry, string? stepLabel)
    {
        lock (sync) entry.CurrentStepLabel = stepLabel;
        Notify();
    }

    /// <summary>
    /// Reports the current state to whoever is watching. Called with the lock RELEASED: publishing
    /// may touch the disk, and holding the pool's lock across that would serialise the acquisitions
    /// the pool exists to overlap.
    /// </summary>
    private void Notify()
    {
        if (onChanged == null) return;
        try { onChanged(new LaneSnapshot(MaxConcurrency, Snapshot())); }
        catch (Exception ex)
        {
            // A monitor must never be able to fail a run.
            log.LogWarning(ex, "Lane monitor threw while publishing a snapshot");
        }
    }

    /// <summary>Every lane and what it is doing. This is what the monitor renders.</summary>
    public IReadOnlyList<LaneStatus> Snapshot()
    {
        lock (sync)
        {
            return lanes.Select(l => new LaneStatus(
                l.Lane.LaneId, l.Lane.ProfileKey, l.Busy, l.RunId, l.TaskName, l.CurrentStepLabel, l.StartedUtc))
                .ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        List<LaneEntry> toClose;
        lock (sync)
        {
            toClose = [.. lanes];
            lanes.Clear();
        }
        foreach (var entry in toClose)
        {
            try { await entry.Lane.DisposeAsync(); }
            catch (Exception ex) { log.LogWarning(ex, "Closing lane {LaneId} threw", entry.Lane.LaneId); }
        }
        slots.Dispose();
    }
}

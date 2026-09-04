using System.Diagnostics;
using System.IO;

namespace Automata.Core.Automation.Execution;

/// <summary>
/// Publishes a pool's lanes to <see cref="LiveLaneStore"/> so another process can watch them.
/// <para>
/// Wired to <see cref="BrowserLanePool"/>'s change callback rather than polled, so the strip a user
/// is watching updates when something actually happens instead of up to a poll-interval late. The
/// pool itself stays free of any knowledge of storage — it reports, this persists, exactly as the
/// engine reports run events and the caller writes them down.
/// </para>
/// <para>
/// Every change is written, with no throttling. A lane changes hands on acquire, release and each
/// step start — at most a few times a second even with the ceiling wide open — and a few hundred
/// bytes of JSON costs far less than the machinery to coalesce it would.
/// </para>
/// </summary>
public sealed class LaneMonitor : IDisposable
{
    private readonly LiveLaneStore store;
    private readonly int processId;
    private readonly DateTimeOffset processStartedUtc;
    private readonly string processName;
    private readonly object sync = new();
    private bool disposed;

    public LaneMonitor(LiveLaneStore store, string? processName = null)
    {
        this.store = store;
        using var self = Process.GetCurrentProcess();
        processId = self.Id;
        processStartedUtc = self.StartTime.ToUniversalTime();
        this.processName = processName ?? self.ProcessName;
    }

    /// <summary>What the process is working on overall, shown as the heading above its lanes.</summary>
    public string? TargetName { get; set; }

    public string? RunId { get; set; }

    /// <summary>Hand this to <see cref="BrowserLanePool"/>'s constructor.</summary>
    public Action<LaneSnapshot> OnChanged => Publish;

    public void Publish(LaneSnapshot snapshot)
    {
        if (disposed) return;
        // Parallel rows change lanes from several threads at once, and they would all be writing
        // the same file. Serialised here rather than in the store, which has no reason to know
        // that its single writer is concurrent.
        lock (sync)
        {
            if (disposed) return;
            try
            {
                store.Publish(new LiveLanes
                {
                    ProcessId = processId,
                    ProcessStartedUtc = processStartedUtc,
                    ProcessName = processName,
                    TargetName = TargetName,
                    RunId = RunId,
                    MaxConcurrency = snapshot.MaxConcurrency,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Lanes = [.. snapshot.Lanes],
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A monitor file is never worth failing a run over.
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            // Leaving the file behind would show phantom lanes until a reader noticed the process
            // was gone. The reader copes either way; this just makes the common case immediate.
            try { store.Clear(processId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

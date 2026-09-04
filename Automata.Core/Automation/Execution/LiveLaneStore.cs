using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Execution;

/// <summary>The pool's lanes and the ceiling they are bounded by, as one value.</summary>
public sealed record LaneSnapshot(int MaxConcurrency, IReadOnlyList<LaneStatus> Lanes);

/// <summary>
/// What one Automata process is running right now.
/// <para>
/// Identified by process id AND that process's start time. A pid on its own is not an identity —
/// Windows reuses them — and a monitor that showed a long-dead run's lanes because some unrelated
/// program inherited its pid would be worse than showing nothing.
/// </para>
/// </summary>
public sealed class LiveLanes
{
    public int SchemaVersion { get; set; } = 1;
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartedUtc { get; set; }

    /// <summary>Which program this is — <c>automata-runner</c>, the app — so the monitor can say
    /// whose work it is showing.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>What the process is working on overall, for a heading above the lanes.</summary>
    public string? TargetName { get; set; }
    public string? RunId { get; set; }

    public int MaxConcurrency { get; set; } = 1;
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<LaneStatus> Lanes { get; set; } = [];
}

/// <summary>
/// The live view of every Automata process's browser lanes: one small file per process in
/// <c>Documents\Automata\Live\&lt;pid&gt;.json</c>, rewritten whenever a lane changes hands.
/// <para>
/// It exists because the interesting lanes are in another process. The desktop app has one browser
/// pane and no pool; the pool lives in <c>automata-runner</c>, which is headless and usually
/// running off-screen at 3am. <see cref="BrowserLanePool.Snapshot"/> answers "which lane is running
/// what" perfectly well in-process — this is how that answer crosses the process boundary to the
/// window someone is actually looking at.
/// </para>
/// <para>
/// Files are cheap and need no server, but they outlive the process that wrote them if it is
/// killed. So a reader checks liveness rather than trusting the file, and a writer deletes its own
/// on the way out.
/// </para>
/// </summary>
public sealed class LiveLaneStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Live");

    public string RootPath { get; }

    private readonly Func<int, DateTimeOffset, bool> isAlive;

    /// <param name="isAlive">
    /// Whether a process is still running. Injected so the reader is testable: the real check asks
    /// the operating system, which a test cannot arrange.
    /// </param>
    public LiveLaneStore(string? rootPath = null, Func<int, DateTimeOffset, bool>? isAlive = null)
    {
        RootPath = rootPath ?? DefaultRoot;
        this.isAlive = isAlive ?? StillRunning;
    }

    public void Publish(LiveLanes lanes)
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(PathFor(lanes.ProcessId), JsonSerializer.Serialize(lanes, AutomataJson.Options));
    }

    public bool Clear(int processId)
    {
        var file = PathFor(processId);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>
    /// Every process still genuinely running, newest first. Files left behind by a process that
    /// was killed are removed as they are found, so the folder tidies itself rather than growing a
    /// phantom per crash.
    /// </summary>
    public IReadOnlyList<LiveLanes> List()
    {
        if (!Directory.Exists(RootPath)) return [];

        var live = new List<LiveLanes>();
        foreach (var file in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            var entry = Read(file);
            if (entry == null || !isAlive(entry.ProcessId, entry.ProcessStartedUtc))
            {
                try { File.Delete(file); } catch (IOException) { /* another reader got there first */ }
                catch (UnauthorizedAccessException) { /* not ours to tidy */ }
                continue;
            }
            live.Add(entry);
        }
        return live.OrderByDescending(e => e.UpdatedUtc).ToList();
    }

    /// <summary>Just the busy lanes across every live process — what a strip actually shows.</summary>
    public IReadOnlyList<(LiveLanes Process, LaneStatus Lane)> BusyLanes() =>
        List().SelectMany(p => p.Lanes.Where(l => l.Busy).Select(l => (p, l))).ToList();

    private string PathFor(int processId) =>
        Path.Combine(RootPath, StoreUtil.SafeFileName(processId.ToString()) + ".json");

    private static LiveLanes? Read(string file)
    {
        try { return JsonSerializer.Deserialize<LiveLanes>(File.ReadAllText(file), AutomataJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Very likely a half-written file caught mid-publish. Treat it as nothing to show and
            // let the next poll pick up the finished version.
            return null;
        }
    }

    private static bool StillRunning(int processId, DateTimeOffset startedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Math.Abs((process.StartTime.ToUniversalTime() - startedUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException) { return false; }          // no process with that id
        catch (InvalidOperationException) { return false; }  // it exited while we asked
        catch (Exception)
        {
            // Some other refusal — a permissions quirk, say. Hiding work that may well be running
            // is the worse mistake for a monitor, so assume it is alive and let the file's own
            // deletion on exit clean up.
            return true;
        }
    }
}

using System.Text.Json;
using Automata.Core.Automation.Data;
using Automata.Core.Automation.Model;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Storage;

/// <summary>What a run was launched against.</summary>
public enum RunTargetKind { Task, Collection }

/// <summary>The durable record of one run.</summary>
public sealed class RunManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public RunTargetKind Target { get; set; }
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";

    /// <summary>"manual" | "schedule" | "dependency" — how the run was started.</summary>
    public string Trigger { get; set; } = "manual";

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>Null while the run is still in flight.</summary>
    public bool? Success { get; set; }

    public string? Summary { get; set; }
}

/// <summary>
/// Durable run records: the manifest (<c>Runs</c>), a per-task event log (<c>RunEvents</c>) and
/// the values steps published (<c>RunOutputs</c>).
/// <para>
/// This is where <c>ExtractText</c>'s captured value has somewhere to go, and what lets the
/// sidebar show runs it did not start, including ones the headless runner finished while the
/// window was closed. Events are written as they happen, one row each, so a crashed run still
/// leaves everything it had already reported. (The plain-text run log is still a file — see
/// <see cref="Logging.RunLogWriter"/>.)
/// </para>
/// </summary>
public sealed class RunStore
{
    public RunStore(AutomataDatabase database) => Database = database;

    public AutomataDatabase Database { get; }

    public RunManifest CreateRun(RunTargetKind target, string targetId, string targetName, string trigger = "manual")
    {
        var manifest = new RunManifest
        {
            RunId = StoreUtil.NewId(),
            Target = target,
            TargetId = targetId,
            TargetName = targetName,
            Trigger = trigger,
            StartedUtc = StoreUtil.UtcNow(),
        };

        using var db = Database.CreateDbContext();
        db.Runs.Add(manifest);
        db.SaveChanges();
        return manifest;
    }

    /// <summary>Appends one event, serialized on one line. An unknown run is ignored.</summary>
    public void AppendEvent(string runId, string taskId, object evt)
    {
        using var db = Database.CreateDbContext();
        if (!db.Runs.Any(r => r.RunId == runId)) return;
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            TaskId = taskId,
            AtUtc = StoreUtil.UtcNow(),
            Json = JsonSerializer.Serialize(evt, AutomataJson.Compact).ReplaceLineEndings(" "),
        });
        db.SaveChanges();
    }

    /// <summary>One task's events in a run, oldest first, each as its JSON line.</summary>
    public IReadOnlyList<string> LoadEvents(string runId, string taskId)
    {
        using var db = Database.CreateDbContext();
        return db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && e.TaskId == taskId)
            .OrderBy(e => e.Id)
            .Select(e => e.Json)
            .ToList();
    }

    /// <summary>The task ids a run recorded anything for, in the order they first appeared.</summary>
    public IReadOnlyList<string> TaskIds(string runId)
    {
        using var db = Database.CreateDbContext();
        var fromEvents = db.RunEvents.AsNoTracking().Where(e => e.RunId == runId)
            .OrderBy(e => e.Id).Select(e => e.TaskId).ToList();
        var fromOutputs = db.RunOutputs.AsNoTracking().Where(o => o.RunId == runId)
            .OrderBy(o => o.Id).Select(o => o.TaskId).ToList();
        return fromEvents.Concat(fromOutputs).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Replaces the values one task's steps published, keyed step id → output name.</summary>
    public void SaveOutputs(string runId, string taskId, IReadOnlyDictionary<string, Dictionary<string, string>> outputs)
    {
        using var db = Database.CreateDbContext();
        if (!db.Runs.Any(r => r.RunId == runId)) return;
        using var tx = db.Database.BeginTransaction();
        db.RunOutputs.Where(o => o.RunId == runId && o.TaskId == taskId).ExecuteDelete();
        foreach (var (stepId, fields) in outputs)
            foreach (var (name, value) in fields)
                db.RunOutputs.Add(new RunOutputRecord
                {
                    RunId = runId, TaskId = taskId, StepId = stepId, Name = name, Value = value,
                });
        db.SaveChanges();
        tx.Commit();
    }

    public IReadOnlyDictionary<string, Dictionary<string, string>> LoadOutputs(string runId, string taskId)
    {
        using var db = Database.CreateDbContext();
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var o in db.RunOutputs.AsNoTracking()
                     .Where(o => o.RunId == runId && o.TaskId == taskId)
                     .OrderBy(o => o.Id))
        {
            if (!result.TryGetValue(o.StepId, out var fields))
                result[o.StepId] = fields = new Dictionary<string, string>(StringComparer.Ordinal);
            fields[o.Name] = o.Value;
        }
        return result;
    }

    public void CompleteRun(string runId, bool success, string summary)
    {
        using var db = Database.CreateDbContext();
        var manifest = db.Runs.FirstOrDefault(r => r.RunId == runId);
        if (manifest == null) return;
        manifest.Success = success;
        manifest.Summary = summary;
        manifest.EndedUtc = StoreUtil.UtcNow();
        db.SaveChanges();
    }

    /// <summary>Most recent runs first.</summary>
    public IReadOnlyList<RunManifest> ListRuns(int limit = 50)
    {
        using var db = Database.CreateDbContext();
        return db.Runs.AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .Take(limit)
            .ToList();
    }

    public RunManifest? GetRun(string runId)
    {
        if (string.IsNullOrEmpty(runId)) return null;
        using var db = Database.CreateDbContext();
        return db.Runs.AsNoTracking().FirstOrDefault(r => r.RunId == runId);
    }

    /// <summary>
    /// Writes a whole run as given — its own id and timestamps — for imports and the one-time move
    /// off the old files. A run whose id is already here is left alone: the same id is the same
    /// run, and history is never overwritten.
    /// </summary>
    /// <returns>False when the run already existed.</returns>
    public bool ImportRun(RunManifest manifest, IEnumerable<ImportedRunTask> tasks)
    {
        using var db = Database.CreateDbContext();
        if (db.Runs.Any(r => r.RunId == manifest.RunId)) return false;
        using var tx = db.Database.BeginTransaction();
        db.Runs.Add(manifest);
        foreach (var task in tasks)
        {
            foreach (var json in task.Events)
                db.RunEvents.Add(new RunEventRecord
                {
                    RunId = manifest.RunId,
                    TaskId = task.TaskId,
                    AtUtc = manifest.StartedUtc,
                    Json = json.ReplaceLineEndings(" "),
                });
            foreach (var (stepId, fields) in task.Outputs)
                foreach (var (name, value) in fields)
                    db.RunOutputs.Add(new RunOutputRecord
                    {
                        RunId = manifest.RunId, TaskId = task.TaskId, StepId = stepId, Name = name, Value = value,
                    });
        }
        db.SaveChanges();
        tx.Commit();
        return true;
    }
}

/// <summary>One task's share of an imported run: its event lines and published values.</summary>
public sealed record ImportedRunTask(
    string TaskId,
    IReadOnlyList<string> Events,
    IReadOnlyDictionary<string, Dictionary<string, string>> Outputs);

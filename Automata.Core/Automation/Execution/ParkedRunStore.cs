using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Execution;

/// <summary>
/// Runs waiting out a long pause, one file each in
/// <c>Documents\Automata\Parked\&lt;runId&gt;.json</c>.
/// <para>
/// A folder of small files rather than one list, because parked runs appear and disappear
/// independently and often at the same time — the runner may resume one while a browser run parks
/// another. Whole-file-per-entry means those two never contend over the same file, and a resumed
/// run leaves no residue at all: the file is deleted, not marked.
/// </para>
/// </summary>
public sealed class ParkedRunStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Parked");

    public string RootPath { get; }

    public ParkedRunStore(string? rootPath = null) => RootPath = rootPath ?? DefaultRoot;

    public void Save(ParkedRun parked)
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(PathFor(parked.RunId), JsonSerializer.Serialize(parked, AutomataJson.Options));
    }

    public ParkedRun? Get(string runId)
    {
        var file = PathFor(runId);
        if (!File.Exists(file)) return null;
        return Read(file);
    }

    /// <summary>Everything parked, soonest to resume first.</summary>
    public IReadOnlyList<ParkedRun> List()
    {
        if (!Directory.Exists(RootPath)) return [];
        return Directory.EnumerateFiles(RootPath, "*.json")
            .Select(Read)
            .Where(p => p != null)
            .OrderBy(p => p!.ResumeAtUtc)
            .ToList()!;
    }

    /// <summary>Parked runs whose wait is over.</summary>
    public IReadOnlyList<ParkedRun> Due(DateTimeOffset now) =>
        List().Where(p => p.ResumeAtUtc <= now).ToList();

    public bool Remove(string runId)
    {
        var file = PathFor(runId);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    private string PathFor(string runId) =>
        Path.Combine(RootPath, StoreUtil.SafeFileName(runId) + ".json");

    private static ParkedRun? Read(string file)
    {
        try { return JsonSerializer.Deserialize<ParkedRun>(File.ReadAllText(file), AutomataJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // One unreadable parked run must not stop every other one resuming. It stays on disk
            // rather than being deleted, so there is still something to look at.
            return null;
        }
    }
}

using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// The schedule, in one file at <c>Documents\Automata\Schedule\schedule.json</c>.
/// <para>
/// One file rather than the folder-per-item shape <c>CollectionStore</c> uses, because schedule
/// entries are few, small, and machine-maintained — they carry next-due bookkeeping nobody
/// hand-edits. Whole-file read and write, like the settings store.
/// </para>
/// </summary>
public sealed class ScheduleStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Automata", "Schedule", "schedule.json");

    public string FilePath { get; }

    public ScheduleStore(string? filePath = null) => FilePath = filePath ?? DefaultPath;

    public List<ScheduleEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ScheduleEntry>>(
                File.ReadAllText(FilePath), AutomataJson.Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt schedule must not stop the app starting; an empty schedule simply means
            // nothing fires until it is fixed.
            return [];
        }
    }

    public void Save(IEnumerable<ScheduleEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.ToList(), AutomataJson.Options));
    }

    public ScheduleEntry? Get(string id) =>
        Load().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>Adds or replaces an entry by id.</summary>
    public void Upsert(ScheduleEntry entry)
    {
        var entries = Load();
        var index = entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0) entries[index] = entry;
        else entries.Add(entry);
        Save(entries);
    }

    public bool Remove(string id)
    {
        var entries = Load();
        var removed = entries.RemoveAll(e => e.Id == id) > 0;
        if (removed) Save(entries);
        return removed;
    }
}

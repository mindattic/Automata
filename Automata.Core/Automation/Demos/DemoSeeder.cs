using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Demos;

/// <summary>What the generator found a demo task to be in, before it changed anything.</summary>
public enum DemoState
{
    /// <summary>Not in the store at all.</summary>
    Missing,

    /// <summary>Present, unedited, and identical to what the current build would generate.</summary>
    Current,

    /// <summary>Present and unedited, but an older build generated it.</summary>
    Stale,

    /// <summary>Present and changed by hand since it was generated.</summary>
    Edited,
}

/// <summary>One demo task and what the generator found.</summary>
public sealed record DemoStatus(string Key, string Name, DemoState State, string? TaskId);

/// <summary>What a seed or regenerate actually did.</summary>
public sealed record DemoSeedReport(
    string CollectionId,
    IReadOnlyList<string> PagesWritten,
    IReadOnlyList<DemoStatus> Before,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Refreshed,
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Kept);

/// <summary>
/// Writes the demo pages to disk and keeps the "Demos" collection in step with them.
/// <para>
/// <b>Demos is generated territory.</b> Everything in it that carries a demo marker belongs to the
/// generator, and regenerating puts every one of them back exactly as the build ships them —
/// contents, name and description alike. There is no per-example negotiation, because the answer
/// to "I want to keep my version" is not a checkbox: it is to move or duplicate that task into a
/// collection of your own, where nothing regenerates anything. Both of those gestures take the
/// demo marker off the copy, so the generator stops owning it the moment you claim it.
/// </para>
/// <para>
/// The two entry points differ on exactly one point, and deliberately: <see cref="SeedMissing"/>
/// runs on every launch and <b>never destroys an edit</b> — it adds what is absent and refreshes
/// what nobody has touched. <see cref="Regenerate"/> is something a person clicked, and it restores
/// everything. Silent actions may not lose work; an explicit one, asked for in as many words, may.
/// </para>
/// </summary>
public sealed class DemoSeeder(
    CollectionStore collections, string? demoRoot = null, DatasetStore? datasets = null)
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Demos");

    /// <summary>Overridable for tests and the harness via <c>AUTOMATA_DEMOS_ROOT</c>.</summary>
    public string RootPath { get; } = demoRoot ?? DefaultRoot;

    /// <summary>
    /// What every factory demo is currently in, without changing anything. This is what the
    /// Examples dialog reads so it can name the examples a regenerate is about to replace.
    /// </summary>
    public IReadOnlyList<DemoStatus> Survey()
    {
        var collection = collections.LoadCollections()
            .FirstOrDefault(c => c.Name == DemoTasks.CollectionName);
        var existing = collection == null
            ? []
            : collections.LoadTasks(collection.Id);

        var statuses = new List<DemoStatus>();
        foreach (var factory in DemoTasks.All(RootPath))
        {
            var match = existing.FirstOrDefault(t => t.Demo?.Key == factory.Key);
            if (match == null)
            {
                statuses.Add(new DemoStatus(factory.Key, factory.Name, DemoState.Missing, null));
                continue;
            }

            var factoryHash = HashOf(factory);
            var state = HashOf(match) != match.Demo!.FactoryHash
                ? DemoState.Edited
                : match.Demo.FactoryHash == factoryHash ? DemoState.Current : DemoState.Stale;
            statuses.Add(new DemoStatus(factory.Key, factory.Name, state, match.Id));
        }
        return statuses;
    }

    /// <summary>
    /// First-load seeding: write any page or demo that is not there, refresh any that no one has
    /// edited, and leave every edited one alone. Safe to call on every launch, which is the whole
    /// requirement — startup happens without anyone asking for it, so it may not lose work.
    /// </summary>
    public DemoSeedReport SeedMissing() => Apply(restoreEverything: false);

    /// <summary>
    /// Puts every example back to the version this build ships, whatever state it is in.
    /// <para>
    /// Wholesale on purpose. A per-example prompt sounds kinder and is not: it makes the one place
    /// a new user can look for a working reference into a place where any given example might be
    /// somebody's half-finished experiment, and it leaves the batch permanently unable to say
    /// "these are the examples". Keeping a modified example means moving or duplicating it out of
    /// Demos, which the dialog says in as many words before this runs.
    /// </para>
    /// </summary>
    public DemoSeedReport Regenerate() => Apply(restoreEverything: true);

    private DemoSeedReport Apply(bool restoreEverything)
    {
        var before = Survey();
        var pages = WritePages();
        WriteExampleData(overwrite: restoreEverything);

        var collection = collections.EnsureCollectionNamed(DemoTasks.CollectionName);
        if (string.IsNullOrEmpty(collection.Description))
        {
            collection.Description =
                "Generated examples, one per capability. Safe to run, safe to break — regenerating " +
                "from Settings puts every one of them back. Anything here you want to keep, move " +
                "or duplicate into a collection of your own.";
            collections.SaveCollection(collection);
        }

        var existing = collections.LoadTasks(collection.Id).ToList();
        List<string> added = [], refreshed = [], restored = [], kept = [];

        foreach (var factory in DemoTasks.All(RootPath))
        {
            var status = before.First(s => s.Key == factory.Key);
            var match = existing.FirstOrDefault(t => t.Id == status.TaskId);

            switch (status.State)
            {
                case DemoState.Missing:
                {
                    var fresh = Build(factory, collection.Id, UniqueName(factory.Name, existing));
                    collections.SaveTask(fresh);
                    existing.Add(fresh);
                    added.Add(fresh.Name);
                    break;
                }

                case DemoState.Current:
                    break;

                // Nobody has touched it, so there is nothing to lose and nothing worth mentioning.
                // Its name is already the factory name — a rename reads as an edit, since the hash
                // covers it.
                case DemoState.Stale:
                    refreshed.Add(Restore(factory, collection.Id, existing, match!).Name);
                    break;

                case DemoState.Edited:
                {
                    if (!restoreEverything)
                    {
                        kept.Add(match!.Name);
                        break;
                    }
                    // Back to the shipped version outright — including its name, because the name
                    // is part of what an example is and a renamed one is no easier to recognise
                    // than a rewritten one.
                    restored.Add(Restore(factory, collection.Id, existing, match!).Name);
                    break;
                }
            }
        }

        return new DemoSeedReport(collection.Id, pages, before, added, refreshed, restored, kept);
    }

    /// <summary>
    /// Puts one example back and keeps <paramref name="existing"/> current, so a later example
    /// restoring in the same pass sees the name this one has just vacated or taken rather than the
    /// one it had when the pass started.
    /// </summary>
    private TaskDefinition Restore(
        DemoTask factory, string collectionId, List<TaskDefinition> existing, TaskDefinition match)
    {
        var task = Build(factory, collectionId, UniqueName(factory.Name, existing, match));
        Reidentify(task, match.Id);
        existing[existing.IndexOf(match)] = task;
        return task;
    }

    /// <summary>
    /// Writes every demo page, always. Pages are generated output with no user content in them, so
    /// unlike a task there is nothing here to preserve — and a stale page under a fresh task is
    /// exactly the mismatch that makes a demo fail for reasons that teach nobody anything.
    /// </summary>
    public IReadOnlyList<string> WritePages()
    {
        var written = new List<string>();
        foreach (var page in DemoPages.All())
        {
            var path = Path.Combine(RootPath, page.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, page.Content, new UTF8Encoding(false));
            written.Add(page.RelativePath);
        }
        return written;
    }

    /// <summary>
    /// Writes the example ASSET — the ragged list the roster example iterates.
    /// <para>
    /// Unlike a page, this does NOT get rewritten on every launch. A dataset lives in the shared
    /// Datasets folder among the user's own files, and a generated page is output nobody could
    /// have edited on purpose while a data file plainly is. So it is written when it is absent, and
    /// replaced only by an explicit regenerate — the same split as everything else here: the silent
    /// path may not destroy work, the asked-for one may.
    /// </para>
    /// </summary>
    private void WriteExampleData(bool overwrite)
    {
        if (datasets == null) return;
        var path = datasets.PathFor(DemoPages.RosterDataset);
        if (!overwrite && File.Exists(path)) return;
        Directory.CreateDirectory(datasets.RootPath);
        File.WriteAllText(path, DemoPages.RosterJson, new UTF8Encoding(false));
    }

    /// <summary>
    /// The base name if nothing else has it, else the first free "name (n)".
    /// <para>
    /// Names matter more than they look: a task's file is named after it, so two tasks sharing a
    /// name in one collection would have one silently overwrite the other on disk.
    /// </para>
    /// <param name="self">
    /// The task being rewritten, which does not count as competition for its own name — without
    /// this, restoring an unrenamed example would push it to "name (2)" for colliding with itself.
    /// </param>
    /// </summary>
    private static string UniqueName(
        string baseName, IEnumerable<TaskDefinition> existing, TaskDefinition? self = null)
    {
        var taken = existing
            .Where(t => !ReferenceEquals(t, self) && t.Id != self?.Id)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static TaskDefinition Build(DemoTask factory, string collectionId, string name)
    {
        var task = new TaskDefinition
        {
            CollectionId = collectionId,
            Name = name,
            Description = factory.Description,
            StartUrl = factory.StartUrl,
            Steps = factory.Steps,
            Settings = factory.Settings,
            Inputs = factory.Inputs ?? [],
            Outputs = factory.Outputs ?? [],
            // Fixed, not generated — see DemoTasks. A runTask step names a task by id, so a demo
            // that called another demo could not be written at all if the callee's id were only
            // decided at seed time.
            Id = factory.TaskId,
        };

        task.Demo = new DemoOrigin { Key = factory.Key, FactoryHash = HashOf(task) };
        return task;
    }

    /// <summary>
    /// Saves a task whose id is not the one the store currently files it under.
    /// <para>
    /// The store keys a task file by the id INSIDE it, so writing a changed id straight over the
    /// old file looks like a different task landing on an occupied name — and the store would
    /// dutifully keep both, under "name" and "name (2)". Dropping the old identity first is what
    /// makes this one task with a new id rather than two tasks with one name. It is a no-op in the
    /// ordinary case, where the id has not moved at all.
    /// </para>
    /// </summary>
    private void Reidentify(TaskDefinition task, string previousId)
    {
        if (!string.Equals(previousId, task.Id, StringComparison.Ordinal))
            collections.DeleteTask(previousId);
        collections.SaveTask(task);
    }

    /// <summary>
    /// A content hash over everything a regenerate would put back: name, description, steps, start
    /// URL, settings, and the inputs and outputs the task declares.
    /// <para>
    /// The name is in there because restoring puts the name back too, and a survey that called a
    /// renamed example "up to date" would be promising not to touch something it is about to
    /// rename. Ids, timestamps and the marker itself are excluded for the plainer reason that they
    /// change on every save.
    /// </para>
    /// </summary>
    private static string HashOf(DemoTask factory) =>
        Hash(factory.Name, factory.Description, factory.StartUrl, factory.Steps, factory.Settings,
            factory.Inputs ?? [], factory.Outputs ?? []);

    private static string HashOf(TaskDefinition task) =>
        Hash(task.Name, task.Description, task.StartUrl, task.Steps, task.Settings, task.Inputs,
            task.Outputs);

    private static string Hash(
        string name, string description, string? startUrl, List<Step> steps,
        EngineSettingsOverride? settings, List<TaskInput> inputs, List<TaskOutput> outputs)
    {
        var payload = JsonSerializer.Serialize(
            new { name, description, startUrl, steps, settings, inputs, outputs }, HashOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
    }

    /// <summary>
    /// Compact, so the hash cannot move because a formatting default changed. Demo step ids are
    /// deterministic (see <see cref="DemoTasks"/>), so they are safe — in fact necessary — to
    /// include: an id is what a binding points at, and a demo whose bindings changed IS a
    /// different demo.
    /// </summary>
    private static readonly JsonSerializerOptions HashOptions =
        new(AutomataJson.Options) { WriteIndented = false };
}

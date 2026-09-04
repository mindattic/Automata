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

/// <summary>What to do about a demo the user has edited.</summary>
public enum DemoResolution
{
    /// <summary>Leave their version exactly as it is.</summary>
    Keep,

    /// <summary>Throw their changes away and restore the factory version.</summary>
    Revert,

    /// <summary>Keep their version untouched and add the factory version beside it under a new
    /// name, so the reference material is there without anything being lost.</summary>
    Clone,
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
    IReadOnlyList<string> Reverted,
    IReadOnlyList<string> Cloned,
    IReadOnlyList<string> Kept);

/// <summary>
/// Writes the demo pages to disk and keeps the "Demos" collection in step with them.
/// <para>
/// The policy, in one sentence: <b>seed what is missing, silently refresh what nobody has touched,
/// and never overwrite a hand edit without being told to.</b> An untouched demo carries no
/// information, so asking about it would be noise; an edited one may be the user's own work built
/// on top of a demo, and clobbering that is unrecoverable.
/// </para>
/// </summary>
public sealed class DemoSeeder(CollectionStore collections, string? demoRoot = null)
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Demos");

    /// <summary>Overridable for tests and the harness via <c>AUTOMATA_DEMOS_ROOT</c>.</summary>
    public string RootPath { get; } = demoRoot ?? DefaultRoot;

    /// <summary>
    /// What every factory demo is currently in, without changing anything. This is what the
    /// sidebar asks before it decides whether it needs to prompt at all.
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
    /// edited, and leave every edited one alone without asking. Safe to call on every launch.
    /// </summary>
    public DemoSeedReport SeedMissing() => Apply(resolutions: null);

    /// <summary>
    /// Regeneration: the same pass, but with an answer for each demo the user has edited. A key
    /// with no answer is left alone, so a partial set of choices can never destroy anything by
    /// omission.
    /// </summary>
    public DemoSeedReport Regenerate(IReadOnlyDictionary<string, DemoResolution> resolutions) =>
        Apply(resolutions);

    private DemoSeedReport Apply(IReadOnlyDictionary<string, DemoResolution>? resolutions)
    {
        var before = Survey();
        var pages = WritePages();

        var collection = collections.EnsureCollectionNamed(DemoTasks.CollectionName);
        if (string.IsNullOrEmpty(collection.Description))
        {
            collection.Description =
                "Generated examples, one per capability. Safe to run, safe to break — regenerate " +
                "them at any time from Settings.";
            collections.SaveCollection(collection);
        }

        var existing = collections.LoadTasks(collection.Id).ToList();
        List<string> added = [], refreshed = [], reverted = [], cloned = [], kept = [];

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

                // Nobody has touched it, so there is nothing to lose and nothing worth asking.
                // Its NAME is left as it is: renaming a demo is not editing it, and imposing the
                // factory name back could collide with another task that already has it.
                case DemoState.Stale:
                    Reidentify(Build(factory, collection.Id, match!.Name), match.Id);
                    refreshed.Add(match.Name);
                    break;

                case DemoState.Edited:
                {
                    var choice = resolutions != null && resolutions.TryGetValue(factory.Key, out var r)
                        ? r
                        : DemoResolution.Keep;
                    switch (choice)
                    {
                        case DemoResolution.Revert:
                            Reidentify(Build(factory, collection.Id, match!.Name), match.Id);
                            reverted.Add(match.Name);
                            break;

                        // Their work stays exactly where it is and keeps its name; what it loses is
                        // the demo marker AND the factory id, because it is their task now, not a
                        // copy of ours — and an id, like the marker, names the demo rather than
                        // what somebody built on top of it. The pristine version arrives beside it
                        // under a free name and takes both over, so the reference material is
                        // present, nothing was destroyed, and the next regenerate has nothing left
                        // to ask about.
                        case DemoResolution.Clone:
                            var theirs = match!;
                            var theirPreviousId = theirs.Id;
                            theirs.Demo = null;
                            if (theirs.Id == factory.TaskId) theirs.Id = StoreUtil.NewId();
                            Reidentify(theirs, theirPreviousId);

                            var clone = Build(
                                factory, collection.Id, UniqueName(factory.Name, existing));
                            collections.SaveTask(clone);
                            existing.Add(clone);
                            cloned.Add(clone.Name);
                            break;

                        default:
                            kept.Add(match!.Name);
                            break;
                    }
                    break;
                }
            }
        }

        return new DemoSeedReport(collection.Id, pages, before, added, refreshed, reverted, cloned, kept);
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
    /// The base name if nothing else has it, else the first free "name (n)".
    /// <para>
    /// Names matter more than they look: a task's file is named after it, so two tasks sharing a
    /// name in one collection would have one silently overwrite the other on disk.
    /// </para>
    /// </summary>
    private static string UniqueName(string baseName, IEnumerable<TaskDefinition> existing)
    {
        var taken = existing.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    /// A content hash over what a demo DOES: its steps, its start URL and its settings.
    /// <para>
    /// Name and description are excluded deliberately. Renaming a demo, or rewording its blurb, is
    /// not editing what it does — and a hash that moved for those would nag the user about a demo
    /// they only retitled, while also making it impossible to place a pristine copy beside their
    /// edited one under a different name. Ids, timestamps and the marker itself are excluded for
    /// the plainer reason that they change on every save.
    /// </para>
    /// </summary>
    private static string HashOf(DemoTask factory) =>
        Hash(factory.StartUrl, factory.Steps, factory.Settings);

    private static string HashOf(TaskDefinition task) =>
        Hash(task.StartUrl, task.Steps, task.Settings);

    private static string Hash(
        string? startUrl, List<Step> steps, EngineSettingsOverride? settings)
    {
        var payload = JsonSerializer.Serialize(new { startUrl, steps, settings }, HashOptions);
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

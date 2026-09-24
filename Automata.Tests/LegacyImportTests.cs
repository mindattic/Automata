using System.Text.Json;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The one-time move off <c>Documents\Automata</c>: the old JSON files come into an empty
/// database once, are never touched, and the old store's hand-edit rules still decide what they
/// mean.
/// </summary>
[TestFixture]
public class LegacyImportTests
{
    private string root = null!;
    private LegacyLocations legacy = null!;
    private TestDb db = null!;

    [SetUp]
    public void SetUp()
    {
        db = new TestDb();
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        legacy = LegacyLocations.Under(root);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private LegacyImportReport Import() => new LegacyWorkspaceImporter(db.Database, legacy).ImportOnce();

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, AutomataJson.Options));
    }

    private string CollectionDir(string folder) => Path.Combine(legacy.CollectionsRoot, folder);

    private (Collection Collection, TaskDefinition Task) WriteCollection(
        string folder, string taskName = "Task", string? taskFile = null, string? collectionName = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid().ToString("n"), Name = collectionName ?? folder,
            CreatedUtc = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ModifiedUtc = new DateTimeOffset(2025, 6, 2, 0, 0, 0, TimeSpan.Zero),
        };
        var task = new TaskDefinition
        {
            Id = Guid.NewGuid().ToString("n"), CollectionId = collection.Id, Name = taskName,
            CreatedUtc = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            Steps =
            [
                new Step
                {
                    Id = "s1", Action = StepAction.Group, Label = "outer",
                    Children = [new Step { Id = "s1a", Action = StepAction.Click, Label = "inner" }],
                },
            ],
        };
        collection.TaskOrder = [task.Id];
        WriteJson(Path.Combine(CollectionDir(folder), "collection.json"), collection);
        WriteJson(Path.Combine(CollectionDir(folder), (taskFile ?? taskName) + ".json"), task);
        return (collection, task);
    }

    /// <summary>Every kind of old file, all at once — the shape of a real user's Documents\Automata.</summary>
    private void WriteEverything()
    {
        WriteCollection("Email checks", "Check inbox");
        // How the old store laid out names Windows refuses: sanitised on disk, verbatim in the JSON.
        WriteCollection("Search_ Engines_", "Wolf: Tshirts", taskFile: "Wolf_ Tshirts", collectionName: "Search: Engines?");

        Directory.CreateDirectory(legacy.DatasetsRoot);
        File.WriteAllText(Path.Combine(legacy.DatasetsRoot, "skus.csv"), "sku,price\nA,1\nB,2\n");
        File.WriteAllText(Path.Combine(legacy.DatasetsRoot, "roster.json"), """[ { "Name": "Ada", "Address": { "City": "London" } } ]""");
        File.WriteAllText(Path.Combine(legacy.DatasetsRoot, "notes.txt"), "not a dataset");

        WriteJson(legacy.SchedulePath, new List<ScheduleEntry>
        {
            new() { Id = "e1", Name = "Nightly", TargetId = "c1" },
            new() { Id = "e2", Name = "Weekly", TargetId = "c1" },
        });

        var runDir = Path.Combine(legacy.RunsRoot, "20260101-090000-email-checks-abcdef12");
        WriteJson(Path.Combine(runDir, "manifest.json"), new RunManifest
        {
            RunId = "abcdef1234", Target = RunTargetKind.Collection, TargetId = "c1", TargetName = "Email checks",
            StartedUtc = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), Success = true, Summary = "ok",
        });
        Directory.CreateDirectory(Path.Combine(runDir, "tasks", "t1"));
        File.WriteAllText(Path.Combine(runDir, "tasks", "t1", "events.jsonl"), "{\"kind\":\"a\"}\n{\"kind\":\"b\"}\n");
        WriteJson(Path.Combine(runDir, "tasks", "t1", "outputs.json"),
            new Dictionary<string, Dictionary<string, string>> { ["s1"] = new() { ["price"] = "$1" } });

        WriteJson(Path.Combine(legacy.ParkedRoot, "abcdef1234.json"), new ParkedRun
        {
            RunId = "abcdef1234", TaskId = "t1", TaskName = "Check inbox",
            Checkpoint = new ParkCheckpoint(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), "a long wait",
                [0], "s1", "Wait", [], new Dictionary<string, string>(), 0, 0),
        });

        WriteJson(legacy.SettingsPath, new AutomataSettings { Provider = "openai", BorderRadius = 2 });
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText);

    [Test]
    public void EverythingComesIn_AndTheFilesAreLeftExactlyAsTheyWere()
    {
        WriteEverything();
        var before = Snapshot();

        var report = Import();

        Assert.Multiple(() =>
        {
            Assert.That(report.Ran, Is.True);
            Assert.That(report.Collections, Is.EqualTo(2));
            Assert.That(report.Tasks, Is.EqualTo(2));
            Assert.That(report.Datasets, Is.EqualTo(2), "only .csv and .json are datasets");
            Assert.That(report.ScheduleEntries, Is.EqualTo(2));
            Assert.That(report.Runs, Is.EqualTo(1));
            Assert.That(report.ParkedRuns, Is.EqualTo(1));
            Assert.That(report.Settings, Is.True);
            Assert.That(report.Warnings, Is.Empty);
            Assert.That(report.Describe(), Does.Contain("left in place"));

            var store = db.Collections();
            var email = store.LoadCollections().Single(c => c.Name == "Email checks");
            var task = store.LoadTasks(email.Id).Single();
            Assert.That(task.Name, Is.EqualTo("Check inbox"));
            Assert.That(task.Steps[0].Children.Single().Label, Is.EqualTo("inner"), "the nested tree survives");
            Assert.That(task.CreatedUtc, Is.EqualTo(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(email.ModifiedUtc, Is.EqualTo(new DateTimeOffset(2025, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                "history is kept, not restamped");
            Assert.That(email.TaskOrder, Is.EqualTo(new[] { task.Id }));

            Assert.That(db.Datasets().Read("skus.csv").Select(r => r["price"]), Is.EqualTo(new[] { "1", "2" }));
            Assert.That(db.Datasets().Read("roster.json").Single()["Address.City"], Is.EqualTo("London"));
            Assert.That(db.Schedule().Load().Select(e => e.Id), Is.EqualTo(new[] { "e1", "e2" }));
            Assert.That(db.Runs().GetRun("abcdef1234")!.Summary, Is.EqualTo("ok"));
            Assert.That(db.Runs().LoadEvents("abcdef1234", "t1"), Has.Count.EqualTo(2));
            Assert.That(db.Runs().LoadOutputs("abcdef1234", "t1")["s1"]["price"], Is.EqualTo("$1"));
            Assert.That(db.Parked().Get("abcdef1234")!.Checkpoint.Reason, Is.EqualTo("a long wait"));
            Assert.That(db.Settings().Load().Provider, Is.EqualTo("openai"));
        });

        Assert.That(Snapshot(), Is.EqualTo(before), "the old files are the backup — not one byte may change");
    }

    /// <summary>A name that was merely sanitised on its way to disk is not a rename: the JSON keeps
    /// the original, illegal characters and all.</summary>
    [Test]
    public void ASanitisedFileNameIsNotMistakenForARename()
    {
        WriteEverything();
        Import();

        var store = db.Collections();
        var search = store.LoadCollections().Single(c => c.Name == "Search: Engines?");
        Assert.That(store.LoadTasks(search.Id).Single().Name, Is.EqualTo("Wolf: Tshirts"));
    }

    [Test]
    public void ItRunsOnlyOnce()
    {
        WriteEverything();
        Import();

        var second = Import();
        var recorded = new LegacyWorkspaceImporter(db.Database, legacy).Recorded()!;

        Assert.Multiple(() =>
        {
            Assert.That(second.Ran, Is.False);
            Assert.That(second.Outcome, Does.StartWith("Already considered"));
            Assert.That(db.Collections().LoadCollections(), Has.Count.EqualTo(2), "nothing was imported twice");
            Assert.That(recorded.Ran, Is.True);
            Assert.That(recorded.Tasks, Is.EqualTo(2));
        });
    }

    /// <summary>A user who deletes an imported collection must not see it come back on the next
    /// launch just because the old files are still there.</summary>
    [Test]
    public void DeletingAnImportedCollectionDoesNotBringItBackNextLaunch()
    {
        WriteEverything();
        Import();
        var store = db.Collections();
        foreach (var c in store.LoadCollections()) store.DeleteCollection(c.Id);

        Import();

        Assert.That(store.LoadCollections(), Is.Empty);
    }

    [Test]
    public void ADatabaseThatAlreadyHasDataIsNeverImportedInto()
    {
        db.Collections().CreateCollection("Made in the new version");
        WriteEverything();

        var report = Import();

        Assert.Multiple(() =>
        {
            Assert.That(report.Ran, Is.False);
            Assert.That(report.Outcome, Does.Contain("already had data"));
            Assert.That(db.Collections().LoadCollections().Single().Name, Is.EqualTo("Made in the new version"));
            Assert.That(Import().Outcome, Does.StartWith("Already considered"), "and the decision is recorded");
        });
    }

    /// <summary>A truly empty install: nothing to import, it says so, and the first-run examples
    /// still arrive afterwards.</summary>
    [Test]
    public void AFreshInstallImportsNothing_AndTheExamplesStillSeed()
    {
        var report = Import();
        Assert.That(report.Ran, Is.False);
        Assert.That(report.Outcome, Does.Contain("nothing to import"));

        var seeder = new DemoSeeder(db.Collections(), Path.Combine(root, "demos"), db.Datasets());
        var seeded = seeder.SeedMissing();

        Assert.Multiple(() =>
        {
            Assert.That(seeded.Added, Is.Not.Empty);
            Assert.That(db.Collections().LoadCollections().Select(c => c.Name), Does.Contain(DemoTasks.CollectionName));
            Assert.That(db.Datasets().Exists(DemoPages.RosterDataset), Is.True);
        });
    }

    // ---- the old store's hand-edit rules, applied on the way in ---------------------------------

    [Test]
    public void AFileRenamedInExplorerNamesItsTask()
    {
        var (_, task) = WriteCollection("C", "Original");
        File.Move(Path.Combine(CollectionDir("C"), "Original.json"), Path.Combine(CollectionDir("C"), "Hand renamed.json"));

        Import();

        Assert.That(db.Collections().GetTask(task.Id)!.Name, Is.EqualTo("Hand renamed"));
    }

    [Test]
    public void AFolderRenamedInExplorerNamesItsCollection()
    {
        var (collection, _) = WriteCollection("Original");
        Directory.Move(CollectionDir("Original"), CollectionDir("Hand renamed"));

        Import();

        Assert.That(db.Collections().GetCollection(collection.Id)!.Name, Is.EqualTo("Hand renamed"));
    }

    [Test]
    public void ACopyPastedTaskFileGetsItsOwnId()
    {
        WriteCollection("C", "T");
        File.Copy(Path.Combine(CollectionDir("C"), "T.json"), Path.Combine(CollectionDir("C"), "T - Copy.json"));

        Import();

        var store = db.Collections();
        var tasks = store.LoadTasks(store.LoadCollections().Single().Id);
        Assert.That(tasks.Select(t => t.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(tasks.Select(t => t.Name), Is.EquivalentTo(new[] { "T", "T - Copy" }));
    }

    [Test]
    public void ACopyPastedCollectionFolderGetsItsOwnIds()
    {
        WriteCollection("C", "T");
        CopyDirectory(CollectionDir("C"), CollectionDir("C - Copy"));

        var report = Import();

        var store = db.Collections();
        Assert.Multiple(() =>
        {
            Assert.That(report.Collections, Is.EqualTo(2));
            Assert.That(store.LoadCollections().Select(c => c.Id).Distinct().Count(), Is.EqualTo(2));
            Assert.That(store.LoadAllTasks().Select(t => t.Id).Distinct().Count(), Is.EqualTo(2));
        });
    }

    [Test]
    public void AFolderOfTasksWithoutACollectionJsonIsRecoveredUnderItsName()
    {
        var stray = new TaskDefinition { Id = "t1", CollectionId = "whatever", Name = "Orphaned" };
        WriteJson(Path.Combine(CollectionDir("Hand-made folder"), "Orphaned.json"), stray);

        var report = Import();

        var store = db.Collections();
        var collection = store.LoadCollections().Single();
        Assert.Multiple(() =>
        {
            Assert.That(collection.Name, Is.EqualTo("Hand-made folder"));
            Assert.That(store.LoadTasks(collection.Id).Single().CollectionId, Is.EqualTo(collection.Id));
            Assert.That(report.Warnings, Has.Some.Contains("no collection.json"));
        });
    }

    [Test]
    public void UnreadableFilesAreSkippedWithAWarning_NotAFailedImport()
    {
        WriteCollection("C", "Good");
        File.WriteAllText(Path.Combine(CollectionDir("C"), "Broken.json"), "{ not json at all");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.SettingsPath)!);
        File.WriteAllText(legacy.SettingsPath, "{ not json");

        var report = Import();

        Assert.Multiple(() =>
        {
            Assert.That(report.Ran, Is.True);
            Assert.That(report.Tasks, Is.EqualTo(1));
            Assert.That(report.Settings, Is.False);
            Assert.That(report.Warnings, Has.Some.Contains("Broken.json"));
            Assert.That(db.Settings().Load().Provider, Is.EqualTo("claude"), "defaults, as a corrupt file always meant");
        });
    }

    /// <summary>A settings.json written before SidebarWidth existed still imports, with the default
    /// width rather than a zero that would collapse the sidebar.</summary>
    [Test]
    public void AnOldSettingsFileWithoutNewerFieldsGetsTheirDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.SettingsPath)!);
        File.WriteAllText(legacy.SettingsPath, """{ "provider": "claude", "borderRadius": 3 }""");

        Import();

        var settings = db.Settings().Load();
        Assert.That(settings.BorderRadius, Is.EqualTo(3));
        Assert.That(settings.SidebarWidth, Is.EqualTo(420));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }
}

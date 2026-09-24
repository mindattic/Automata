using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class SchemaMigrationTests
{
    private string root = null!;
    private TestDb db = null!;

    [SetUp]
    public void SetUp()
    {
        db = new TestDb();
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string WriteV1Store()
    {
        var dir = Path.Combine(root, "Collections", "Legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "collection.json"), """
            { "schemaVersion": 1, "id": "c1", "name": "Legacy", "description": "",
              "createdUtc": "2026-01-01T00:00:00+00:00", "modifiedUtc": "2026-01-01T00:00:00+00:00",
              "taskOrder": ["t1"] }
            """);
        File.WriteAllText(Path.Combine(dir, "Old Task.json"), """
            { "schemaVersion": 1, "id": "t1", "collectionId": "c1", "name": "Old Task",
              "description": "", "steps": [ { "id": "s1", "action": "click", "label": "Click it",
              "target": { "tag": "button", "cssSelector": "#go", "classList": [] }, "children": [] } ],
              "createdUtc": "2026-01-01T00:00:00+00:00", "modifiedUtc": "2026-01-01T00:00:00+00:00" }
            """);
        return dir;
    }

    /// <summary>
    /// The whole point of the v1 -> v2 change being additive: a store written by an old version
    /// comes into the database without a migration pass, keeping every field it had.
    /// </summary>
    [Test]
    public void AHandWrittenV1Store_ImportsUnchanged()
    {
        WriteV1Store();

        new LegacyWorkspaceImporter(db.Database, LegacyLocations.Under(root)).ImportOnce();
        var store = db.Collections();
        var collections = store.LoadCollections();
        var tasks = store.LoadTasks("c1");

        Assert.Multiple(() =>
        {
            Assert.That(collections, Has.Count.EqualTo(1));
            Assert.That(collections[0].Name, Is.EqualTo("Legacy"));
            Assert.That(collections[0].Settings, Is.Null, "a v1 collection has nothing to inherit from");
            Assert.That(tasks, Has.Count.EqualTo(1));
            Assert.That(tasks[0].Steps, Has.Count.EqualTo(1));
            Assert.That(tasks[0].Steps[0].Action, Is.EqualTo(StepAction.Click));
            Assert.That(tasks[0].Steps[0].Target!.CssSelector, Is.EqualTo("#go"));
            Assert.That(tasks[0].Settings, Is.Null);
            Assert.That(tasks[0].CreatedUtc, Is.EqualTo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                "history survives the move");
        });
    }

    /// <summary>
    /// Importing must not rewrite the old files: they are the user's backup, and have to be left
    /// exactly as they were found.
    /// </summary>
    [Test]
    public void ImportingAV1Store_DoesNotRewriteItOnDisk()
    {
        var dir = WriteV1Store();
        var before = Directory.GetFiles(dir).ToDictionary(f => f, File.ReadAllText);

        new LegacyWorkspaceImporter(db.Database, LegacyLocations.Under(root)).ImportOnce();

        foreach (var (file, text) in before)
            Assert.That(File.ReadAllText(file), Is.EqualTo(text), file);
        Assert.That(Directory.GetFiles(dir), Has.Length.EqualTo(before.Count), "nothing was added beside them");
    }

    [Test]
    public void SavingStampsTheCurrentSchemaVersion()
    {
        var store = db.Collections();
        var collection = store.CreateCollection("Fresh");
        var task = new TaskDefinition { CollectionId = collection.Id, Name = "T", SchemaVersion = 1 };
        store.SaveTask(task);

        var exported = JsonSerializer.Serialize(store.GetTask(task.Id), AutomataJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(store.GetCollection(collection.Id)!.SchemaVersion,
                Is.EqualTo(SchemaMigration.CurrentCollectionVersion));
            Assert.That(store.GetTask(task.Id)!.SchemaVersion, Is.EqualTo(SchemaMigration.CurrentTaskVersion),
                "the write path stamps the version whose shape the row was actually written in");
            Assert.That(JsonDocument.Parse(exported).RootElement.GetProperty("schemaVersion").GetInt32(),
                Is.EqualTo(SchemaMigration.CurrentTaskVersion));
        });
    }

    /// <summary>
    /// An override that overrides nothing must never be persisted — otherwise a task nobody has
    /// configured looks configured, both to a reader of an export and to the first-run floor check.
    /// </summary>
    [Test]
    public void AnEmptyOverrideIsPrunedInsteadOfPersisted()
    {
        var store = db.Collections();
        var collection = store.CreateCollection("Fresh");
        collection.Settings = new EngineSettingsOverride();
        store.SaveCollection(collection);

        var task = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = "T",
            Settings = new EngineSettingsOverride(),
            Steps = [new Step { Id = "s1", Action = StepAction.Click, Settings = new EngineSettingsOverride() }],
        };
        store.SaveTask(task);

        var back = store.GetTask(task.Id)!;
        var manifest = JsonSerializer.Serialize(store.GetCollection(collection.Id), AutomataJson.Options);
        var taskJson = JsonSerializer.Serialize(back, AutomataJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(store.GetCollection(collection.Id)!.Settings, Is.Null);
            Assert.That(back.Settings, Is.Null);
            Assert.That(back.Steps[0].Settings, Is.Null);
            Assert.That(manifest, Does.Not.Contain("settings"));
            Assert.That(taskJson, Does.Not.Contain("settings"));
        });
    }

    [Test]
    public void ARealOverrideSurvivesARoundTrip()
    {
        var store = db.Collections();
        var collection = store.CreateCollection("Scoped");
        collection.Settings = new EngineSettingsOverride { DefaultStepTimeoutMs = 3000 };
        store.SaveCollection(collection);

        store.SaveTask(new TaskDefinition
        {
            Id = "t1",
            CollectionId = collection.Id,
            Name = "T",
            Settings = new EngineSettingsOverride { Retry = new RetryPolicy { MaxAttempts = 3, DelayMs = 50 } },
            Steps = [new Step { Id = "s1", Action = StepAction.Click, Settings = new EngineSettingsOverride { SelfHeal = false } }],
        });

        var reloaded = db.Collections();
        var back = reloaded.LoadCollections().Single(c => c.Name == "Scoped");
        var task = reloaded.LoadTasks(back.Id).Single();

        Assert.Multiple(() =>
        {
            Assert.That(back.Settings!.DefaultStepTimeoutMs, Is.EqualTo(3000));
            Assert.That(back.Settings.SelfHeal, Is.Null, "what was not overridden still inherits");
            Assert.That(task.Settings!.Retry!.MaxAttempts, Is.EqualTo(3));
            Assert.That(task.Settings.Retry.DelayMs, Is.EqualTo(50));
            Assert.That(task.Steps[0].Settings!.SelfHeal, Is.False);
        });
    }

    /// <summary>IsEmpty is [JsonIgnore]d; if that ever slips, every entity gains a junk property.</summary>
    [Test]
    public void IsEmptyIsNotSerialized()
    {
        var json = JsonSerializer.Serialize(
            new EngineSettingsOverride { SelfHeal = false }, AutomataJson.Options);

        Assert.That(json, Does.Not.Contain("isEmpty"));
    }
}

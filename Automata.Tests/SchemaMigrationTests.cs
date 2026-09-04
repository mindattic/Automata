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

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The whole point of the v1 -> v2 change being additive: a store written by the previous
    /// version loads without a migration pass, keeping every field it had.
    /// </summary>
    [Test]
    public void AHandWrittenV1Store_LoadsUnchanged()
    {
        var dir = Path.Combine(root, "Legacy");
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

        var store = new CollectionStore(root);
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
            Assert.That(tasks[0].Settings, Is.Null);
        });
    }

    /// <summary>
    /// Loading must not rewrite an untouched store. Opening the new version against an existing
    /// Documents\Automata should leave every file exactly as it was found.
    /// </summary>
    [Test]
    public void LoadingAV1Store_DoesNotRewriteItOnDisk()
    {
        var dir = Path.Combine(root, "Legacy");
        Directory.CreateDirectory(dir);
        var manifest = Path.Combine(dir, "collection.json");
        File.WriteAllText(manifest, """
            { "schemaVersion": 1, "id": "c1", "name": "Legacy", "description": "",
              "createdUtc": "2026-01-01T00:00:00+00:00", "modifiedUtc": "2026-01-01T00:00:00+00:00",
              "taskOrder": [] }
            """);
        var before = File.ReadAllText(manifest);

        new CollectionStore(root).LoadCollections();

        Assert.That(File.ReadAllText(manifest), Is.EqualTo(before));
    }

    [Test]
    public void SavingStampsTheCurrentSchemaVersion()
    {
        var store = new CollectionStore(root);
        var collection = store.CreateCollection("Fresh");
        var task = new TaskDefinition { CollectionId = collection.Id, Name = "T", SchemaVersion = 1 };
        store.SaveTask(task);

        var manifest = File.ReadAllText(Path.Combine(root, "Fresh", "collection.json"));
        var taskJson = File.ReadAllText(Path.Combine(root, "Fresh", "T.json"));

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(manifest).RootElement.GetProperty("schemaVersion").GetInt32(),
                Is.EqualTo(SchemaMigration.CurrentCollectionVersion));
            Assert.That(JsonDocument.Parse(taskJson).RootElement.GetProperty("schemaVersion").GetInt32(),
                Is.EqualTo(SchemaMigration.CurrentTaskVersion),
                "the write path stamps the version whose shape the file was actually written in");
        });
    }

    /// <summary>
    /// An override that overrides nothing must never reach disk — otherwise a task nobody has
    /// configured looks configured, both to a reader and to the first-run floor check.
    /// </summary>
    [Test]
    public void AnEmptyOverrideIsPrunedInsteadOfPersisted()
    {
        var store = new CollectionStore(root);
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

        var manifest = File.ReadAllText(Path.Combine(root, "Fresh", "collection.json"));
        var taskJson = File.ReadAllText(Path.Combine(root, "Fresh", "T.json"));

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Not.Contain("settings"));
            Assert.That(taskJson, Does.Not.Contain("settings"));
        });
    }

    [Test]
    public void ARealOverrideSurvivesARoundTrip()
    {
        var store = new CollectionStore(root);
        var collection = store.CreateCollection("Scoped");
        collection.Settings = new EngineSettingsOverride { MaxConcurrency = 3 };
        store.SaveCollection(collection);

        store.SaveTask(new TaskDefinition
        {
            Id = "t1",
            CollectionId = collection.Id,
            Name = "T",
            Settings = new EngineSettingsOverride { Retry = new RetryPolicy { MaxAttempts = 3, DelayMs = 50 } },
            Steps = [new Step { Id = "s1", Action = StepAction.Click, Settings = new EngineSettingsOverride { SelfHeal = false } }],
        });

        var reloaded = new CollectionStore(root);
        var back = reloaded.LoadCollections().Single(c => c.Name == "Scoped");
        var task = reloaded.LoadTasks(back.Id).Single();

        Assert.Multiple(() =>
        {
            Assert.That(back.Settings!.MaxConcurrency, Is.EqualTo(3));
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

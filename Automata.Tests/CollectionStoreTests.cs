using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class CollectionStoreTests
{
    private string root = null!;
    private CollectionStore store = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        store = new CollectionStore(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private TaskDefinition NewTask(string collectionId, string name = "My task") => new()
    {
        CollectionId = collectionId,
        Name = name,
        Steps = [new Step { Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" }],
    };

    [Test]
    public void CreateCollection_ThenLoad_RoundTrips()
    {
        var created = store.CreateCollection("Email checks");

        var loaded = store.LoadCollections();

        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].Id, Is.EqualTo(created.Id));
        Assert.That(loaded[0].Name, Is.EqualTo("Email checks"));
        Assert.That(File.Exists(Path.Combine(root, created.Id, "collection.json")), Is.True);
    }

    [Test]
    public void CreateCollection_WithTakenName_GetsNumericSuffix()
    {
        store.CreateCollection("Work");
        var second = store.CreateCollection("Work");

        Assert.That(second.Name, Is.EqualTo("Work (2)"));
    }

    [Test]
    public void SaveTask_ThenLoadTasks_RoundTrips_AndAppendsToTaskOrder()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id);

        store.SaveTask(task);

        var tasks = store.LoadTasks(collection.Id);
        Assert.That(tasks, Has.Count.EqualTo(1));
        Assert.That(tasks[0].Id, Is.EqualTo(task.Id));
        Assert.That(tasks[0].Steps, Has.Count.EqualTo(1));
        Assert.That(store.GetCollection(collection.Id)!.TaskOrder, Is.EqualTo(new[] { task.Id }));
    }

    [Test]
    public void SaveTask_WithEmptyCollectionId_AutoAssignsDefaultCollection()
    {
        var task = NewTask(collectionId: "");

        store.SaveTask(task);

        Assert.That(task.CollectionId, Is.Not.Empty);
        var parent = store.GetCollection(task.CollectionId)!;
        Assert.That(parent.Name, Is.EqualTo(CollectionStore.DefaultCollectionName));
        Assert.That(store.GetTask(task.Id), Is.Not.Null);
    }

    [Test]
    public void DeleteTask_RemovesFileAndTaskOrderEntry()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id);
        store.SaveTask(task);

        store.DeleteTask(task.Id);

        Assert.That(store.LoadTasks(collection.Id), Is.Empty);
        Assert.That(store.GetCollection(collection.Id)!.TaskOrder, Is.Empty);
    }

    [Test]
    public void MoveTask_RelocatesFile_AndRewritesCollectionId()
    {
        var from = store.CreateCollection("From");
        var to = store.CreateCollection("To");
        var task = NewTask(from.Id);
        store.SaveTask(task);

        var moved = store.MoveTask(task.Id, to.Id);

        Assert.That(moved.CollectionId, Is.EqualTo(to.Id));
        Assert.That(File.Exists(Path.Combine(root, to.Id, "tasks", task.Id + ".json")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, from.Id, "tasks", task.Id + ".json")), Is.False);
        Assert.That(store.GetCollection(from.Id)!.TaskOrder, Is.Empty);
        Assert.That(store.GetCollection(to.Id)!.TaskOrder, Is.EqualTo(new[] { task.Id }));
    }

    [Test]
    public void DuplicateTask_RegeneratesTaskAndStepIds_AndSuffixesName()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Login flow");
        task.Steps[0].Children.Add(new Step { Action = StepAction.Click, Label = "child" });
        store.SaveTask(task);

        var copy = store.DuplicateTask(task.Id);

        Assert.That(copy.Id, Is.Not.EqualTo(task.Id));
        Assert.That(copy.Name, Is.EqualTo("Login flow (2)"));
        Assert.That(copy.Steps[0].Id, Is.Not.EqualTo(task.Steps[0].Id));
        Assert.That(copy.Steps[0].Children[0].Id, Is.Not.EqualTo(task.Steps[0].Children[0].Id));
        Assert.That(store.LoadTasks(collection.Id), Has.Count.EqualTo(2));
    }

    [Test]
    public void DuplicateCollection_CopiesAllTasks_WithFreshIds()
    {
        var source = store.CreateCollection("Source");
        var task = NewTask(source.Id, "T1");
        store.SaveTask(task);

        var copy = store.DuplicateCollection(source.Id);

        Assert.That(copy.Id, Is.Not.EqualTo(source.Id));
        Assert.That(copy.Name, Is.EqualTo("Source (2)"));
        var copiedTasks = store.LoadTasks(copy.Id);
        Assert.That(copiedTasks, Has.Count.EqualTo(1));
        Assert.That(copiedTasks[0].Id, Is.Not.EqualTo(task.Id));
        Assert.That(copiedTasks[0].Name, Is.EqualTo("T1"));
        Assert.That(store.LoadTasks(source.Id), Has.Count.EqualTo(1)); // source untouched
    }

    [Test]
    public void LoadTasks_HealsCollectionIdDrift_FolderWins()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id);
        store.SaveTask(task);

        // Simulate a hand-copied file pointing at some other collection.
        var file = Path.Combine(root, collection.Id, "tasks", task.Id + ".json");
        var drifted = JsonSerializer.Deserialize<TaskDefinition>(File.ReadAllText(file), AutomataJson.Options)!;
        drifted.CollectionId = "somewhere-else";
        File.WriteAllText(file, JsonSerializer.Serialize(drifted, AutomataJson.Options));

        var tasks = store.LoadTasks(collection.Id);

        Assert.That(tasks[0].CollectionId, Is.EqualTo(collection.Id));
        var reread = JsonSerializer.Deserialize<TaskDefinition>(File.ReadAllText(file), AutomataJson.Options)!;
        Assert.That(reread.CollectionId, Is.EqualTo(collection.Id)); // healed on disk too
    }

    [Test]
    public void LoadCollections_RecoversTaskFolderMissingCollectionJson()
    {
        var task = new TaskDefinition { Id = "t1", CollectionId = "orphan", Name = "Orphaned" };
        var tasksDir = Path.Combine(root, "orphan", "tasks");
        Directory.CreateDirectory(tasksDir);
        File.WriteAllText(Path.Combine(tasksDir, "t1.json"),
            JsonSerializer.Serialize(task, AutomataJson.Options));

        var collections = store.LoadCollections();

        Assert.That(collections, Has.Count.EqualTo(1));
        Assert.That(collections[0].Id, Is.EqualTo("orphan"));
        Assert.That(collections[0].Name, Is.EqualTo("Recovered"));
        Assert.That(store.LoadTasks("orphan"), Has.Count.EqualTo(1));
    }

    [Test]
    public void LoadTasks_OrdersByTaskOrder_UnlistedSortLastByName()
    {
        var collection = store.CreateCollection("C");
        var a = NewTask(collection.Id, "Alpha");
        var b = NewTask(collection.Id, "Beta");
        store.SaveTask(a);
        store.SaveTask(b);

        // Reverse the order, then drop a hand-copied task that's in no TaskOrder at all.
        var reordered = store.GetCollection(collection.Id)!;
        reordered.TaskOrder = [b.Id, a.Id];
        store.SaveCollection(reordered);
        var stray = new TaskDefinition { Id = "stray1", CollectionId = collection.Id, Name = "AAA stray" };
        File.WriteAllText(Path.Combine(root, collection.Id, "tasks", "stray1.json"),
            JsonSerializer.Serialize(stray, AutomataJson.Options));

        var tasks = store.LoadTasks(collection.Id);

        Assert.That(tasks.Select(t => t.Name), Is.EqualTo(new[] { "Beta", "Alpha", "AAA stray" }));
    }
}

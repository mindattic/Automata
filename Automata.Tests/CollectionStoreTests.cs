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

    private static TaskDefinition ReadTaskFile(string path) =>
        JsonSerializer.Deserialize<TaskDefinition>(File.ReadAllText(path), AutomataJson.Options)!;

    // ---- name-based layout ---------------------------------------------------------------------

    [Test]
    public void CreateCollection_UsesHumanReadableFolderName()
    {
        var created = store.CreateCollection("Email checks");

        Assert.That(File.Exists(Path.Combine(root, "Email checks", "collection.json")), Is.True);
        Assert.That(store.LoadCollections().Single().Id, Is.EqualTo(created.Id));
    }

    [Test]
    public void SaveTask_UsesHumanReadableFileName_AndAppendsToTaskOrder()
    {
        var collection = store.CreateCollection("Google Searches");
        var task = NewTask(collection.Id, "Wolf Tshirts");

        store.SaveTask(task);

        Assert.That(File.Exists(Path.Combine(root, "Google Searches", "Wolf Tshirts.json")), Is.True);
        var tasks = store.LoadTasks(collection.Id);
        Assert.That(tasks.Single().Id, Is.EqualTo(task.Id));
        Assert.That(store.GetCollection(collection.Id)!.TaskOrder, Is.EqualTo(new[] { task.Id }));
    }

    [Test]
    public void RenamingATask_MovesItsFile()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Old name");
        store.SaveTask(task);

        task.Name = "New name";
        store.SaveTask(task);

        Assert.That(File.Exists(Path.Combine(root, "C", "New name.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "C", "Old name.json")), Is.False);
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("New name"));
    }

    [Test]
    public void RenamingACollection_MovesItsFolder_WithTasksIntact()
    {
        var collection = store.CreateCollection("Before");
        store.SaveTask(NewTask(collection.Id, "T1"));

        collection.Name = "After";
        store.SaveCollection(collection);

        Assert.That(Directory.Exists(Path.Combine(root, "After")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(root, "Before")), Is.False);
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("T1"));
    }

    // ---- illegal characters: lossless round-trip -------------------------------------------------

    [Test]
    public void IllegalCharacters_SanitizedOnDisk_ButOriginalNameRoundTrips()
    {
        var collection = store.CreateCollection("Search: Engines?");
        var task = NewTask(collection.Id, "Wolf: Tshirts * <cheap>");
        store.SaveTask(task);

        // Disk names are sanitized projections…
        Assert.That(Directory.Exists(Path.Combine(root, "Search_ Engines_")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "Search_ Engines_", "Wolf_ Tshirts _ _cheap_.json")), Is.True);

        // …but the originals — illegal characters intact — parse back from the JSON.
        var reloadedCollection = store.LoadCollections().Single();
        Assert.That(reloadedCollection.Name, Is.EqualTo("Search: Engines?"));
        var reloadedTask = store.LoadTasks(collection.Id).Single();
        Assert.That(reloadedTask.Name, Is.EqualTo("Wolf: Tshirts * <cheap>"));

        // A second load must not "heal" the sanitization difference into a rename.
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("Wolf: Tshirts * <cheap>"));
    }

    [Test]
    public void ReservedWindowsNames_GetPrefixed_ButOriginalNameRoundTrips()
    {
        var collection = store.CreateCollection("CON");
        var task = NewTask(collection.Id, "NUL");
        store.SaveTask(task);

        Assert.That(Directory.Exists(Path.Combine(root, "_CON")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "_CON", "_NUL.json")), Is.True);
        Assert.That(store.LoadCollections().Single().Name, Is.EqualTo("CON"));
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("NUL"));
    }

    [Test]
    public void TaskNamedCollection_CannotClobberTheManifest()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "collection");
        store.SaveTask(task);

        Assert.That(File.Exists(Path.Combine(root, "C", "_collection.json")), Is.True);
        Assert.That(store.GetCollection(collection.Id), Is.Not.Null); // manifest survived
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("collection"));
    }

    [Test]
    public void VeryLongNames_AreTruncatedOnDisk_ButRoundTrip()
    {
        var longName = new string('a', 150) + " end";
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, longName);
        store.SaveTask(task);

        var file = Directory.EnumerateFiles(Path.Combine(root, "C"))
            .Single(f => Path.GetFileName(f) != "collection.json");
        Assert.That(Path.GetFileNameWithoutExtension(file).Length, Is.LessThanOrEqualTo(100));
        Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo(longName));
    }

    // ---- explorer-edit healing -------------------------------------------------------------------

    [Test]
    public void FileRenamedInExplorer_IsAdoptedAsTheTaskName()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Original");
        store.SaveTask(task);

        File.Move(Path.Combine(root, "C", "Original.json"), Path.Combine(root, "C", "Hand renamed.json"));

        var reloaded = store.LoadTasks(collection.Id).Single();
        Assert.That(reloaded.Name, Is.EqualTo("Hand renamed"));
        Assert.That(ReadTaskFile(Path.Combine(root, "C", "Hand renamed.json")).Name,
            Is.EqualTo("Hand renamed")); // healed on disk too
    }

    [Test]
    public void FolderRenamedInExplorer_IsAdoptedAsTheCollectionName()
    {
        var collection = store.CreateCollection("Original");

        Directory.Move(Path.Combine(root, "Original"), Path.Combine(root, "Hand renamed"));

        Assert.That(store.LoadCollections().Single().Name, Is.EqualTo("Hand renamed"));
        Assert.That(store.GetCollection(collection.Id)!.Name, Is.EqualTo("Hand renamed")); // id stable
    }

    [Test]
    public void TaskFileCopyPastedInExplorer_GetsAFreshId()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "T");
        store.SaveTask(task);

        File.Copy(Path.Combine(root, "C", "T.json"), Path.Combine(root, "C", "T - Copy.json"));

        var tasks = store.LoadTasks(collection.Id);
        Assert.That(tasks, Has.Count.EqualTo(2));
        Assert.That(tasks.Select(t => t.Id).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void TaskFolderMissingManifest_IsRecoveredWithTheFolderName()
    {
        var stray = new TaskDefinition { Id = "t1", CollectionId = "whatever", Name = "Orphaned" };
        Directory.CreateDirectory(Path.Combine(root, "Hand-made folder"));
        File.WriteAllText(Path.Combine(root, "Hand-made folder", "Orphaned.json"),
            JsonSerializer.Serialize(stray, AutomataJson.Options));

        var collections = store.LoadCollections();

        Assert.That(collections, Has.Count.EqualTo(1));
        Assert.That(collections[0].Name, Is.EqualTo("Hand-made folder"));
        var tasks = store.LoadTasks(collections[0].Id);
        Assert.That(tasks.Single().CollectionId, Is.EqualTo(collections[0].Id)); // re-parented
    }

    // ---- CRUD / move / duplicate ------------------------------------------------------------------

    [Test]
    public void CreateCollection_WithTakenName_GetsNumericSuffix()
    {
        store.CreateCollection("Work");
        var second = store.CreateCollection("Work");

        Assert.That(second.Name, Is.EqualTo("Work (2)"));
        Assert.That(Directory.Exists(Path.Combine(root, "Work (2)")), Is.True);
    }

    [Test]
    public void SaveTask_WithEmptyCollectionId_AutoAssignsDefaultCollection()
    {
        var task = NewTask(collectionId: "");

        store.SaveTask(task);

        Assert.That(task.CollectionId, Is.Not.Empty);
        Assert.That(store.GetCollection(task.CollectionId)!.Name, Is.EqualTo(CollectionStore.DefaultCollectionName));
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
    public void MoveTask_RelocatesFileBetweenCollectionFolders()
    {
        var from = store.CreateCollection("From");
        var to = store.CreateCollection("To");
        var task = NewTask(from.Id, "Mover");
        store.SaveTask(task);

        var moved = store.MoveTask(task.Id, to.Id);

        Assert.That(moved.CollectionId, Is.EqualTo(to.Id));
        Assert.That(File.Exists(Path.Combine(root, "To", "Mover.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "From", "Mover.json")), Is.False);
        Assert.That(store.GetCollection(from.Id)!.TaskOrder, Is.Empty);
        Assert.That(store.GetCollection(to.Id)!.TaskOrder, Is.EqualTo(new[] { task.Id }));
    }

    [Test]
    public void DuplicateTask_RegeneratesIds_AndSuffixesName()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Login flow");
        task.Steps[0].Children.Add(new Step { Action = StepAction.Click, Label = "child" });
        store.SaveTask(task);

        var copy = store.DuplicateTask(task.Id);

        Assert.That(copy.Id, Is.Not.EqualTo(task.Id));
        Assert.That(copy.Name, Is.EqualTo("Login flow (2)"));
        Assert.That(File.Exists(Path.Combine(root, "C", "Login flow (2).json")), Is.True);
        Assert.That(copy.Steps[0].Id, Is.Not.EqualTo(task.Steps[0].Id));
        Assert.That(copy.Steps[0].Children[0].Id, Is.Not.EqualTo(task.Steps[0].Children[0].Id));
    }

    [Test]
    public void DuplicateCollection_CopiesAllTasks_WithFreshIds()
    {
        var source = store.CreateCollection("Source");
        var task = NewTask(source.Id, "T1");
        store.SaveTask(task);

        var copy = store.DuplicateCollection(source.Id);

        Assert.That(copy.Name, Is.EqualTo("Source (2)"));
        var copiedTasks = store.LoadTasks(copy.Id);
        Assert.That(copiedTasks.Single().Id, Is.Not.EqualTo(task.Id));
        Assert.That(store.LoadTasks(source.Id), Has.Count.EqualTo(1)); // source untouched
    }

    [Test]
    public void RenameOntoASiblingTasksName_SuffixesInsteadOfClobbering()
    {
        var collection = store.CreateCollection("C");
        var a = NewTask(collection.Id, "Alpha");
        var b = NewTask(collection.Id, "Beta");
        store.SaveTask(a);
        store.SaveTask(b);

        b.Name = "Alpha";
        store.SaveTask(b);

        Assert.That(b.Name, Is.EqualTo("Alpha (2)"));
        var tasks = store.LoadTasks(collection.Id);
        Assert.That(tasks, Has.Count.EqualTo(2));
        Assert.That(tasks.Single(t => t.Id == a.Id).Name, Is.EqualTo("Alpha")); // untouched
    }

    [Test]
    public void LoadTasks_OrdersByTaskOrder_UnlistedSortLastByName()
    {
        var collection = store.CreateCollection("C");
        var a = NewTask(collection.Id, "Alpha");
        var b = NewTask(collection.Id, "Beta");
        store.SaveTask(a);
        store.SaveTask(b);

        var reordered = store.GetCollection(collection.Id)!;
        reordered.TaskOrder = [b.Id, a.Id];
        store.SaveCollection(reordered);
        var stray = new TaskDefinition { Id = "stray1", CollectionId = collection.Id, Name = "AAA stray" };
        File.WriteAllText(Path.Combine(root, "C", "AAA stray.json"),
            JsonSerializer.Serialize(stray, AutomataJson.Options));

        var tasks = store.LoadTasks(collection.Id);

        Assert.That(tasks.Select(t => t.Name), Is.EqualTo(new[] { "Beta", "Alpha", "AAA stray" }));
    }

}

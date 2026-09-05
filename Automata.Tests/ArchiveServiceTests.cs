using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ArchiveServiceTests
{
    private string workDir = null!;
    private CollectionStore sourceStore = null!;
    private CollectionStore targetStore = null!;

    [SetUp]
    public void SetUp()
    {
        workDir = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workDir);
        sourceStore = new CollectionStore(Path.Combine(workDir, "source"));
        targetStore = new CollectionStore(Path.Combine(workDir, "target"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
    }

    private string ZipPath(string name) => Path.Combine(workDir, name);

    private (Collection collection, TaskDefinition task) SeedSource()
    {
        var collection = sourceStore.CreateCollection("Email checks");
        var task = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = "Check inbox",
            Steps =
            [
                new Step
                {
                    Action = StepAction.Navigate, Label = "Go", Url = "https://mail.example",
                    Children = [new Step { Action = StepAction.Click, Label = "Open first" }],
                },
            ],
        };
        sourceStore.SaveTask(task);
        return (collection, task);
    }

    [Test]
    public void ExportCollection_ThenImportIntoEmptyStore_RoundTripsContent()
    {
        var (collection, task) = SeedSource();
        var zip = new ArchiveService(sourceStore).ExportCollection(collection.Id, ZipPath("c.automata.zip"));

        var result = new ArchiveService(targetStore).Import(zip);

        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Collections.Single().Name, Is.EqualTo("Email checks"));
        var imported = targetStore.LoadTasks(result.Collections.Single().Id);
        Assert.That(imported, Has.Count.EqualTo(1));
        Assert.That(imported[0].Name, Is.EqualTo("Check inbox"));
        Assert.That(imported[0].Id, Is.EqualTo(task.Id)); // no collision → id preserved
        Assert.That(imported[0].Steps[0].Children, Has.Count.EqualTo(1));
        Assert.That(imported[0].Steps[0].Id, Is.Not.EqualTo(task.Steps[0].Id)); // step ids always fresh
    }

    [Test]
    public void ImportCollection_OverExistingIds_RegeneratesAndRemapsTaskOrder()
    {
        var (collection, task) = SeedSource();
        var zip = new ArchiveService(sourceStore).ExportCollection(collection.Id, ZipPath("c.automata.zip"));

        // Import into the SAME store: every id collides.
        var result = new ArchiveService(sourceStore).Import(zip);

        var imported = result.Collections.Single();
        Assert.That(imported.Id, Is.Not.EqualTo(collection.Id));
        Assert.That(imported.Name, Is.EqualTo("Email checks (2)"));
        var importedTasks = sourceStore.LoadTasks(imported.Id);
        Assert.That(importedTasks, Has.Count.EqualTo(1));
        Assert.That(importedTasks[0].Id, Is.Not.EqualTo(task.Id));
        Assert.That(imported.TaskOrder, Is.EqualTo(new[] { importedTasks[0].Id })); // remapped
        Assert.That(sourceStore.LoadTasks(collection.Id), Has.Count.EqualTo(1));    // original intact
        Assert.That(result.Warnings, Is.Not.Empty);
    }

    [Test]
    public void ExportTask_ThenImport_LandsInImportedCollection()
    {
        var (_, task) = SeedSource();
        var zip = new ArchiveService(sourceStore).ExportTask(task.Id, ZipPath("t.automata.zip"));

        var result = new ArchiveService(targetStore).Import(zip);

        var parent = result.Collections.Single();
        Assert.That(parent.Name, Is.EqualTo(ArchiveService.ImportedCollectionName));
        var imported = targetStore.LoadTasks(parent.Id).Single();
        Assert.That(imported.Name, Is.EqualTo("Check inbox"));
        Assert.That(imported.CollectionId, Is.EqualTo(parent.Id));
    }

    [Test]
    public void ImportTask_TwiceIntoSameStore_SuffixesNameAndRegeneratesId()
    {
        var (_, task) = SeedSource();
        var zip = new ArchiveService(sourceStore).ExportTask(task.Id, ZipPath("t.automata.zip"));
        var archive = new ArchiveService(targetStore);

        archive.Import(zip);
        var second = archive.Import(zip);

        Assert.That(second.Tasks.Single().Name, Is.EqualTo("Check inbox (2)"));
        Assert.That(second.Tasks.Single().Id, Is.Not.EqualTo(task.Id));
        Assert.That(second.Warnings, Is.Not.Empty);
    }

    [Test]
    public void Import_RejectsZipWithBadManifest()
    {
        var zipPath = ZipPath("bad.zip");
        using (var stream = File.Create(zipPath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("""{ "format": "something-else", "type": "collection" }""");

        Assert.That(() => new ArchiveService(targetStore).Import(zipPath),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("format"));
    }

    [Test]
    public void Import_RejectsZipWithNoManifest()
    {
        var zipPath = ZipPath("empty.zip");
        using (var stream = File.Create(zipPath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        Assert.That(() => new ArchiveService(targetStore).Import(zipPath),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Import_NewerSchemaVersion_WarnsButImports()
    {
        var (collection, _) = SeedSource();
        var zipPath = new ArchiveService(sourceStore).ExportCollection(collection.Id, ZipPath("c.automata.zip"));

        // Rewrite the manifest to claim a future schema.
        var rewritten = ZipPath("future.zip");
        using (var src = ZipFile.OpenRead(zipPath))
        using (var stream = File.Create(rewritten))
        using (var dst = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var entry in src.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                if (entry.FullName == "manifest.json")
                {
                    var manifest = JsonSerializer.Deserialize<ExportManifest>(content, AutomataJson.Options)!;
                    manifest.SchemaVersion = 99;
                    content = JsonSerializer.Serialize(manifest, AutomataJson.Options);
                }
                using var writer = new StreamWriter(dst.CreateEntry(entry.FullName).Open());
                writer.Write(content);
            }
        }

        var result = new ArchiveService(targetStore).Import(rewritten);

        Assert.That(result.Warnings, Has.Some.Contains("newer"));
        Assert.That(result.Collections.Single().Name, Is.EqualTo("Email checks"));
    }

    [Test]
    public void ExportedZip_ContainsManifestCollectionAndTasks()
    {
        var (collection, task) = SeedSource();
        var zipPath = new ArchiveService(sourceStore).ExportCollection(collection.Id, ZipPath("c.automata.zip"));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.That(zip.GetEntry("manifest.json"), Is.Not.Null);
        Assert.That(zip.GetEntry("collection.json"), Is.Not.Null);
        Assert.That(zip.GetEntry($"tasks/{task.Id}.json"), Is.Not.Null);
    }

    // ---- an import has to be wired to itself -------------------------------------------------
    //
    // The same defect a duplicate had, reached the other way: an import re-keys every step id and
    // regenerates a colliding task id, and then left every reference to either pointing at what it
    // named before. An export/import round-trip therefore came back looking identical and running
    // differently — which is the worst shape a bug can have in a format people move work in.

    [Test]
    public void ImportingATask_RewiresItsStepReferencesToTheImportedSteps()
    {
        var collection = sourceStore.CreateCollection("Wired");
        var read = new Step
        {
            Action = StepAction.ExtractText, Label = "read", Outputs = [new OutputField { Name = "total" }],
        };
        var type = new Step
        {
            Action = StepAction.TypeText, Label = "type",
            Bindings = new Dictionary<string, BindingRef>
            {
                ["Value"] = new() { Kind = BindingKind.StepOutput, SourceStepId = read.Id, OutputField = "total" },
            },
        };
        var task = new TaskDefinition
        {
            CollectionId = collection.Id, Name = "Round trip", Steps = [read, type],
            Outputs = [new TaskOutput { Name = "total", SourceStepId = read.Id, SourceOutputField = "total" }],
        };
        sourceStore.SaveTask(task);
        var zip = new ArchiveService(sourceStore).ExportTask(task.Id, ZipPath("t.automata.zip"));

        var imported = new ArchiveService(targetStore).Import(zip).Tasks.Single();

        Assert.Multiple(() =>
        {
            Assert.That(imported.Steps[1].Bindings!["Value"].SourceStepId, Is.EqualTo(imported.Steps[0].Id));
            Assert.That(imported.Outputs.Single().SourceStepId, Is.EqualTo(imported.Steps[0].Id));
        });
    }

    /// <summary>
    /// Importing a collection back into the workspace it came from: every task id collides, so
    /// every one is regenerated — and the runTask step and the pipeline wiring between them have
    /// to follow, or the import quietly calls the tasks it was imported alongside.
    /// </summary>
    [Test]
    public void ImportingACollectionOverItself_RewiresTheTasksToTheImportedCopies()
    {
        var collection = sourceStore.CreateCollection("Pipeline");
        var first = new TaskDefinition
        {
            CollectionId = collection.Id, Name = "Find it",
            Steps = [new Step { Action = StepAction.ExtractText, Label = "read", Outputs = [new OutputField { Name = "id" }] }],
        };
        first.Outputs = [new TaskOutput { Name = "ticket", SourceStepId = first.Steps[0].Id, SourceOutputField = "id" }];
        sourceStore.SaveTask(first);

        var second = new TaskDefinition
        {
            CollectionId = collection.Id, Name = "Use it",
            Inputs = [new TaskInput
            {
                Name = "ticket",
                From = new TaskOutputRef { TaskId = first.Id, TaskName = "Find it", OutputName = "ticket" },
            }],
            Steps = [new Step { Action = StepAction.RunTask, Label = "call", RunTaskId = first.Id }],
        };
        sourceStore.SaveTask(second);

        var zip = new ArchiveService(sourceStore).ExportCollection(collection.Id, ZipPath("p.automata.zip"));
        // Imported into the SAME store, so both task ids are already taken.
        var result = new ArchiveService(sourceStore).Import(zip);

        var importedFirst = result.Tasks.Single(t => t.Name == "Find it");
        var importedSecond = result.Tasks.Single(t => t.Name == "Use it");
        Assert.Multiple(() =>
        {
            Assert.That(importedFirst.Id, Is.Not.EqualTo(first.Id), "the collision was regenerated");
            Assert.That(importedSecond.Steps[0].RunTaskId, Is.EqualTo(importedFirst.Id),
                "and the call follows the copy rather than reaching back into the original");
            Assert.That(importedSecond.Inputs.Single().From!.TaskId, Is.EqualTo(importedFirst.Id));
        });
    }

    /// <summary>
    /// A single-task zip carries wirings to tasks that did not come with it. Those are dangling,
    /// and dangling is worth PRESERVING rather than blanking: the editor can say which task a
    /// wiring named and offer to re-point it, which it cannot do about a reference that was
    /// cleared on the way in.
    /// </summary>
    [Test]
    public void ImportingATask_LeavesAWiringItHasNoAnswerForAlone()
    {
        var collection = sourceStore.CreateCollection("Half a pipeline");
        var task = new TaskDefinition
        {
            CollectionId = collection.Id, Name = "Downstream",
            Inputs = [new TaskInput
            {
                Name = "ticket",
                From = new TaskOutputRef { TaskId = "upstream-left-behind", TaskName = "Find it", OutputName = "ticket" },
            }],
            Steps = [new Step { Action = StepAction.RunTask, Label = "call", RunTaskId = "callee-left-behind" }],
        };
        sourceStore.SaveTask(task);
        var zip = new ArchiveService(sourceStore).ExportTask(task.Id, ZipPath("half.automata.zip"));

        var imported = new ArchiveService(targetStore).Import(zip).Tasks.Single();

        Assert.Multiple(() =>
        {
            Assert.That(imported.Inputs.Single().From!.TaskId, Is.EqualTo("upstream-left-behind"));
            Assert.That(imported.Steps[0].RunTaskId, Is.EqualTo("callee-left-behind"));
        });
    }

    [Test]
    public void SuggestedZipName_SlugsTheDisplayName()
    {
        Assert.That(ArchiveService.SuggestedZipName("Email Checks! (v2)"),
            Is.EqualTo("email-checks-v2.automata.zip"));
    }
}

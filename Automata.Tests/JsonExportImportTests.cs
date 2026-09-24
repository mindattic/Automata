using System.IO.Compression;
using System.Text.Json;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using AutoWebNav;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The file side of the database: <c>.automata.json</c> for a task, a collection or the whole
/// workspace, and the zip round trip. Every import loads the database and none overwrites.
/// </summary>
[TestFixture]
public class JsonExportImportTests
{
    private string workDir = null!;
    private TestDb source = null!;
    private TestDb target = null!;

    [SetUp]
    public void SetUp()
    {
        workDir = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workDir);
        source = new TestDb();
        target = new TestDb();
    }

    [TearDown]
    public void TearDown()
    {
        source.Dispose();
        target.Dispose();
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
    }

    private string FilePath(string name) => Path.Combine(workDir, name);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AutomataJson.Options);

    /// <summary>A collection of two tasks wired to each other, with nesting and a fingerprint.</summary>
    private (Collection Collection, TaskDefinition First, TaskDefinition Second) Seed(TestDb db)
    {
        var store = db.Collections();
        var collection = store.CreateCollection("Pipeline");
        var first = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = "Find it",
            Steps =
            [
                new Step
                {
                    Action = StepAction.Group, Label = "outer",
                    Children =
                    [
                        new Step
                        {
                            Action = StepAction.ExtractText, Label = "read",
                            Target = new ElementFingerprint { Tag = "span", CssSelector = "#ticket", ClassList = ["id"] },
                            Outputs = [new OutputField { Name = "id" }],
                        },
                    ],
                },
            ],
        };
        first.Outputs = [new TaskOutput { Name = "ticket", SourceStepId = first.Steps[0].Children[0].Id, SourceOutputField = "id" }];
        store.SaveTask(first);

        var second = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = "Use it",
            Inputs = [new TaskInput { Name = "ticket", From = new TaskOutputRef { TaskId = first.Id, OutputName = "ticket" } }],
            Steps = [new Step { Action = StepAction.RunTask, Label = "call", RunTaskId = first.Id }],
        };
        store.SaveTask(second);
        return (store.GetCollection(collection.Id)!, store.GetTask(first.Id)!, store.GetTask(second.Id)!);
    }

    // ---- single task / collection -------------------------------------------------------------

    [Test]
    public void ATaskExportedAsJson_ImportsIntoAnotherDatabase()
    {
        var (_, first, _) = Seed(source);
        var file = source.Archive().ExportTaskJson(first.Id, FilePath("t.automata.json"));

        var text = File.ReadAllText(file);
        var result = target.Archive().Import(file);

        var imported = result.Tasks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(ArchiveService.IsAutomataJson(text), Is.True);
            Assert.That(JsonDocument.Parse(text).RootElement.GetProperty("type").GetString(), Is.EqualTo("task"));
            Assert.That(result.Collections.Single().Name, Is.EqualTo(ArchiveService.ImportedCollectionName));
            Assert.That(imported.Id, Is.EqualTo(first.Id), "no collision, so the id travels");
            Assert.That(imported.Steps[0].Children[0].Target!.CssSelector, Is.EqualTo("#ticket"));
            Assert.That(imported.Outputs.Single().SourceStepId, Is.EqualTo(imported.Steps[0].Children[0].Id),
                "step ids are fresh, and the references follow them");
            Assert.That(target.Collections().GetTask(first.Id), Is.Not.Null);
        });
    }

    [Test]
    public void ACollectionExportedAsJson_RoundTripsItsTasksAndOrder()
    {
        var (collection, first, second) = Seed(source);
        var file = source.Archive().ExportCollectionJson(collection.Id, FilePath("c.automata.json"));

        var result = target.Archive().Import(file);

        var imported = result.Collections.Single();
        var tasks = target.Collections().LoadTasks(imported.Id);
        Assert.Multiple(() =>
        {
            Assert.That(imported.Name, Is.EqualTo("Pipeline"));
            Assert.That(tasks.Select(t => t.Name), Is.EqualTo(new[] { "Find it", "Use it" }));
            Assert.That(tasks.Select(t => t.Id), Is.EqualTo(new[] { first.Id, second.Id }));
            Assert.That(imported.TaskOrder, Is.EqualTo(new[] { first.Id, second.Id }));
            Assert.That(result.Warnings, Is.Empty);
        });
    }

    /// <summary>The same no-overwrite rules as the zip: back into its own database, every id is
    /// taken, so every one is regenerated and the name is suffixed — and the wiring follows.</summary>
    [Test]
    public void ACollectionJsonImportedOverItself_RegeneratesAndRewires()
    {
        var (collection, first, _) = Seed(source);
        var file = source.Archive().ExportCollectionJson(collection.Id, FilePath("c.automata.json"));

        var result = source.Archive().Import(file);

        var importedFirst = result.Tasks.Single(t => t.Name == "Find it");
        var importedSecond = result.Tasks.Single(t => t.Name == "Use it");
        Assert.Multiple(() =>
        {
            Assert.That(result.Collections.Single().Name, Is.EqualTo("Pipeline (2)"));
            Assert.That(importedFirst.Id, Is.Not.EqualTo(first.Id));
            Assert.That(importedSecond.Steps[0].RunTaskId, Is.EqualTo(importedFirst.Id));
            Assert.That(importedSecond.Inputs.Single().From!.TaskId, Is.EqualTo(importedFirst.Id));
            Assert.That(source.Collections().LoadTasks(collection.Id), Has.Count.EqualTo(2), "original intact");
        });
    }

    /// <summary>A zip export and its import give back exactly the task that went out.</summary>
    [Test]
    public void AZipExportImportsBackToTheSameTaskContent()
    {
        var (collection, first, _) = Seed(source);
        var zip = source.Archive().ExportCollection(collection.Id, FilePath("c.automata.zip"));

        var result = target.Archive().Import(zip);
        var back = target.Collections().GetTask(first.Id)!;

        // Step ids are always re-keyed on import; everything else must match.
        var expected = Json(first).Replace(first.Steps[0].Id, back.Steps[0].Id)
            .Replace(first.Steps[0].Children[0].Id, back.Steps[0].Children[0].Id);
        Assert.That(Json(back).Replace(Json(back.ModifiedUtc), Json(first.ModifiedUtc))
                .Replace(Json(back.CreatedUtc), Json(first.CreatedUtc)),
            Is.EqualTo(expected));
        Assert.That(result.Tasks, Has.Count.EqualTo(2));
    }

    /// <summary>One of the old per-task files, dragged in as it is.</summary>
    [Test]
    public void AnOldTaskFileImportsAsATask()
    {
        var file = FilePath("Old Task.json");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "id": "t1", "collectionId": "c1", "name": "Old Task",
              "description": "", "steps": [ { "id": "s1", "action": "click", "label": "Click it",
              "target": { "tag": "button", "cssSelector": "#go", "classList": [] }, "children": [] } ],
              "createdUtc": "2026-01-01T00:00:00+00:00", "modifiedUtc": "2026-01-01T00:00:00+00:00" }
            """);

        Assert.That(ArchiveService.IsAutomataJson(File.ReadAllText(file)), Is.True);
        var imported = target.Archive().Import(file).Tasks.Single();

        Assert.Multiple(() =>
        {
            Assert.That(imported.Name, Is.EqualTo("Old Task"));
            Assert.That(imported.Steps.Single().Target!.CssSelector, Is.EqualTo("#go"));
            Assert.That(target.Collections().GetCollection(imported.CollectionId)!.Name,
                Is.EqualTo(ArchiveService.ImportedCollectionName));
        });
    }

    [Test]
    public void ARecorderFlowIsNotMistakenForAnAutomataExport()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArchiveService.IsAutomataJson("""{ "title": "Flow", "steps": [ { "type": "navigate", "url": "https://x" } ] }"""), Is.False);
            Assert.That(ArchiveService.IsAutomataJson("not json"), Is.False);
            Assert.That(ArchiveService.IsAutomataJson("""{ "format": "automata-export", "type": "task" }"""), Is.True);
        });
    }

    [Test]
    public void AJsonExportOfAnUnknownTypeIsRefused()
    {
        Assert.That(() => target.Archive().ImportJson("""{ "format": "automata-export", "type": "mystery" }"""),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("mystery"));
    }

    // ---- the whole workspace ------------------------------------------------------------------

    private void SeedWorkspace(TestDb db, out Collection collection, out TaskDefinition first)
    {
        (collection, first, _) = Seed(db);
        db.Datasets().ImportText("skus.csv", "sku,price\nA-1,9.99\n\"B,2\",\"1\"\"5\"\n");
        db.Datasets().ImportText("roster.json", """[ { "Name": "Ada", "Address": { "City": "London" } } ]""");
        db.Schedule().Save(
        [
            new ScheduleEntry
            {
                Id = "nightly", Name = "Nightly", Target = ScheduleTargetKind.Collection, TargetId = collection.Id,
                Triggers = [new TriggerDefinition { Kind = TriggerKind.Cron, CronExpression = "0 2 * * *" }],
            },
            new ScheduleEntry
            {
                Id = "after", Name = "After", Target = ScheduleTargetKind.Task, TargetId = first.Id,
                Triggers = [new TriggerDefinition { Kind = TriggerKind.AfterEntry, AfterEntryId = "nightly" }],
            },
        ]);
        db.Settings().Save(new AutomataSettings { Provider = "gemini", Theme = "light" });

        var runs = db.Runs();
        var run = runs.CreateRun(RunTargetKind.Collection, collection.Id, collection.Name, "schedule");
        runs.AppendEvent(run.RunId, first.Id, new { kind = "StepCompleted", detail = "ok" });
        runs.SaveOutputs(run.RunId, first.Id, new Dictionary<string, Dictionary<string, string>>
        {
            ["s"] = new() { ["id"] = "T-42" },
        });
        runs.CompleteRun(run.RunId, true, "1/1 passed");

        db.Parked().Save(new ParkedRun
        {
            RunId = "parked-run", TaskId = first.Id, TaskName = first.Name, CollectionId = collection.Id,
            Checkpoint = new ParkCheckpoint(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), "waiting",
                [0], "w", "Wait", [new OutputValue("s", "id", "T-42")], new Dictionary<string, string> { ["x"] = "1" }, 1, 0),
        });
    }

    /// <summary>
    /// Into an empty database a workspace import is a restore: exporting the restored workspace
    /// gives back exactly the document that went in — collections, tasks, datasets, schedule,
    /// settings, runs and parked runs alike.
    /// </summary>
    [Test]
    public void AWorkspaceRoundTripsThroughJsonIntoAnEmptyDatabase()
    {
        SeedWorkspace(source, out _, out _);
        var file = source.Archive().ExportWorkspaceJson(FilePath("w.automata.json"));

        var result = target.Archive().Import(file);

        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Describe(), Does.Contain("2 dataset(s)").And.Contain("1 run(s)").And.Contain("settings"));

        var before = Normalize(source.Archive().BuildWorkspaceExport());
        var after = Normalize(target.Archive().BuildWorkspaceExport());
        Assert.That(after, Is.EqualTo(before));
        Assert.That(target.Datasets().Read("roster.json").Single()["Address.City"], Is.EqualTo("London"));
        Assert.That(target.Datasets().Read("skus.csv")[1]["price"], Is.EqualTo("1\"5"));
    }

    /// <summary>Into a populated database nothing is overwritten: colliding ids are regenerated and
    /// every reference — schedule targets and chains, run history, parked runs — follows them.</summary>
    [Test]
    public void AWorkspaceMergedIntoItsOwnDatabaseOverwritesNothing()
    {
        SeedWorkspace(source, out var collection, out var first);
        var file = source.Archive().ExportWorkspaceJson(FilePath("w.automata.json"));

        var result = source.Archive().Import(file);

        var store = source.Collections();
        var copy = result.Collections.Single();
        var copiedFirst = result.Tasks.Single(t => t.Name == "Find it");
        var schedule = source.Schedule().Load();
        Assert.Multiple(() =>
        {
            Assert.That(store.LoadCollections().Select(c => c.Name), Is.EqualTo(new[] { "Pipeline", "Pipeline (2)" }));
            Assert.That(copy.Id, Is.Not.EqualTo(collection.Id));
            Assert.That(copiedFirst.Id, Is.Not.EqualTo(first.Id));
            Assert.That(store.LoadTasks(collection.Id), Has.Count.EqualTo(2), "the original is untouched");
            Assert.That(schedule, Has.Count.EqualTo(4));
            Assert.That(schedule.Count(e => e.TargetId == copy.Id), Is.EqualTo(1), "the copied schedule targets the copy");
            var copiedAfter = schedule.Single(e => e.Name == "After" && e.TargetId == copiedFirst.Id);
            var copiedNightly = schedule.Single(e => e.Name == "Nightly" && e.TargetId == copy.Id);
            Assert.That(copiedAfter.Triggers.Single().AfterEntryId, Is.EqualTo(copiedNightly.Id),
                "and the chain follows the copied entry");
            Assert.That(result.Datasets, Is.Zero, "datasets with the same name are kept, not replaced");
            Assert.That(result.Runs, Is.Zero, "the same run is not recorded twice");
            Assert.That(result.Settings, Is.False);
            Assert.That(result.Warnings, Has.Some.Contains("skus.csv"));
            Assert.That(source.Settings().Load().Provider, Is.EqualTo("gemini"));
        });
    }

    /// <summary>Everything the exported document says, minus what legitimately differs between two
    /// databases (when each row was last written is kept; nothing else is allowed to move).</summary>
    private static string Normalize(JsonExport export)
    {
        export.ExportedUtc = default;
        return Json(export);
    }

    [Test]
    public void TheWorkspaceExportIsReadableJsonWithEverySection()
    {
        SeedWorkspace(source, out _, out _);
        var file = source.Archive().ExportWorkspaceJson(FilePath("w.automata.json"));

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("format").GetString(), Is.EqualTo(ArchiveService.FormatName));
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("workspace"));
            foreach (var section in new[] { "collections", "datasets", "schedule", "settings", "runs", "parked" })
                Assert.That(root.TryGetProperty(section, out _), Is.True, section);
            Assert.That(root.GetProperty("collections")[0].GetProperty("tasks")[0].GetProperty("steps")[0]
                .GetProperty("children")[0].GetProperty("target").GetProperty("cssSelector").GetString(), Is.EqualTo("#ticket"));
        });
    }

    [Test]
    public void TheZipLayoutIsUnchanged()
    {
        var (collection, first, _) = Seed(source);
        var zipPath = source.Archive().ExportCollection(collection.Id, FilePath("c.automata.zip"));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.That(zip.Entries.Select(e => e.FullName),
            Is.SupersetOf(new[] { "manifest.json", "collection.json", $"tasks/{first.Id}.json" }));
    }
}

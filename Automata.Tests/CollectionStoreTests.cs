using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;
using AutoWebNav;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class CollectionStoreTests
{
    private TestDb db = null!;
    private CollectionStore store = null!;

    [SetUp]
    public void SetUp()
    {
        db = new TestDb();
        store = db.Collections();
    }

    [TearDown]
    public void TearDown() => db.Dispose();

    private TaskDefinition NewTask(string collectionId, string name = "My task") => new()
    {
        CollectionId = collectionId,
        Name = name,
        Steps = [new Step { Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" }],
    };

    // ---- a task survives the database exactly ----------------------------------------------------

    /// <summary>
    /// The step tree is stored as one JSON document in the shape a task file always had, so a task
    /// read back must serialize to exactly what went in — nested children, element fingerprints,
    /// bindings, scoped settings, the lot. This is what keeps export, import and the old files
    /// interchangeable.
    /// </summary>
    [Test]
    public void ATaskRoundTripsExactly_NestedStepsFingerprintsAndAll()
    {
        var collection = store.CreateCollection("Round trip");
        var task = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = "Search Google for cats",
            Description = "Demo end-to-end proof point",
            StartUrl = "https://www.google.com",
            Inputs = [new TaskInput { Name = "query", Default = "cats" }],
            Settings = new EngineSettingsOverride { SelfHeal = true, Retry = new RetryPolicy { MaxAttempts = 2 } },
            Demo = new DemoOrigin { Key = "demo-key", FactoryHash = "abc123" },
            Steps =
            [
                new Step { Id = "s1", Action = StepAction.Navigate, Label = "Go", Url = "https://www.google.com" },
                new Step
                {
                    Id = "s2", Action = StepAction.TypeText, Label = "Type", Value = "cats",
                    Target = new ElementFingerprint
                    {
                        Tag = "textarea", NameAttr = "q", AriaRole = "combobox", AriaLabel = "Search",
                        CssSelector = "textarea[name=\"q\"]", XPath = "/html/body/div[1]/form//textarea",
                        ClassList = ["gLFyf"], NearbyLabelText = "Search",
                    },
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.TaskInput, ParameterName = "query" },
                    },
                },
                new Step
                {
                    Id = "s3", Action = StepAction.Group, Label = "Verify",
                    Settings = new EngineSettingsOverride { DefaultStepTimeoutMs = 5000 },
                    Children =
                    [
                        new Step
                        {
                            Id = "s3a", Action = StepAction.WaitForElement, Label = "Results", TimeoutMs = 15000,
                            Target = new ElementFingerprint { Tag = "div", Id = "search", CssSelector = "#search" },
                            Children =
                            [
                                new Step
                                {
                                    Id = "s3a1", Action = StepAction.ExtractText, Label = "Title",
                                    Target = new ElementFingerprint { Tag = "h3", CssSelector = "#search h3", ClassList = ["LC20lb"] },
                                    Outputs = [new OutputField { Name = "title" }],
                                },
                            ],
                        },
                    ],
                },
            ],
            Outputs = [new TaskOutput { Name = "title", SourceStepId = "s3a1", SourceOutputField = "text" }],
        };
        store.SaveTask(task);

        var back = db.Collections().GetTask(task.Id)!;

        Assert.That(JsonSerializer.Serialize(back, AutomataJson.Options),
            Is.EqualTo(JsonSerializer.Serialize(task, AutomataJson.Options)));
        Assert.That(back.Steps[2].Children[0].Children[0].Target!.ClassList, Is.EqualTo(new[] { "LC20lb" }));
    }

    // ---- identity, renames and deletes -----------------------------------------------------------

    [Test]
    public void ARenamedTaskIsStillTheSameTask()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "One");
        store.SaveTask(task);

        task.Name = "Renamed";
        task.Description = "edited after the rename";
        store.SaveTask(task);

        var tasks = store.LoadTasks(collection.Id);
        Assert.Multiple(() =>
        {
            Assert.That(tasks, Has.Count.EqualTo(1), "a rename must not leave a copy behind");
            Assert.That(tasks[0].Id, Is.EqualTo(task.Id));
            Assert.That(tasks[0].Name, Is.EqualTo("Renamed"));
            Assert.That(tasks[0].Description, Is.EqualTo("edited after the rename"));
        });
    }

    [Test]
    public void ARenamedCollectionKeepsItsTasks()
    {
        var collection = store.CreateCollection("Before");
        store.SaveTask(NewTask(collection.Id, "T1"));

        collection.Name = "After";
        store.SaveCollection(collection);

        Assert.Multiple(() =>
        {
            Assert.That(store.LoadCollections().Select(c => c.Name), Is.EqualTo(new[] { "After" }));
            Assert.That(store.LoadTasks(collection.Id).Single().Name, Is.EqualTo("T1"));
        });
    }

    /// <summary>A deleted id must stop resolving.</summary>
    [Test]
    public void ADeletedTaskIsNotStillFoundByItsId()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id);
        store.SaveTask(task);
        Assert.That(store.GetTask(task.Id), Is.Not.Null);

        store.DeleteTask(task.Id);

        Assert.That(store.GetTask(task.Id), Is.Null);
    }

    /// <summary>
    /// Deleting only hides (HOUSE-LAW-2): the row stays, still owns its id — so an import cannot
    /// land a different task on it — and saving that id again brings it back, which is how the
    /// demo generator restores an example somebody deleted.
    /// </summary>
    [Test]
    public void DeletingOnlyHides_AndSavingTheSameIdBringsItBack()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Kept");
        store.SaveTask(task);

        store.DeleteTask(task.Id);
        Assert.Multiple(() =>
        {
            Assert.That(store.LoadTasks(collection.Id), Is.Empty);
            Assert.That(store.TaskIdTaken(task.Id), Is.True, "the hidden row still owns its id");
        });

        store.SaveTask(task);
        Assert.That(store.LoadTasks(collection.Id).Single().Id, Is.EqualTo(task.Id));
    }

    [Test]
    public void DeletingACollectionHidesItAndEveryTaskInIt()
    {
        var collection = store.CreateCollection("Doomed");
        var task = NewTask(collection.Id);
        store.SaveTask(task);

        store.DeleteCollection(collection.Id);

        Assert.Multiple(() =>
        {
            Assert.That(store.LoadCollections(), Is.Empty);
            Assert.That(store.GetTask(task.Id), Is.Null);
            Assert.That(store.LoadAllTasks(), Is.Empty);
            Assert.That(store.CollectionIdTaken(collection.Id), Is.True);
            Assert.Throws<InvalidOperationException>(() => store.SaveTask(NewTask(collection.Id)),
                "a hidden collection takes no new tasks");
        });
    }

    // ---- names -----------------------------------------------------------------------------------

    /// <summary>A name is just a name now — nothing is projected onto a file system, so characters,
    /// device names and lengths that used to need sanitizing on disk round-trip untouched.</summary>
    [Test]
    public void NamesThatWereAwkwardOnDiskRoundTripExactly()
    {
        var longName = new string('a', 150) + " end";
        var collection = store.CreateCollection("Search: Engines?");
        store.SaveTask(NewTask(collection.Id, "Wolf: Tshirts * <cheap>"));
        store.SaveTask(NewTask(collection.Id, "NUL"));
        store.SaveTask(NewTask(collection.Id, "collection"));
        store.SaveTask(NewTask(collection.Id, longName));

        Assert.Multiple(() =>
        {
            Assert.That(store.LoadCollections().Single().Name, Is.EqualTo("Search: Engines?"));
            Assert.That(store.LoadTasks(collection.Id).Select(t => t.Name),
                Is.EquivalentTo(new[] { "Wolf: Tshirts * <cheap>", "NUL", "collection", longName }));
        });
    }

    [Test]
    public void CreateCollection_WithTakenName_GetsNumericSuffix()
    {
        store.CreateCollection("Work");
        var second = store.CreateCollection("work");

        Assert.That(second.Name, Is.EqualTo("work (2)"), "names compare case-insensitively");
        Assert.That(store.LoadCollections(), Has.Count.EqualTo(2));
    }

    [Test]
    public void RenamingACollectionOntoAnothersName_SuffixesInsteadOfMerging()
    {
        store.CreateCollection("Alpha");
        var beta = store.CreateCollection("Beta");

        beta.Name = "Alpha";
        store.SaveCollection(beta);

        Assert.That(beta.Name, Is.EqualTo("Alpha (2)"));
        Assert.That(store.GetCollection(beta.Id)!.Name, Is.EqualTo("Alpha (2)"));
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
    public void TheSameTaskNameInTwoCollectionsIsFine()
    {
        var first = store.CreateCollection("One");
        var second = store.CreateCollection("Two");
        store.SaveTask(NewTask(first.Id, "Same"));
        var other = NewTask(second.Id, "Same");
        store.SaveTask(other);

        Assert.That(other.Name, Is.EqualTo("Same"));
    }

    // ---- CRUD / move / duplicate ------------------------------------------------------------------

    [Test]
    public void SaveTask_AppendsToTaskOrder()
    {
        var collection = store.CreateCollection("Google Searches");
        var task = NewTask(collection.Id, "Wolf Tshirts");

        store.SaveTask(task);

        Assert.That(store.LoadTasks(collection.Id).Single().Id, Is.EqualTo(task.Id));
        Assert.That(store.GetCollection(collection.Id)!.TaskOrder, Is.EqualTo(new[] { task.Id }));
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
    public void SaveTask_IntoACollectionThatDoesNotExist_Refuses()
    {
        Assert.Throws<InvalidOperationException>(() => store.SaveTask(NewTask("no-such-collection")));
    }

    [Test]
    public void DeleteTask_HidesItAndRemovesTheTaskOrderEntry()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id);
        store.SaveTask(task);

        store.DeleteTask(task.Id);

        Assert.That(store.LoadTasks(collection.Id), Is.Empty);
        Assert.That(store.GetCollection(collection.Id)!.TaskOrder, Is.Empty);
    }

    [Test]
    public void MoveTask_ReparentsItAndBothOrdersFollow()
    {
        var from = store.CreateCollection("From");
        var to = store.CreateCollection("To");
        var task = NewTask(from.Id, "Mover");
        store.SaveTask(task);

        var moved = store.MoveTask(task.Id, to.Id);

        Assert.That(moved.CollectionId, Is.EqualTo(to.Id));
        Assert.That(store.LoadTasks(to.Id).Single().Id, Is.EqualTo(task.Id));
        Assert.That(store.LoadTasks(from.Id), Is.Empty);
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
        Assert.That(store.LoadTasks(collection.Id), Has.Count.EqualTo(2));
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
        Assert.That(copy.TaskOrder, Is.EqualTo(new[] { copiedTasks.Single().Id }));
        Assert.That(store.LoadTasks(source.Id), Has.Count.EqualTo(1)); // source untouched
    }

    // ---- a copy has to be wired to ITSELF ----------------------------------------------------
    //
    // A duplicate gives every step a fresh id, because step ids are only unique within a task and
    // two tasks answering to one id would make a self-heal or a park ambiguous. What it did NOT do
    // is rewrite the REFERENCES to those ids, of which a task is full: a binding to an earlier
    // step's output, an `otherwise` that records which `if` it belongs to, a declared task output
    // naming the step that produces it, a live wait whose condition reads the element it watches.
    // Every one of them was left pointing at the ORIGINAL's step, so the copy still loaded, still
    // looked right in the editor, and failed at run time with "has not been produced yet" about a
    // value the step right above it publishes.

    /// <summary>The reference a duplicate is most likely to have: type what the step before read.</summary>
    [Test]
    public void DuplicatingATask_RewiresAStepOutputBindingToTheCopiedStep()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Read then type");
        var read = new Step
        {
            Action = StepAction.ExtractText, Label = "read total",
            Outputs = [new OutputField { Name = "total" }],
        };
        var type = new Step
        {
            Action = StepAction.TypeText, Label = "type it",
            Bindings = new Dictionary<string, BindingRef>
            {
                ["Value"] = new() { Kind = BindingKind.StepOutput, SourceStepId = read.Id, OutputField = "total" },
            },
        };
        task.Steps = [read, type];
        store.SaveTask(task);

        var copy = store.DuplicateTask(task.Id);

        Assert.That(copy.Steps[1].Bindings!["Value"].SourceStepId, Is.EqualTo(copy.Steps[0].Id),
            "the copy's binding must name the copy's own step, not the original's");
    }

    /// <summary>
    /// The rest of the references, in one task: an `otherwise`'s pairing, the task's declared
    /// output, a write step's column, a loop's own condition, and a wait that watches an element —
    /// the compiler always points that last one's condition at the wait step itself.
    /// </summary>
    [Test]
    public void DuplicatingATask_RewiresEveryOtherKindOfReferenceToo()
    {
        var collection = store.CreateCollection("C");
        var task = NewTask(collection.Id, "Everything");
        var read = new Step
        {
            Action = StepAction.ExtractText, Label = "read", Outputs = [new OutputField { Name = "sku" }],
        };
        var guard = new Step
        {
            Action = StepAction.If, Label = "if it read",
            Condition = new ConditionSpec
            {
                Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = read.Id, OutputField = "sku" },
                Op = ConditionOp.NotEmpty,
            },
        };
        var otherwise = new Step { Action = StepAction.Else, Label = "otherwise", PairedIfId = guard.Id };
        var write = new Step
        {
            Action = StepAction.WriteDataset, Label = "save",
            WriteDataset = new DatasetWriteSpec
            {
                DatasetName = "found.csv",
                Columns = new Dictionary<string, BindingRef>
                {
                    ["sku"] = new() { Kind = BindingKind.StepOutput, SourceStepId = read.Id, OutputField = "sku" },
                },
            },
        };
        var watch = new Step { Action = StepAction.Wait, Label = "wait for it" };
        watch.Wait = new WaitSpec
        {
            Mode = WaitMode.UntilCondition,
            Condition = new ConditionSpec
            {
                Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = watch.Id, OutputField = "value" },
                Op = ConditionOp.Equals,
                Right = new BindingRef { Kind = BindingKind.Literal, Literal = "Ready" },
            },
        };
        task.Steps = [read, guard, otherwise, write, watch];
        task.Outputs = [new TaskOutput { Name = "sku", SourceStepId = read.Id, SourceOutputField = "sku" }];
        store.SaveTask(task);

        var copy = store.DuplicateTask(task.Id);
        var (copiedRead, copiedGuard, copiedElse, copiedWrite, copiedWatch) =
            (copy.Steps[0], copy.Steps[1], copy.Steps[2], copy.Steps[3], copy.Steps[4]);

        Assert.Multiple(() =>
        {
            Assert.That(copiedGuard.Condition!.Left.SourceStepId, Is.EqualTo(copiedRead.Id),
                "the guard reads the copy's own extract");
            Assert.That(copiedElse.PairedIfId, Is.EqualTo(copiedGuard.Id),
                "the otherwise belongs to the copy's own if");
            Assert.That(copiedWrite.WriteDataset!.Columns["sku"].SourceStepId, Is.EqualTo(copiedRead.Id),
                "the column is filled from the copy's own extract");
            Assert.That(copiedWatch.Wait!.Condition!.Left.SourceStepId, Is.EqualTo(copiedWatch.Id),
                "a watching wait reads itself, so the copy must read the copy");
            Assert.That(copy.Outputs.Single().SourceStepId, Is.EqualTo(copiedRead.Id),
                "and what the task publishes comes from the copy's own step");
        });
    }

    /// <summary>
    /// The other half of a remap: it rewrites what it has an answer for and leaves everything else
    /// exactly as it was. Duplicating ONE task hands the rewrite a map of one id, so every
    /// reference in the copy that points outside it — a runTask step calling a sibling, a binding
    /// that names another task's step — is a reference the map is silent about. The natural way to
    /// write the rewrite ("set it to what the map says") clears all of them.
    /// </summary>
    [Test]
    public void DuplicatingATask_LeavesReferencesOutsideItAlone()
    {
        var collection = store.CreateCollection("C");
        var sibling = NewTask(collection.Id, "Sibling");
        store.SaveTask(sibling);

        var task = NewTask(collection.Id, "Caller");
        task.Steps =
        [
            new Step { Action = StepAction.RunTask, Label = "call the sibling", RunTaskId = sibling.Id },
            new Step
            {
                Action = StepAction.TypeText, Label = "type what the sibling read",
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Value"] = new()
                    {
                        Kind = BindingKind.StepOutput,
                        SourceTaskId = sibling.Id,
                        SourceStepId = sibling.Steps[0].Id,
                        OutputField = "total",
                    },
                },
            },
        ];
        store.SaveTask(task);

        var copy = store.DuplicateTask(task.Id);
        var binding = copy.Steps[1].Bindings!["Value"];

        Assert.Multiple(() =>
        {
            Assert.That(copy.Steps[0].RunTaskId, Is.EqualTo(sibling.Id),
                "the copy still calls the sibling it was written to call");
            Assert.That(binding.SourceTaskId, Is.EqualTo(sibling.Id));
            Assert.That(binding.SourceStepId, Is.EqualTo(sibling.Steps[0].Id),
                "and a step in another task is not one of the ids being re-keyed");
        });
    }

    /// <summary>
    /// A duplicated collection is a whole pipeline, so the wiring BETWEEN its tasks has to follow
    /// it too. Left alone, task 2 of the copy took its input from task 1 of the ORIGINAL — which
    /// does not run in this collection, so every run fell back to a default and said so.
    /// </summary>
    [Test]
    public void DuplicatingACollection_RewiresTheTasksToEachOther()
    {
        var source = store.CreateCollection("Pipeline");
        var first = NewTask(source.Id, "Find it");
        first.Steps[0].Outputs = [new OutputField { Name = "id" }];
        first.Outputs = [new TaskOutput { Name = "ticket", SourceStepId = first.Steps[0].Id, SourceOutputField = "id" }];
        store.SaveTask(first);

        var second = NewTask(source.Id, "Use it");
        second.Inputs = [new TaskInput
        {
            Name = "ticket",
            From = new TaskOutputRef { TaskId = first.Id, TaskName = "Find it", OutputName = "ticket" },
        }];
        second.Steps[0].Action = StepAction.RunTask;
        second.Steps[0].RunTaskId = first.Id;
        store.SaveTask(second);

        var copy = store.DuplicateCollection(source.Id);
        var copied = store.LoadTasks(copy.Id);
        var copiedFirst = copied.Single(t => t.Name == "Find it");
        var copiedSecond = copied.Single(t => t.Name == "Use it");

        Assert.Multiple(() =>
        {
            Assert.That(copiedSecond.Inputs.Single().From!.TaskId, Is.EqualTo(copiedFirst.Id),
                "the copied wiring names the copied upstream task");
            Assert.That(copiedSecond.Steps[0].RunTaskId, Is.EqualTo(copiedFirst.Id),
                "and a runTask step calls the copy rather than reaching back into the original");
            Assert.That(copiedFirst.Outputs.Single().SourceStepId, Is.EqualTo(copiedFirst.Steps[0].Id));
        });
    }

    [Test]
    public void LoadTasks_OrdersByTaskOrder_UnlistedSortLastByName()
    {
        var collection = store.CreateCollection("C");
        var a = NewTask(collection.Id, "Alpha");
        var b = NewTask(collection.Id, "Beta");
        var stray = NewTask(collection.Id, "AAA stray");
        store.SaveTask(a);
        store.SaveTask(b);
        store.SaveTask(stray);

        // An order that does not list every task — the stray one sorts last, by name.
        var reordered = store.GetCollection(collection.Id)!;
        reordered.TaskOrder = [b.Id, a.Id];
        store.SaveCollection(reordered);

        var tasks = store.LoadTasks(collection.Id);

        Assert.That(tasks.Select(t => t.Name), Is.EqualTo(new[] { "Beta", "Alpha", "AAA stray" }));
    }

    /// <summary>
    /// Deleting a task tidies the order of the collection it was actually IN — and only that one.
    /// (The file store once cleaned the wrong collection's order, leaving the real one listing a
    /// task that no longer existed.)
    /// </summary>
    [Test]
    public void DeleteTask_TidiesTheOrderOfTheCollectionTheTaskWasActuallyIn()
    {
        var first = store.CreateCollection("Aaa first");
        var second = store.CreateCollection("Bbb second");

        // A task in each, so the delete has to pick the right one.
        var decoy = NewTask(first.Id, "Decoy");
        store.SaveTask(decoy);
        var doomed = NewTask(second.Id, "Doomed");
        store.SaveTask(doomed);

        // Edited twice first — the ordinary case, and the one the file store used to get wrong.
        doomed.StartUrl = "https://edited.example";
        store.SaveTask(doomed);

        store.DeleteTask(doomed.Id);

        Assert.Multiple(() =>
        {
            Assert.That(store.GetTask(doomed.Id), Is.Null, "the task itself is gone");
            Assert.That(store.GetCollection(second.Id)!.TaskOrder, Does.Not.Contain(doomed.Id),
                "and its own collection no longer orders an id that points at nothing");
            Assert.That(store.GetCollection(first.Id)!.TaskOrder, Is.EqualTo(new[] { decoy.Id }),
                "while the other collection is left exactly as it was");
        });
    }

    /// <summary>The same confusion the other way round: after a move, a save must not leave a
    /// copy behind in the collection the task came from.</summary>
    [Test]
    public void SavingATaskAfterItMoved_LeavesNoCopyBehindInTheOldCollection()
    {
        var from = store.CreateCollection("From");
        var to = store.CreateCollection("To");
        var task = NewTask(from.Id, "Traveller");
        store.SaveTask(task);

        var moved = store.MoveTask(task.Id, to.Id);
        moved.Name = "Traveller renamed";
        store.SaveTask(moved);

        Assert.Multiple(() =>
        {
            Assert.That(store.LoadTasks(from.Id), Is.Empty, "nothing is left in the old collection");
            Assert.That(store.LoadTasks(to.Id).Select(t => t.Name),
                Is.EqualTo(new[] { "Traveller renamed" }));
        });
    }
}

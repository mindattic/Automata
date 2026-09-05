using System.IO;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// One task handing a value to the next.
/// <para>
/// The unit half pins the precedence rules, which are the part that decides what a run actually
/// does when two answers are available. The end-to-end half runs a real three-task collection
/// through the dispatcher, because the rules being right is not the same claim as the values
/// arriving — and the second is the one a user would notice.
/// </para>
/// </summary>
[TestFixture]
public class TaskPipelineTests
{
    private string root = null!;
    private CollectionStore collections = null!;
    private DatasetStore datasets = null!;
    private RunStore runs = null!;
    private AutomataSettingsStore settings = null!;
    private FakeBrowserSurfaceFactory factory = null!;
    private StringWriter output = null!;
    private ParkedRunStore parked = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
        datasets = new DatasetStore(Path.Combine(root, "datasets"));
        runs = new RunStore(Path.Combine(root, "runs"));
        settings = new AutomataSettingsStore(Path.Combine(root, "settings.json"));
        factory = new FakeBrowserSurfaceFactory();
        output = new StringWriter();
        parked = new ParkedRunStore(Path.Combine(root, "parked"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private RunnerCliDispatcher Dispatcher() => new(
        collections,
        runs,
        new WorkflowEngine(new ReplayEngine(new FingerprintResolver { PollIntervalMs = 10 }), collections, datasets),
        settings,
        factory,
        output,
        parked: parked);

    // ---- the rules ---------------------------------------------------------------------------

    private static TaskDefinition Wired(string inputName, string fromTaskId, string outputName, string? fallback) =>
        new()
        {
            Name = "Second",
            Inputs =
            [
                new TaskInput
                {
                    Name = inputName,
                    Default = fallback,
                    From = new TaskOutputRef { TaskId = fromTaskId, TaskName = "First", OutputName = outputName },
                },
            ],
        };

    [Test]
    public void AWiredInputTakesTheValueTheEarlierTaskPublished()
    {
        var carried = new TaskPipeline.Carried();
        carried.Record("first", new Dictionary<string, string> { ["orderId"] = "A-1001" });

        var (inputs, notes) = TaskPipeline.Resolve(
            Wired("orderId", "first", "orderId", fallback: "none"), carried, supplied: null);

        Assert.That(inputs["orderId"], Is.EqualTo("A-1001"));
        Assert.That(notes.Single(), Does.Contain("A-1001"), "the log has to say what was carried");
    }

    /// <summary>
    /// A value supplied directly wins. Re-running one task by hand with <c>--input</c> is the
    /// reason to have the rule at all, and it would not work if the wiring could overrule it.
    /// </summary>
    [Test]
    public void ASuppliedValueBeatsTheWiring()
    {
        var carried = new TaskPipeline.Carried();
        carried.Record("first", new Dictionary<string, string> { ["orderId"] = "A-1001" });

        var (inputs, notes) = TaskPipeline.Resolve(
            Wired("orderId", "first", "orderId", fallback: "none"),
            carried,
            supplied: new Dictionary<string, string> { ["orderId"] = "B-2002" });

        Assert.That(inputs["orderId"], Is.EqualTo("B-2002"));
        Assert.That(notes.Single(), Does.Contain("supplied directly"), "and it should say the wiring was skipped");
    }

    /// <summary>
    /// The wiring is a hint, never a requirement: a task whose upstream has not run falls back to
    /// its own default. That is what keeps a wired task runnable on its own, which is the only
    /// reason wiring it into a collection is safe.
    /// </summary>
    [Test]
    public void AWiringWhoseTaskHasNotRunLeavesTheInputToItsDefault()
    {
        var (inputs, notes) = TaskPipeline.Resolve(
            Wired("orderId", "first", "orderId", fallback: "none"),
            new TaskPipeline.Carried(),
            supplied: null);

        Assert.That(inputs, Does.Not.ContainKey("orderId"),
            "nothing is supplied, so the engine falls back to the declared default");
        Assert.That(notes.Single(), Does.Contain("has not run in this collection"));
    }

    [Test]
    public void ATaskThatRanButPublishedNothingSaysSoDifferently()
    {
        var carried = new TaskPipeline.Carried();
        carried.Record("first", new Dictionary<string, string>());

        var (_, notes) = TaskPipeline.Resolve(
            Wired("orderId", "first", "orderId", fallback: "none"), carried, supplied: null);

        Assert.That(notes.Single(), Does.Contain("published nothing"),
            "'it did not run' and 'it ran and found nothing' are different problems");
    }

    /// <summary>A task that runs twice in one collection publishes what it found the LAST time —
    /// the only answer that is true of the run so far.</summary>
    [Test]
    public void RunningATaskTwiceRepublishesRatherThanMerging()
    {
        var carried = new TaskPipeline.Carried();
        carried.Record("first", new Dictionary<string, string> { ["orderId"] = "A-1001" });
        carried.Record("first", new Dictionary<string, string> { ["orderId"] = "A-1002" });

        var (inputs, _) = TaskPipeline.Resolve(
            Wired("orderId", "first", "orderId", fallback: null), carried, supplied: null);

        Assert.That(inputs["orderId"], Is.EqualTo("A-1002"));
    }

    // ---- end to end --------------------------------------------------------------------------

    private const string FOUND_BUSY = """{ "isProcessing": false }""";

    private const string FOUND_ELEMENT = """
        { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
          "candidateCount": 1, "centerX": 1, "centerY": 2 }
        """;

    private const string FOUND_NOTHING = """{ "found": false, "ambiguous": false, "candidateCount": 0 }""";

    private const string READ_TICKET = """{ "ok": true, "value": "TCK-9001" }""";

    /// <summary>
    /// Answers a resolve, then a text read, with <paramref name="value"/> — the shape of a page
    /// that has one readable element on it.
    /// </summary>
    private void PageReads(string value) => factory.Responder = script =>
    {
        if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
        if (script.Contains("__automataResolve(")) return """
            { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
              "candidateCount": 1, "centerX": 1, "centerY": 2 }
            """;
        if (script.Contains("textContent")) return $$"""{ "ok": true, "value": "{{value}}" }""";
        return "{}";
    };

    private Collection SeedPipeline(string? middleDefault = "unwired")
    {
        var collection = collections.CreateCollection("Pipeline");

        collections.SaveTask(new TaskDefinition
        {
            Id = "find", CollectionId = collection.Id, Name = "Find",
            Steps =
            [
                new Step
                {
                    Id = "read-id", Action = StepAction.ExtractText, Label = "Read the id",
                    Target = new ElementFingerprint { CssSelector = "#next" },
                    Outputs = [new OutputField { Name = "text" }],
                },
            ],
            Outputs = [new TaskOutput { Name = "ticketId", SourceStepId = "read-id" }],
        });

        collections.SaveTask(new TaskDefinition
        {
            Id = "use", CollectionId = collection.Id, Name = "Use",
            Steps =
            [
                new Step
                {
                    Id = "type-id", Action = StepAction.TypeText, Label = "Type the id",
                    Target = new ElementFingerprint { CssSelector = "#box" },
                    Value = middleDefault,
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.TaskInput, ParameterName = "ticketId" },
                    },
                },
            ],
            Inputs =
            [
                new TaskInput
                {
                    Name = "ticketId",
                    Default = middleDefault,
                    From = new TaskOutputRef { TaskId = "find", TaskName = "Find", OutputName = "ticketId" },
                },
            ],
        });

        return collection;
    }

    /// <summary>
    /// The claim the whole feature rests on: what the first task read off the page is what the
    /// second task typed. Asserted against the browser's own recorded calls, not against the log —
    /// a log line saying a value was carried is not evidence that anything used it.
    /// </summary>
    [Test]
    public async Task WhatTheFirstTaskFoundIsWhatTheSecondTaskUses()
    {
        SeedPipeline();
        PageReads("TCK-9001");

        var code = await Dispatcher().DispatchAsync(["run", "--collection", "Pipeline"]);

        var typed = factory.Sessions
            .SelectMany(b => b.Fake.Calls.Where(c => c.Method == "TypeText").Select(c => c.Args))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(typed, Has.Count.EqualTo(1));
            Assert.That(typed[0], Does.Contain("TCK-9001"),
                $"the second task should have typed what the first one read, got: {typed[0]}");
        });
    }

    /// <summary>
    /// One browser for the whole collection. Two tasks in a row must not each open one: the reason
    /// a collection is a pipeline at all is that task 2 arrives on the page task 1 left behind.
    /// </summary>
    [Test]
    public async Task TheWholeCollectionRunsOnOneBrowser()
    {
        SeedPipeline();
        PageReads("TCK-9001");

        await Dispatcher().DispatchAsync(["run", "--collection", "Pipeline"]);

        Assert.That(factory.Requested, Has.Count.EqualTo(1),
            $"expected one browser for the run, got {factory.Requested.Count}");
    }

    [Test]
    public async Task TheRunSaysWhatWasCarriedAndWhereItCameFrom()
    {
        SeedPipeline();
        PageReads("TCK-9001");

        await Dispatcher().DispatchAsync(["run", "--collection", "Pipeline"]);

        var written = output.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Contain("published ticketId"));
            Assert.That(written, Does.Contain("'ticketId' ← 'Find → ticketId'"));
        });
    }

    /// <summary>
    /// Running the wired task ALONE falls back to its default rather than failing. A task that
    /// could only run as part of its collection would be a task nobody could fix on its own.
    /// </summary>
    [Test]
    public async Task TheWiredTaskStillRunsOnItsOwn()
    {
        SeedPipeline(middleDefault: "TCK-0000");
        PageReads("TCK-9001");

        var code = await Dispatcher().DispatchAsync(["run", "--task", "Use"]);

        var typed = factory.Sessions
            .SelectMany(b => b.Fake.Calls.Where(c => c.Method == "TypeText").Select(c => c.Args))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(typed.Single(), Does.Contain("TCK-0000"), "it should use its declared default");
        });
    }

    /// <summary>
    /// <c>--input</c> beats the wiring in a real run too, which is how one task of a pipeline is
    /// re-run against a particular value without editing anything.
    /// </summary>
    [Test]
    public async Task SupplyingAValueOnTheCommandLineOverridesWhatTheCollectionCarried()
    {
        SeedPipeline();
        PageReads("TCK-9001");

        await Dispatcher().DispatchAsync(
            ["run", "--collection", "Pipeline", "--input", "ticketId=TCK-7777"]);

        var typed = factory.Sessions
            .SelectMany(b => b.Fake.Calls.Where(c => c.Method == "TypeText").Select(c => c.Args))
            .ToList();

        Assert.That(typed.Single(), Does.Contain("TCK-7777"));
    }

    /// <summary>
    /// A task that fails after capturing its value still publishes it. The collection carries on
    /// past a failed task by default, so the choice is between handing the next task what was
    /// actually read and handing it a default nobody chose — and the failure is already reported in
    /// the summary and the exit code.
    /// </summary>
    [Test]
    public async Task ATaskThatFailedStillPublishesWhatItDidProduce()
    {
        var collection = collections.CreateCollection("Pipeline");
        collections.SaveTask(new TaskDefinition
        {
            Id = "find", CollectionId = collection.Id, Name = "Find",
            Steps =
            [
                new Step
                {
                    Id = "read-id", Action = StepAction.ExtractText, Label = "Read the id",
                    Target = new ElementFingerprint { CssSelector = "#next" },
                    Outputs = [new OutputField { Name = "text" }],
                },
                new Step
                {
                    Id = "boom", Action = StepAction.Click, Label = "Click nothing",
                    Target = new ElementFingerprint { CssSelector = "#missing" }, TimeoutMs = 5,
                },
            ],
            Outputs = [new TaskOutput { Name = "ticketId", SourceStepId = "read-id" }],
        });

        var resolves = 0;
        factory.Responder = script =>
        {
            if (script.Contains("isProcessing")) return FOUND_BUSY;
            // The first resolve finds what the reading step wants; every one after it finds
            // nothing, which fails the task AFTER the value has already been captured.
            if (script.Contains("__automataResolve(")) return ++resolves == 1 ? FOUND_ELEMENT : FOUND_NOTHING;
            if (script.Contains("textContent")) return READ_TICKET;
            return "{}";
        };

        var code = await Dispatcher().DispatchAsync(["run", "--collection", "Pipeline"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.RunFailed), "the failure is still reported");
            Assert.That(output.ToString(), Does.Contain("published ticketId"),
                "and what it did read is still handed on");
        });
    }

    /// <summary>
    /// An output nobody produced is not published as an empty string. A blank that looks like a
    /// value is the worst outcome available here: the task downstream would run, do the wrong
    /// thing, and report success.
    /// </summary>
    [Test]
    public async Task AnOutputNothingProducedIsNotPublishedAsBlank()
    {
        var collection = collections.CreateCollection("Pipeline");
        collections.SaveTask(new TaskDefinition
        {
            Id = "find", CollectionId = collection.Id, Name = "Find",
            Steps = [new Step { Id = "nav", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" }],
            // Names a step that publishes nothing at all.
            Outputs = [new TaskOutput { Name = "ticketId", SourceStepId = "nav" }],
        });

        await Dispatcher().DispatchAsync(["run", "--collection", "Pipeline"]);

        var written = output.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Not.Contain("published ticketId"));
            Assert.That(written, Does.Contain("nothing produced this run"),
                "silence here would leave the task downstream running on a blank");
        });
    }
}

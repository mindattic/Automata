using System.IO;
using Automata.Core.Automation.Data;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class WorkflowEngineTests
{
    private string root = null!;
    private CollectionStore collections = null!;
    private DatasetStore datasets = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
        datasets = new DatasetStore(Path.Combine(root, "datasets"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private WorkflowEngine Engine() =>
        new(new ReplayEngine(new FingerprintResolver { PollIntervalMs = 10 }), collections, datasets);

    private static ReplayOptions Options() => new()
    {
        DefaultStepTimeoutMs = 300,
        SettlePollMs = 1,
        Control = new ReplayControl(),
    };

    private static ElementFingerprint Target() => new() { Tag = "input", CssSelector = "#field" };

    private const string ResolveFoundCss = """
        { "found": true, "unique": true, "strategy": "css", "ambiguous": false, "candidateCount": 1,
          "centerX": 10, "centerY": 20, "tag": "input", "text": "x" }
        """;

    private static FakeBrowserSurface Browser(string readText = "hello") => new()
    {
        DefaultEvalResponse = script =>
        {
            if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
            if (script.Contains("__automataResolve(")) return ResolveFoundCss;
            if (script.Contains("textContent")) return $$"""{ "ok": true, "value": "{{readText}}" }""";
            if (script.Contains("document.activeElement")) return $$"""{ "value": "{{readText}}" }""";
            return "{}";
        },
    };

    private async Task<List<StepEvent>> Run(TaskDefinition task, FakeBrowserSurface browser)
    {
        var events = new List<StepEvent>();
        await foreach (var evt in Engine().RunAsync(task, Options(), browser))
            events.Add(evt);
        return events;
    }

    private static Step Nav(string id, string url = "https://x.example") =>
        new() { Id = id, Action = StepAction.Navigate, Label = id, Url = url };

    private static List<string> NavigatedUrls(FakeBrowserSurface browser) =>
        browser.Calls.Where(c => c.Method == "Navigate").Select(c => c.Args).ToList();

    // ---- if ------------------------------------------------------------------------------------

    [Test]
    public async Task If_RunsItsChildrenWhenTheConditionHolds()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "gate", Action = StepAction.If, Label = "When set",
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.Literal, Literal = "yes" },
                        Op = ConditionOp.NotEmpty,
                    },
                    Children = [Nav("inside")],
                },
            ],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task If_SkipsItsChildrenWhenTheConditionDoesNotHold()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "gate", Action = StepAction.If,
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.Literal, Literal = "" },
                        Op = ConditionOp.NotEmpty,
                    },
                    Children = [Nav("inside")],
                },
                Nav("after"),
            ],
        };

        var events = await Run(task, browser);

        Assert.Multiple(() =>
        {
            Assert.That(NavigatedUrls(browser), Has.Count.EqualTo(1), "only the step after the gate should run");
            Assert.That(events.OfType<StepEvent.StepCompleted>().First().Message, Does.Contain("skipped"));
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True,
                "a condition that does not hold is not a failure");
        });
    }

    /// <summary>
    /// The case the whole feature exists for: a value read off the page decides what happens next,
    /// which is why conditions cannot be evaluated ahead of the run.
    /// </summary>
    [Test]
    public async Task If_ComparesAgainstAValueCapturedEarlierInTheRun()
    {
        var browser = Browser("$19.99");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "price" }],
                },
                new Step
                {
                    Id = "gate", Action = StepAction.If,
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "price" },
                        Op = ConditionOp.LessThan,
                        Right = new BindingRef { Kind = BindingKind.Literal, Literal = "20" },
                    },
                    Children = [Nav("cheap")],
                },
            ],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Has.Count.EqualTo(1),
            "$19.99 should compare as less than 20 despite the currency symbol");
    }

    [Test]
    public async Task If_WithAnUncomparableValue_FailsWithAReadableReason()
    {
        var browser = Browser("out of stock");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "price" }],
                },
                new Step
                {
                    Id = "gate", Action = StepAction.If,
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "price" },
                        Op = ConditionOp.LessThan,
                        Right = new BindingRef { Kind = BindingKind.Literal, Literal = "20" },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message, Does.Contain("not a number"));
    }

    // ---- forEach -------------------------------------------------------------------------------

    [Test]
    public async Task ForEach_RunsItsChildrenOncePerRow_WithColumnsInScope()
    {
        datasets.Write("skus.csv", [
            new Dictionary<string, string> { ["sku"] = "aaa" },
            new Dictionary<string, string> { ["sku"] = "bbb" },
        ], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach, Label = "Each sku",
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
                    },
                    Children =
                    [
                        new Step
                        {
                            Id = "open", Action = StepAction.Navigate,
                            Bindings = new Dictionary<string, BindingRef>
                            {
                                ["Url"] = new()
                                {
                                    Kind = BindingKind.DatasetColumn, ColumnName = "sku",
                                    Prefix = "https://shop.example/",
                                },
                            },
                        },
                    ],
                },
            ],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://shop.example/aaa", "https://shop.example/bbb" }));
    }

    [Test]
    public async Task ForEach_OverAMissingDataset_FailsWithThePathNamed()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach,
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "nope.csv" },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(completed.Message, Does.Contain("nope.csv"));
    }

    /// <summary>Row variables belong to their loop; leaking them would make a later binding resolve
    /// to a stale row instead of failing honestly.</summary>
    [Test]
    public async Task ForEach_RowVariablesDoNotLeakPastTheLoop()
    {
        datasets.Write("skus.csv", [new Dictionary<string, string> { ["sku"] = "aaa" }], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach,
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
                    },
                    Children = [Nav("inside")],
                },
                new Step
                {
                    Id = "after", Action = StepAction.Navigate,
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Url"] = new() { Kind = BindingKind.DatasetColumn, ColumnName = "sku" },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message, Does.Contain("enclosing for-each"));
    }

    [Test]
    public async Task ForEach_AskingForConcurrencySaysItIsRunningInOrder()
    {
        datasets.Write("skus.csv", [new Dictionary<string, string> { ["sku"] = "a" }], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach, Label = "Each",
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
                        MaxConcurrency = 4,
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.Log>().Any(l => l.Message.Contains("single browser")), Is.True,
            "asking for concurrency this run cannot provide should be said out loud, not ignored");
    }

    // ---- writeDataset --------------------------------------------------------------------------

    [Test]
    public async Task WriteDataset_AppendsARowBuiltFromBindings()
    {
        var browser = Browser("$24.50");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "price" }],
                },
                new Step
                {
                    Id = "save", Action = StepAction.WriteDataset,
                    WriteDataset = new DatasetWriteSpec
                    {
                        DatasetName = "bought.csv",
                        Columns = new Dictionary<string, BindingRef>
                        {
                            ["sku"] = new() { Kind = BindingKind.Literal, Literal = "WT-100" },
                            ["paid"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "price" },
                        },
                    },
                },
            ],
        };

        await Run(task, browser);

        var rows = datasets.Read("bought.csv");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0]["sku"], Is.EqualTo("WT-100"));
            Assert.That(rows[0]["paid"], Is.EqualTo("$24.50"));
        });
    }

    [Test]
    public async Task WriteDataset_WithAnUnresolvableColumn_FailsAndWritesNothing()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "save", Action = StepAction.WriteDataset,
                    WriteDataset = new DatasetWriteSpec
                    {
                        DatasetName = "bought.csv",
                        Columns = new Dictionary<string, BindingRef>
                        {
                            ["paid"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "missing", OutputField = "price" },
                        },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(datasets.Exists("bought.csv"), Is.False, "a failed write must not leave a partial row");
    }

    // ---- runTask -------------------------------------------------------------------------------

    [Test]
    public async Task RunTask_RunsAnotherTasksStepsInline()
    {
        var collection = collections.CreateCollection("C");
        var inner = new TaskDefinition
        {
            Id = "inner", CollectionId = collection.Id, Name = "Inner",
            Steps = [Nav("inner-step", "https://inner.example")],
        };
        collections.SaveTask(inner);

        var browser = Browser();
        var outer = new TaskDefinition
        {
            Id = "outer", Name = "Outer",
            Steps = [new Step { Id = "call", Action = StepAction.RunTask, RunTaskId = "inner" }],
        };

        await Run(outer, browser);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[] { "https://inner.example" }));
    }

    [Test]
    public async Task RunTask_ThatCallsItself_IsStoppedRatherThanLooping()
    {
        var collection = collections.CreateCollection("C");
        var selfCalling = new TaskDefinition
        {
            Id = "loop", CollectionId = collection.Id, Name = "Loops",
            Steps = [new Step { Id = "call", Action = StepAction.RunTask, RunTaskId = "loop" }],
        };
        collections.SaveTask(selfCalling);

        var browser = Browser();
        var events = await Run(selfCalling, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message, Does.Contain("cannot invoke itself"));
    }

    [Test]
    public async Task RunTask_ForAMissingTask_FailsWithTheIdNamed()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "call", Action = StepAction.RunTask, RunTaskId = "ghost" }],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message, Does.Contain("ghost"));
    }

    // ---- wait until a condition ------------------------------------------------------------------

    [Test]
    public async Task WaitUntilCondition_PassesAsSoonAsItHolds()
    {
        var browser = Browser("ready");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "status" }],
                },
                new Step
                {
                    Id = "hold", Action = StepAction.Wait,
                    Wait = new WaitSpec
                    {
                        Mode = WaitMode.UntilCondition,
                        PollMs = 50,
                        TimeoutMs = 2000,
                        Condition = new ConditionSpec
                        {
                            Left = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "status" },
                            Op = ConditionOp.Equals,
                            Right = new BindingRef { Kind = BindingKind.Literal, Literal = "ready" },
                        },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Status, Is.EqualTo(StepStatus.Passed));
    }

    [Test]
    public async Task WaitUntilCondition_GivesUpAtItsTimeout()
    {
        var browser = Browser("nope");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "hold", Action = StepAction.Wait,
                    Wait = new WaitSpec
                    {
                        Mode = WaitMode.UntilCondition,
                        PollMs = 50,
                        TimeoutMs = 120,
                        Condition = new ConditionSpec
                        {
                            Left = new BindingRef { Kind = BindingKind.Literal, Literal = "" },
                            Op = ConditionOp.NotEmpty,
                        },
                    },
                },
            ],
        };

        var events = await Run(task, browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(completed.Message, Does.Contain("still not met"));
    }

    [Test]
    public async Task WaitForASignal_SaysItNeedsTheScheduler()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "hold", Action = StepAction.Wait,
                    Wait = new WaitSpec { Mode = WaitMode.UntilSignal, SignalName = "go" },
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message, Does.Contain("scheduler"));
    }

    // ---- the ordinary path is untouched ---------------------------------------------------------

    [Test]
    public async Task APlainTaskRunsExactlyAsItDidBefore()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                Nav("a"),
                new Step
                {
                    Id = "grp", Action = StepAction.Group,
                    Children = [new Step { Id = "ext", Action = StepAction.ExtractText, Target = Target() }],
                },
            ],
        };

        var events = await Run(task, browser);

        Assert.Multiple(() =>
        {
            Assert.That(events.OfType<StepEvent.StepCompleted>().Select(e => e.StepId),
                Is.EqualTo(new[] { "a", "grp", "ext" }));
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        });
    }

    [Test]
    public async Task AFailedControlFlowStepStopsTheRunLikeAnyOtherFailure()
    {
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "call", Action = StepAction.RunTask, RunTaskId = "ghost" },
                Nav("after"),
            ],
        };

        var events = await Run(task, browser);

        Assert.Multiple(() =>
        {
            Assert.That(NavigatedUrls(browser), Is.Empty, "the step after a failure should not run");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
        });
    }

    // ---- parallel rows ---------------------------------------------------------------------------

    /// <summary>
    /// Parallel rows need BOTH gates open: the resolved Max concurrency ceiling (settings) and the
    /// loop's own request (the step). This raises the ceiling the way a scoped setting would.
    /// </summary>
    private static ReplayOptions OptionsWithCeiling(int lanes)
    {
        var settings = Automata.Core.Automation.Settings.EngineSettingsResolver.Floor()
            with { MaxConcurrency = lanes, DefaultStepTimeoutMs = 300 };
        return new ReplayOptions
        {
            DefaultStepTimeoutMs = 300,
            SettlePollMs = 1,
            Control = new ReplayControl(),
            ResolveForStep = _ => settings,
        };
    }

    private async Task<List<StepEvent>> RunWithLanes(
        TaskDefinition task, FakeBrowserSurface browser, BrowserLanePool pool, int ceiling = 4)
    {
        var events = new List<StepEvent>();
        await foreach (var evt in Engine().RunAsync(task, OptionsWithCeiling(ceiling), browser, lanes: pool))
            events.Add(evt);
        return events;
    }

    private static Step ParallelLoop(int lanes, params Step[] children) => new()
    {
        Id = "loop", Action = StepAction.ForEach, Label = "Each sku",
        ForEach = new ForEachSpec
        {
            Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
            MaxConcurrency = lanes,
        },
        Children = [.. children],
    };

    private void WriteSkus(params string[] skus) =>
        datasets.Write("skus.csv", skus.Select(s => new Dictionary<string, string> { ["sku"] = s }), append: false);

    [Test]
    public async Task ParallelForEach_RunsEveryRowAcrossSeveralLanes()
    {
        WriteSkus("a", "b", "c", "d");
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 3);

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                ParallelLoop(3, new Step
                {
                    Id = "open", Action = StepAction.Navigate,
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Url"] = new()
                        {
                            Kind = BindingKind.DatasetColumn, ColumnName = "sku",
                            Prefix = "https://shop.example/",
                        },
                    },
                }),
            ],
        };

        var events = await RunWithLanes(task, Browser(), pool);

        var visited = factory.Lanes
            .SelectMany(l => l.Fake.Calls.Where(c => c.Method == "Navigate").Select(c => c.Args))
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(visited, Is.EqualTo(new[]
            {
                "https://shop.example/a", "https://shop.example/b",
                "https://shop.example/c", "https://shop.example/d",
            }), "every row must run exactly once, whichever lane took it");
            Assert.That(factory.Requested, Has.Count.LessThanOrEqualTo(3), "never more browsers than the ceiling");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        });
    }

    [Test]
    public async Task ParallelForEach_RespectsTheTighterOfTheStepAndThePool()
    {
        WriteSkus("a", "b", "c", "d", "e", "f");
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        var task = new TaskDefinition { Name = "T", Steps = [ParallelLoop(6, Nav("inside"))] };

        await RunWithLanes(task, Browser(), pool);

        Assert.That(factory.Requested, Has.Count.LessThanOrEqualTo(2),
            "a step asking for six lanes cannot exceed a pool of two");
    }

    /// <summary>
    /// Rows run in their own scope whether there is one lane or many, so raising concurrency on a
    /// working loop cannot change what it produces.
    /// </summary>
    [Test]
    public async Task ParallelForEach_RowsDoNotSeeEachOthersCapturedValues()
    {
        WriteSkus("a", "b");
        var factory = new FakeBrowserSurfaceFactory
        {
            Responder = script =>
            {
                if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
                if (script.Contains("__automataResolve(")) return ResolveFoundCss;
                if (script.Contains("textContent")) return """{ "ok": true, "value": "captured" }""";
                return "{}";
            },
        };
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                ParallelLoop(2, new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "captured" }],
                }),
                // After the loop, nothing published inside it is in scope - which row's value
                // would it even be?
                new Step
                {
                    Id = "after", Action = StepAction.Navigate,
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Url"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "captured" },
                    },
                },
            ],
        };

        var events = await RunWithLanes(task, Browser(), pool);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message,
            Does.Contain("has not been produced yet"));
    }

    [Test]
    public async Task ParallelForEach_ReportsWhichLaneTookEachRow()
    {
        WriteSkus("a", "b");
        var factory = new FakeBrowserSurfaceFactory();
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);
        var task = new TaskDefinition { Name = "T", Steps = [ParallelLoop(2, Nav("inside"))] };

        var events = await RunWithLanes(task, Browser(), pool);

        var logs = events.OfType<StepEvent.Log>().Select(l => l.Message).ToList();
        Assert.That(logs.Count(l => l.Contains("started on lane")), Is.EqualTo(2),
            $"each row should say which lane took it: {string.Join(" | ", logs)}");
    }

    [Test]
    public async Task ParallelForEach_AFailingRowFailsTheRunWithoutLosingItsEvents()
    {
        WriteSkus("a", "b");
        var factory = new FakeBrowserSurfaceFactory
        {
            // Nothing resolves, so every row's step fails.
            Responder = script => script.Contains("__automataResolve(")
                ? """{ "found": false, "ambiguous": false, "candidateCount": 0 }"""
                : """{ "isProcessing": false }""",
        };
        await using var pool = new BrowserLanePool(factory, maxConcurrency: 2);

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                ParallelLoop(2, new Step
                {
                    Id = "click", Action = StepAction.Click, Label = "Click", Target = Target(), TimeoutMs = 5,
                }),
            ],
        };

        var events = await RunWithLanes(task, Browser(), pool);

        Assert.Multiple(() =>
        {
            Assert.That(events.OfType<StepEvent.StepCompleted>().Count(e => e.Status == StepStatus.Failed),
                Is.EqualTo(2), "both rows' failures should reach the caller");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
        });
    }

    [Test]
    public async Task WithoutAPool_ARowLoopStaysSequentialOnTheOneBrowser()
    {
        WriteSkus("a", "b");
        var browser = Browser();
        var task = new TaskDefinition { Name = "T", Steps = [ParallelLoop(4, Nav("inside"))] };

        var events = await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Has.Count.EqualTo(2), "both rows still run, just in order");
        Assert.That(events.OfType<StepEvent.Log>().Any(l => l.Message.Contains("single browser")), Is.True);
    }
}

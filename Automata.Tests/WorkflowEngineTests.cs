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

    // ---- where a called task starts ---------------------------------------------------------

    /// <summary>A caller and a callee, each with its own start URL, so the two are told apart by
    /// where the browser ends up.</summary>
    private TaskDefinition Callee(string startUrl)
    {
        var callee = new TaskDefinition
        {
            Name = "Callee",
            StartUrl = startUrl,
            Steps = [Nav("inside", "https://inside.example")],
        };
        collections.SaveTask(callee);
        return callee;
    }

    private static Step Call(string taskId, bool openStartUrl) => new()
    {
        Id = "call", Action = StepAction.RunTask, Label = "Call it",
        RunTaskId = taskId,
        RunTaskOpensStartUrl = openStartUrl,
    };

    /// <summary>The rule this app has always had, now written down where it can be checked: a
    /// called task carries on from wherever the caller left the browser.</summary>
    [Test]
    public async Task RunTask_LeavesTheCallerOnItsOwnPageByDefault()
    {
        var callee = Callee("https://callee.example");
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "Caller",
            Steps = [Nav("open", "https://caller.example"), Call(callee.Id, openStartUrl: false)],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://caller.example", "https://inside.example" }),
            "nothing should have opened the callee's start URL");
    }

    [Test]
    public async Task RunTask_OpensTheCalleesStartUrlWhenAskedTo()
    {
        var callee = Callee("https://callee.example");
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "Caller",
            Steps = [Nav("open", "https://caller.example"), Call(callee.Id, openStartUrl: true)],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[]
        {
            "https://caller.example", "https://callee.example", "https://inside.example",
        }));
    }

    /// <summary>Asking for a start page a task does not have is not an error — there is simply
    /// nothing to open, and failing would make the option unusable on a task that starts wherever
    /// it is put.</summary>
    [Test]
    public async Task RunTask_AskedToOpenAStartUrlThatIsNotThere_JustCarriesOn()
    {
        var callee = Callee("");
        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "Caller",
            Steps = [Nav("open", "https://caller.example"), Call(callee.Id, openStartUrl: true)],
        };

        var events = await Run(task, browser);

        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://caller.example", "https://inside.example" }));
    }

    // ---- what a loop knows that its columns do not --------------------------------------------

    /// <summary>Binds one field and reports what the step was actually given, which is the only
    /// way to see a binding's resolved value from outside the engine.</summary>
    private static Step NavBoundTo(string id, BindingRef binding, string? prefix = null)
    {
        binding.Prefix = prefix;
        return new Step
        {
            Id = id, Action = StepAction.Navigate, Label = id,
            Bindings = new Dictionary<string, BindingRef> { ["Url"] = binding },
        };
    }

    private static Step LoopOver(string dataset, string rowVariable, params Step[] children) => new()
    {
        Id = "loop-" + dataset, Action = StepAction.ForEach, Label = "Each row of " + dataset,
        ForEach = new ForEachSpec
        {
            Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = dataset },
            RowVariableName = rowVariable,
        },
        Children = [.. children],
    };

    private static BindingRef Column(string name) =>
        new() { Kind = BindingKind.DatasetColumn, ColumnName = name };

    private static BindingRef WholeRow(string? dataset = null) =>
        new() { Kind = BindingKind.DatasetRow, DatasetName = dataset };

    /// <summary>One-based, because the run log has always said "row 1 of 3" and two numbering
    /// schemes for the same thing is worse than either.</summary>
    [Test]
    public async Task ForEach_PublishesTheRowsPositionCountingFromOne()
    {
        datasets.Write("skus.csv",
            new[] { "a", "b", "c" }.Select(s => new Dictionary<string, string> { ["sku"] = s }), append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [LoopOver("skus.csv", "row",
                NavBoundTo("open", Column(ForEachSpec.RowNumberKey), "https://x.example/"))],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://x.example/1", "https://x.example/2", "https://x.example/3" }));
    }

    /// <summary>The position is the loop's bookkeeping; a column of the same name is the user's
    /// data, and data wins.</summary>
    [Test]
    public async Task ForEach_ARealColumnCalledHashBeatsThePosition()
    {
        datasets.Write("skus.csv",
            new[] { "first", "second" }.Select(v => new Dictionary<string, string> { ["#"] = v }), append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [LoopOver("skus.csv", "row",
                NavBoundTo("open", Column(ForEachSpec.RowNumberKey), "https://x.example/"))],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://x.example/first", "https://x.example/second" }));
    }

    [Test]
    public async Task ForEach_TheWholeRowBindsAsOneLineOfJson()
    {
        datasets.Write("skus.csv",
            [new Dictionary<string, string> { ["sku"] = "aaa", ["qty"] = "2" }], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [LoopOver("skus.csv", "row", NavBoundTo("open", WholeRow("skus.csv")))],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser).Single(), Is.EqualTo("""{"sku":"aaa","qty":"2"}"""));
    }

    /// <summary>The dataset name is what a whole-row binding uses to say WHICH loop it means, so
    /// an inner loop cannot shadow the row an outer one is on.</summary>
    [Test]
    public async Task NestedLoops_AWholeRowBindingReachesTheLoopItNames()
    {
        datasets.Write("outer.csv", [new Dictionary<string, string> { ["o"] = "1" }], append: false);
        datasets.Write("inner.csv", [new Dictionary<string, string> { ["i"] = "9" }], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                LoopOver("outer.csv", "outer",
                    LoopOver("inner.csv", "row",
                        NavBoundTo("named", WholeRow("outer.csv")),
                        NavBoundTo("innermost", WholeRow()))),
            ],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[]
        {
            """{"o":"1"}""",
            """{"i":"9"}""",
        }), "naming the dataset reaches that loop's row; naming none means the loop you are in");
    }

    /// <summary>
    /// Each loop counts its own rows, so an inner loop starts at 1 again for every outer row
    /// rather than continuing a running total — and the outer loop's own count is still reachable
    /// from inside it, by the row variable that names it.
    /// </summary>
    [Test]
    public async Task NestedLoops_TheInnerPositionRestartsForEachOuterRow()
    {
        datasets.Write("outer.csv",
            new[] { "a", "b" }.Select(v => new Dictionary<string, string> { ["o"] = v }), append: false);
        datasets.Write("inner.csv",
            new[] { "x", "y" }.Select(v => new Dictionary<string, string> { ["i"] = v }), append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                LoopOver("outer.csv", "outer",
                    LoopOver("inner.csv", "row",
                        NavBoundTo("here", Column(ForEachSpec.RowNumberKey), "https://inner.example/"),
                        NavBoundTo("out", Column("outer." + ForEachSpec.RowNumberKey), "https://outer.example/"))),
            ],
        };

        await Run(task, browser);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[]
        {
            "https://inner.example/1", "https://outer.example/1",
            "https://inner.example/2", "https://outer.example/1",
            "https://inner.example/1", "https://outer.example/2",
            "https://inner.example/2", "https://outer.example/2",
        }));
    }

    /// <summary>The same distinction a missing column gets: outside a loop the binding is in the
    /// wrong place, which is a different mistake from naming the wrong dataset.</summary>
    [Test]
    public async Task WholeRow_OutsideALoop_SaysThereIsNoRowHere()
    {
        var events = await Run(
            new TaskDefinition { Name = "T", Steps = [NavBoundTo("open", WholeRow())] }, Browser());

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message,
            Does.Contain("enclosing for-each"));
    }

    [Test]
    public async Task WholeRow_NamingADatasetNoLoopIsOver_SaysWhichOne()
    {
        datasets.Write("skus.csv", [new Dictionary<string, string> { ["sku"] = "a" }], append: false);

        var events = await Run(
            new TaskDefinition
            {
                Name = "T",
                Steps = [LoopOver("skus.csv", "row", NavBoundTo("open", WholeRow("other.csv")))],
            },
            Browser());

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message, Does.Contain("other.csv"));
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

    /// <summary>
    /// The step tree lets ANY step hold children — drag one onto another, or Alt+Right — so a
    /// control-flow case that did not walk its own children ran the step and silently dropped
    /// everything nested under it. Only if/otherwise/forEach decide for themselves whether their
    /// children run; the rest behave like every ordinary step.
    /// </summary>
    [TestCaseSource(nameof(StepsThatMustStillRunTheirChildren))]
    public async Task AControlFlowStepRunsItsOwnChildren(Step parent)
    {
        datasets.Write("stock.csv", [new Dictionary<string, string> { ["qty"] = "7" }], append: false);
        var browser = Browser();
        parent.Children = [Nav("after", "https://child.example")];

        var events = await Run(new TaskDefinition { Name = "T", Steps = [parent] }, browser);

        Assert.That(NavigatedUrls(browser), Does.Contain("https://child.example"),
            $"a step nested under a {parent.Action} step must still run");
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    private static IEnumerable<Step> StepsThatMustStillRunTheirChildren()
    {
        yield return new Step
        {
            Id = "save", Action = StepAction.WriteDataset, Label = "save",
            WriteDataset = new DatasetWriteSpec
            {
                DatasetName = "bought.csv",
                Columns = new Dictionary<string, BindingRef>
                {
                    ["sku"] = new() { Kind = BindingKind.Literal, Literal = "WT-100" },
                },
            },
        };
        yield return new Step
        {
            Id = "total", Action = StepAction.Aggregate, Label = "total",
            Aggregate = new AggregateSpec { DatasetName = "stock.csv", ColumnName = "qty", Op = AggregateOp.Sum },
        };
        yield return new Step
        {
            Id = "hold", Action = StepAction.Wait, Label = "hold",
            Wait = new WaitSpec
            {
                Mode = WaitMode.UntilCondition,
                PollMs = 10,
                TimeoutMs = 500,
                Condition = new ConditionSpec
                {
                    Left = new BindingRef { Kind = BindingKind.Literal, Literal = "yes" },
                    Op = ConditionOp.NotEmpty,
                },
            },
        };
    }

    [Test]
    public async Task RunTask_RunsTheStepsNestedUnderItAfterTheCalleeFinishes()
    {
        var callee = new TaskDefinition
        {
            Name = "Callee", CollectionId = collections.EnsureDefaultCollection().Id,
            Steps = [Nav("inner", "https://callee.example")],
        };
        collections.SaveTask(callee);

        var browser = Browser();
        var caller = new TaskDefinition
        {
            Name = "Caller",
            Steps =
            [
                new Step
                {
                    Id = "call", Action = StepAction.RunTask, Label = "call", RunTaskId = callee.Id,
                    Children = [Nav("after", "https://after.example")],
                },
            ],
        };

        await Run(caller, browser);

        Assert.That(NavigatedUrls(browser),
            Is.EqualTo(new[] { "https://callee.example", "https://after.example" }));
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

    /// <summary>
    /// The footgun this exists for: a collecting loop that appends is not repeatable, so running a
    /// task twice leaves it holding both runs' rows and reporting nothing wrong.
    /// </summary>
    [Test]
    public async Task WriteDataset_WithoutResetOnFirstWrite_DoublesItsRowsOnASecondRun()
    {
        datasets.Write("rows.csv", [Row("a"), Row("b")], append: false);
        var task = CollectingLoop(reset: false);

        await Run(task, Browser());

        Assert.That(datasets.Read("collected.csv"), Has.Count.EqualTo(2));

        await Run(task, Browser());

        Assert.That(datasets.Read("collected.csv"), Has.Count.EqualTo(4),
            "an appending loop keeps what the last run left — this is the behaviour reset exists to fix");
    }

    /// <summary>
    /// With it, the first write of each run replaces and the rest append, so the dataset holds one
    /// run's results however many times the task is run.
    /// </summary>
    [Test]
    public async Task WriteDataset_WithResetOnFirstWrite_HoldsOneRunsResultsHoweverOftenItRuns()
    {
        datasets.Write("rows.csv", [Row("a"), Row("b"), Row("c")], append: false);
        var task = CollectingLoop(reset: true);

        await Run(task, Browser());
        await Run(task, Browser());
        var rows = datasets.Read("collected.csv");

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3), "every row of the loop is still there");
            Assert.That(rows.Select(r => r["key"]).OrderBy(k => k), Is.EqualTo(new[] { "a", "b", "c" }),
                "and they are this run's rows, not a mix of two runs");
        });
    }

    /// <summary>
    /// The reset is claimed once per RUN, not once per step or once per row — a second write step
    /// aimed at the same dataset must add to what the loop collected, not wipe it.
    /// </summary>
    [Test]
    public async Task WriteDataset_ResetIsClaimedOncePerRunNotOncePerWrite()
    {
        datasets.Write("rows.csv", [Row("a"), Row("b")], append: false);
        var task = CollectingLoop(reset: true);
        task.Steps.Add(new Step
        {
            Id = "footer", Action = StepAction.WriteDataset,
            WriteDataset = new DatasetWriteSpec
            {
                DatasetName = "collected.csv",
                Append = true,
                ResetOnFirstWrite = true,
                Columns = new Dictionary<string, BindingRef>
                {
                    ["key"] = new() { Kind = BindingKind.Literal, Literal = "total" },
                },
            },
        });

        await Run(task, Browser());

        Assert.That(datasets.Read("collected.csv"), Has.Count.EqualTo(3),
            "the second write step cleared what the loop had collected");
    }

    /// <summary>One row of a source dataset for the loop to fan out over.</summary>
    private static Dictionary<string, string> Row(string key) => new(StringComparer.Ordinal) { ["key"] = key };

    /// <summary>A for-each over rows.csv that writes one row of collected.csv per source row.</summary>
    private static TaskDefinition CollectingLoop(bool reset) => new()
    {
        Name = "T",
        Steps =
        [
            new Step
            {
                Id = "loop", Action = StepAction.ForEach,
                ForEach = new ForEachSpec
                {
                    Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "rows.csv" },
                    RowVariableName = "row",
                },
                Children =
                [
                    new Step
                    {
                        Id = "save", Action = StepAction.WriteDataset,
                        WriteDataset = new DatasetWriteSpec
                        {
                            DatasetName = "collected.csv",
                            Append = true,
                            ResetOnFirstWrite = reset,
                            Columns = new Dictionary<string, BindingRef>
                            {
                                ["key"] = new() { Kind = BindingKind.DatasetColumn, ColumnName = "key" },
                            },
                        },
                    },
                ],
            },
        ],
    };

    // ---- aggregate -------------------------------------------------------------------------------

    /// <summary>The prices a run might have collected, written the way a page gives them up.</summary>
    private void SeedAmounts(params string[] amounts) =>
        datasets.Write("collected.csv",
            amounts.Select(a => new Dictionary<string, string>(StringComparer.Ordinal) { ["price"] = a }),
            append: false);

    private static TaskDefinition Reducing(AggregateOp op, string column = "price") => new()
    {
        Name = "T",
        Steps =
        [
            new Step
            {
                Id = "agg", Action = StepAction.Aggregate, Label = "Work it out",
                Aggregate = new AggregateSpec { DatasetName = "collected.csv", ColumnName = column, Op = op },
            },
        ],
    };

    private static string? Answer(List<StepEvent> events) =>
        events.OfType<StepEvent.StepCompleted>().Single().ExtractedText;

    /// <summary>
    /// Money read off a page is "$24.50", never 24.5 — an aggregate that only worked on bare
    /// numbers would only work on datasets nobody has.
    /// </summary>
    [TestCase(AggregateOp.Sum, "60")]
    [TestCase(AggregateOp.Count, "3")]
    [TestCase(AggregateOp.Min, "12.5")]
    [TestCase(AggregateOp.Max, "27.5")]
    [TestCase(AggregateOp.Average, "20")]
    public async Task Aggregate_ReducesAColumnOfMoneyToOneNumber(AggregateOp op, string expected)
    {
        SeedAmounts("$12.50", "$20.00", "$27.50");

        var events = await Run(Reducing(op), Browser());

        Assert.That(Answer(events), Is.EqualTo(expected));
    }

    /// <summary>The answer is published for a later step to bind to — that is the whole point of
    /// having it in the product rather than in a script that reads the CSV afterwards.</summary>
    [Test]
    public async Task Aggregate_PublishesItsAnswerForALaterStepToBindTo()
    {
        SeedAmounts("$12.50", "$27.50");
        var task = Reducing(AggregateOp.Sum);
        task.Steps.Add(new Step
        {
            Id = "save", Action = StepAction.WriteDataset,
            WriteDataset = new DatasetWriteSpec
            {
                DatasetName = "totals.csv",
                Append = false,
                Columns = new Dictionary<string, BindingRef>
                {
                    ["total"] = new()
                    {
                        Kind = BindingKind.StepOutput, SourceStepId = "agg", OutputField = "value",
                    },
                },
            },
        });

        await Run(task, Browser());

        Assert.That(datasets.Read("totals.csv").Single()["total"], Is.EqualTo("40"));
    }

    /// <summary>
    /// A cell that is not a number fails the step rather than being skipped. An average that
    /// quietly ignored half its rows returns a plausible number nobody can tell is short.
    /// </summary>
    [Test]
    public async Task Aggregate_OverSomethingThatIsNotANumber_FailsRatherThanSkippingIt()
    {
        SeedAmounts("$12.50", "on request");

        var events = await Run(Reducing(AggregateOp.Sum), Browser());

        var done = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain("on request"), "the message should name the cell");
    }

    /// <summary>Blank is absence, not zero — a row where nothing was collected must not drag an
    /// average down.</summary>
    [Test]
    public async Task Aggregate_SkipsBlankCells()
    {
        SeedAmounts("$10.00", "", "$20.00");

        var events = await Run(Reducing(AggregateOp.Average), Browser());

        Assert.That(Answer(events), Is.EqualTo("15"));
    }

    /// <summary>
    /// A column that is not there at all is a typo, not an empty result. Answering 0 would be
    /// answering a question nobody asked, and it would look like a working step.
    /// </summary>
    [Test]
    public async Task Aggregate_OverAColumnThatIsNotThere_SaysWhichColumnsAre()
    {
        SeedAmounts("$10.00");

        var events = await Run(Reducing(AggregateOp.Count, column: "cost"), Browser());

        var done = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain("price"), "the message should list the columns there are");
    }

    /// <summary>Counting nothing is nothing; averaging nothing is a question with no answer, and
    /// returning 0 for it would be a lie the run cannot be talked out of.</summary>
    [Test]
    public async Task Aggregate_OverAnEmptyColumn_CountsZeroButRefusesToAverage()
    {
        datasets.Write("collected.csv",
            [new Dictionary<string, string>(StringComparer.Ordinal) { ["price"] = "" }], append: false);

        var counted = await Run(Reducing(AggregateOp.Count), Browser());
        var averaged = await Run(Reducing(AggregateOp.Average), Browser());

        Assert.Multiple(() =>
        {
            Assert.That(Answer(counted), Is.EqualTo("0"));
            Assert.That(averaged.OfType<StepEvent.StepCompleted>().Single().Status,
                Is.EqualTo(StepStatus.Failed));
        });
    }

    [Test]
    public async Task Aggregate_OverADatasetThatIsNotThere_SaysWhereItLooked()
    {
        var events = await Run(Reducing(AggregateOp.Sum), Browser());

        var done = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain(datasets.RootPath));
    }

    /// <summary>
    /// The case adjacency cannot see. Two guards in a row, the second with an `otherwise`; delete
    /// the second and the `otherwise` is now behind the FIRST one. Nothing about the tree looks
    /// wrong, the run passes, and the wrong branch fires — which is the one failure mode this
    /// project treats as unacceptable. Recording which `if` the branch was written for makes it loud.
    /// </summary>
    [Test]
    public async Task Otherwise_ThatNowFollowsADifferentIf_FailsInsteadOfTakingItsVerdict()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "kept", Action = StepAction.If,
                    Condition = new ConditionSpec { Left = Literal("yes"), Op = ConditionOp.NotEmpty },
                },
                // Written for an `if` that is no longer in the list — the one it was paired with was
                // deleted, leaving it behind "kept".
                new Step
                {
                    Id = "otherwise", Action = StepAction.Else, PairedIfId = "deleted",
                    Children = [Record("ran", "ran")],
                },
            ],
        };

        var events = await Run(task, Browser());

        var done = events.OfType<StepEvent.StepCompleted>().Last();
        Assert.Multiple(() =>
        {
            Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
            Assert.That(done.Message, Does.Contain("different 'if'"));
            Assert.That(datasets.Exists("took.csv"), Is.False, "and the wrong branch must not run");
        });
    }

    /// <summary>A branch that IS where it says it belongs runs exactly as before.</summary>
    [Test]
    public async Task Otherwise_ThatStillFollowsItsOwnIf_RunsNormally()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "guard", Action = StepAction.If,
                    Condition = new ConditionSpec { Left = Literal(""), Op = ConditionOp.NotEmpty },
                },
                new Step
                {
                    Id = "otherwise", Action = StepAction.Else, PairedIfId = "guard",
                    Children = [Record("ran", "else")],
                },
            ],
        };

        await Run(task, Browser());

        Assert.That(datasets.Read("took.csv").Single()["branch"], Is.EqualTo("else"));
    }

    /// <summary>
    /// A task written before the id existed carries none, and must keep working on adjacency alone —
    /// otherwise this fix would break every branch already on disk.
    /// </summary>
    [Test]
    public async Task Otherwise_WithNoRecordedPairing_StillWorksOnAdjacency()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "guard", Action = StepAction.If,
                    Condition = new ConditionSpec { Left = Literal(""), Op = ConditionOp.NotEmpty },
                },
                new Step { Id = "otherwise", Action = StepAction.Else, Children = [Record("ran", "else")] },
            ],
        };

        await Run(task, Browser());

        Assert.That(datasets.Read("took.csv").Single()["branch"], Is.EqualTo("else"));
    }

    // ---- task inputs -----------------------------------------------------------------------------

    /// <summary>A task that writes whatever it was given into a dataset, so the value is checkable.</summary>
    private static TaskDefinition Parameterised(string name, string? defaultValue, string dataset) => new()
    {
        Id = name, Name = name,
        Inputs = [new TaskInput { Name = "term", Default = defaultValue }],
        Steps =
        [
            new Step
            {
                Id = name + "-save", Action = StepAction.WriteDataset,
                WriteDataset = new DatasetWriteSpec
                {
                    DatasetName = dataset,
                    Append = true,
                    Columns = new Dictionary<string, BindingRef>
                    {
                        ["term"] = new() { Kind = BindingKind.TaskInput, ParameterName = "term" },
                    },
                },
            },
        ],
    };

    private async Task<List<StepEvent>> RunWithInputs(
        TaskDefinition task, params (string Name, string Value)[] inputs)
    {
        var options = new ReplayOptions
        {
            DefaultStepTimeoutMs = 300,
            SettlePollMs = 1,
            Control = new ReplayControl(),
            Inputs = inputs.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase),
        };
        var events = new List<StepEvent>();
        await foreach (var evt in Engine().RunAsync(task, options, Browser()))
            events.Add(evt);
        return events;
    }

    [Test]
    public async Task TaskInput_FallsBackToItsDeclaredDefault()
    {
        await RunWithInputs(Parameterised("t", "wolf", "seen.csv"));

        Assert.That(datasets.Read("seen.csv").Single()["term"], Is.EqualTo("wolf"));
    }

    [Test]
    public async Task TaskInput_SuppliedByTheCaller_WinsOverTheDefault()
    {
        await RunWithInputs(Parameterised("t", "wolf", "seen.csv"), ("term", "badger"));

        Assert.That(datasets.Read("seen.csv").Single()["term"], Is.EqualTo("badger"));
    }

    /// <summary>
    /// An input with no default and nothing supplied fails BY NAME, at the step that needed it.
    /// Resolving to an empty string would type nothing into a search box and report success —
    /// which is the failure mode declaring inputs exists to prevent.
    /// </summary>
    [Test]
    public async Task TaskInput_RequiredAndNotSupplied_FailsNamingIt()
    {
        var events = await RunWithInputs(Parameterised("t", null, "seen.csv"));

        var done = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain("term"));
        Assert.That(datasets.Exists("seen.csv"), Is.False);
    }

    /// <summary>One task handing another a value is the whole point — and the callee's input must
    /// not leak back out, or the same binding would mean different things after the call.</summary>
    [Test]
    public async Task RunTask_HandsTheCalledTaskItsInputs_WithoutLeakingThemBack()
    {
        var callee = Parameterised("callee", "wolf", "seen.csv");
        collections.SaveTask(callee);

        var caller = Parameterised("caller", "otter", "seen.csv");
        caller.Steps.Insert(0, new Step
        {
            Id = "call", Action = StepAction.RunTask, RunTaskId = callee.Id,
            RunTaskInputs = new Dictionary<string, BindingRef>
            {
                ["term"] = new() { Kind = BindingKind.Literal, Literal = "badger" },
            },
        });

        await Run(caller, Browser());

        Assert.That(datasets.Read("seen.csv").Select(r => r["term"]),
            Is.EqualTo(new[] { "badger", "otter" }),
            "the callee should see what it was handed, and the caller its own value afterwards");
    }

    /// <summary>Anything the caller does not name falls back to the callee's own default, rather
    /// than to whatever the caller happens to have under that name.</summary>
    [Test]
    public async Task RunTask_WithoutNamingAnInput_LetsTheCalleeUseItsOwnDefault()
    {
        var callee = Parameterised("callee", "wolf", "seen.csv");
        collections.SaveTask(callee);

        var caller = Parameterised("caller", "otter", "seen.csv");
        caller.Steps.Insert(0, new Step { Id = "call", Action = StepAction.RunTask, RunTaskId = callee.Id });

        await Run(caller, Browser());

        Assert.That(datasets.Read("seen.csv").Select(r => r["term"]), Is.EqualTo(new[] { "wolf", "otter" }));
    }

    /// <summary>
    /// A loop is a place inside a task, not a task of its own. A binding to an input resolved fine
    /// the line before the loop and failed inside it, because the forked row scope started with no
    /// inputs at all — so the step reported "nothing supplied it" about a value that was supplied.
    /// </summary>
    [Test]
    public async Task TaskInput_IsStillReadableInsideAForEach()
    {
        datasets.Write("skus.csv", [
            new Dictionary<string, string> { ["sku"] = "aaa" },
            new Dictionary<string, string> { ["sku"] = "bbb" },
        ], append: false);

        var task = new TaskDefinition
        {
            Name = "T",
            Inputs = [new TaskInput { Name = "term", Default = "wolf" }],
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
                            Id = "save", Action = StepAction.WriteDataset,
                            WriteDataset = new DatasetWriteSpec
                            {
                                DatasetName = "seen.csv",
                                Append = true,
                                Columns = new Dictionary<string, BindingRef>
                                {
                                    ["sku"] = new() { Kind = BindingKind.DatasetColumn, ColumnName = "sku" },
                                    ["term"] = new() { Kind = BindingKind.TaskInput, ParameterName = "term" },
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var events = await Run(task, Browser());

        Assert.Multiple(() =>
        {
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
            Assert.That(datasets.Read("seen.csv").Select(r => r["term"]),
                Is.EqualTo(new[] { "wolf", "wolf" }), "every row sees the input the run started with");
        });
    }

    /// <summary>
    /// A called task's inputs are its own, and a loop inside it must see THOSE rather than the
    /// caller's — the row scope copies the whole stack, not merely the outermost frame.
    /// </summary>
    [Test]
    public async Task AForEachInsideACalledTask_SeesTheCalleesOwnInputs()
    {
        datasets.Write("one.csv", [new Dictionary<string, string> { ["n"] = "1" }], append: false);

        var callee = new TaskDefinition
        {
            Id = "callee", Name = "Callee",
            Inputs = [new TaskInput { Name = "term", Default = "fallback" }],
            Steps =
            [
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach,
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "one.csv" },
                    },
                    Children =
                    [
                        new Step
                        {
                            Id = "save", Action = StepAction.WriteDataset,
                            WriteDataset = new DatasetWriteSpec
                            {
                                DatasetName = "seen.csv",
                                Append = true,
                                Columns = new Dictionary<string, BindingRef>
                                {
                                    ["term"] = new() { Kind = BindingKind.TaskInput, ParameterName = "term" },
                                },
                            },
                        },
                    ],
                },
            ],
        };
        collections.SaveTask(callee);

        var caller = new TaskDefinition
        {
            Id = "caller", Name = "Caller",
            Steps =
            [
                new Step
                {
                    Id = "call", Action = StepAction.RunTask, RunTaskId = "callee",
                    RunTaskInputs = new Dictionary<string, BindingRef>
                    {
                        ["term"] = new() { Kind = BindingKind.Literal, Literal = "handed-over" },
                    },
                },
            ],
        };

        await Run(caller, Browser());

        Assert.That(datasets.Read("seen.csv").Single()["term"], Is.EqualTo("handed-over"));
    }

    // ---- zoom is a property of the run, not of one page --------------------------------------

    /// <summary>A zoom set before a loop still applies inside it. Navigating resets a page to 100%
    /// and the engine re-applies the run's zoom afterwards — but a row that started life at 100%
    /// had nothing to re-apply, so the first navigate inside a loop silently undid it.</summary>
    [Test]
    public async Task Zoom_SetBeforeALoop_IsReappliedAfterANavigateInsideIt()
    {
        datasets.Write("skus.csv", [new Dictionary<string, string> { ["sku"] = "aaa" }], append: false);

        var browser = Browser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "zoom", Action = StepAction.SetZoom, Label = "Zoom out", ZoomPercent = 50 },
                new Step
                {
                    Id = "loop", Action = StepAction.ForEach,
                    ForEach = new ForEachSpec
                    {
                        Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
                    },
                    Children = [Nav("inside")],
                },
            ],
        };

        await Run(task, browser);

        var zooms = browser.Calls.Where(c => c.Method == "SetZoom").Select(c => c.Args).ToList();
        Assert.That(zooms, Is.EqualTo(new[] { "0.5", "0.5" }),
            "once for the step, once to restore it after the navigate inside the loop");
    }

    /// <summary>And a zoom set INSIDE a loop outlives it, because rows deliberately leave the page
    /// where the next thing starts from.</summary>
    [Test]
    public async Task Zoom_SetInsideALoop_IsStillInForceAfterIt()
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
                    Children = [new Step { Id = "zoom", Action = StepAction.SetZoom, ZoomPercent = 50 }],
                },
                Nav("after"),
            ],
        };

        await Run(task, browser);

        var zooms = browser.Calls.Where(c => c.Method == "SetZoom").Select(c => c.Args).ToList();
        Assert.That(zooms, Is.EqualTo(new[] { "0.5", "0.5" }),
            "the navigate after the loop restores what the loop set, rather than leaving it at 100%");
    }

    // ---- a step whose spec never got filled in ------------------------------------------------

    /// <summary>Every other control-flow step says what is missing; this one dereferenced a null
    /// spec and took the whole run down with a NullReferenceException instead.</summary>
    [Test]
    public async Task ExtractAll_WithNoSpec_FailsTheStepRatherThanCrashingTheRun()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "harvest", Action = StepAction.ExtractAll, Label = "Harvest" }],
        };

        var events = await Run(task, Browser());

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
            Assert.That(completed.Message, Does.Contain("dataset"));
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
        });
    }

    // ---- ragged data, presence, and otherwise -------------------------------------------------------

    /// <summary>
    /// A list where not every row carries every field — the normal shape of a JSON blob that came
    /// out of somewhere else, and the one a spreadsheet cannot produce.
    /// </summary>
    private void SeedRagged() =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(datasets.RootPath).FullName, "roster.json"),
            """[ { "Name": "Ada" }, { "Role": "unknown" }, { "Name": "Grace" } ]""");

    /// <summary>A loop that records which branch each row took, so both halves are checkable.</summary>
    private static TaskDefinition Branching(ConditionOp op, bool withElse) => new()
    {
        Name = "T",
        Steps =
        [
            new Step
            {
                Id = "loop", Action = StepAction.ForEach,
                ForEach = new ForEachSpec
                {
                    Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "roster.json" },
                    RowVariableName = "row",
                },
                Children =
                [
                    new Step
                    {
                        Id = "guard", Action = StepAction.If,
                        Condition = new ConditionSpec
                        {
                            Left = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "Name" },
                            Op = op,
                        },
                        Children = [Record("then", "then")],
                    },
                    .. withElse
                        ? new[] { new Step { Id = "otherwise", Action = StepAction.Else, Children = [Record("else", "else")] } }
                        : [],
                ],
            },
        ],
    };

    private static Step Record(string id, string which) => new()
    {
        Id = id, Action = StepAction.WriteDataset,
        WriteDataset = new DatasetWriteSpec
        {
            DatasetName = "took.csv",
            Append = true,
            Columns = new Dictionary<string, BindingRef>
            {
                ["branch"] = new() { Kind = BindingKind.Literal, Literal = which },
            },
        },
    };

    /// <summary>
    /// The reason <c>Exists</c> had to exist. Asking whether an ABSENT value is empty fails the
    /// run — deliberately, because a column that is not there is nearly always a mis-typed column
    /// name — so a ragged list cannot be branched on with <c>NotEmpty</c> at all.
    /// </summary>
    [Test]
    public async Task NotEmpty_OnARowThatLacksTheColumn_FailsRatherThanAnswering()
    {
        SeedRagged();

        var events = await Run(Branching(ConditionOp.NotEmpty, withElse: false), Browser());

        var failure = events.OfType<StepEvent.StepCompleted>().Last(e => e.Status == StepStatus.Failed);
        Assert.That(failure.Message, Does.Contain("this row has no 'Name'"));
        Assert.That(failure.Message, Does.Contain("exists"), "and it should say what to do instead");
    }

    [Test]
    public async Task Exists_AnswersInsteadOfFailing_OnTheRowThatLacksTheColumn()
    {
        SeedRagged();

        await Run(Branching(ConditionOp.Exists, withElse: false), Browser());

        Assert.That(datasets.Read("took.csv"), Has.Count.EqualTo(2), "the two rows that have a Name");
    }

    [Test]
    public async Task Otherwise_RunsExactlyWhenTheIfBeforeItDidNot()
    {
        SeedRagged();

        await Run(Branching(ConditionOp.Exists, withElse: true), Browser());

        Assert.That(datasets.Read("took.csv").Select(r => r["branch"]),
            Is.EqualTo(new[] { "then", "else", "then" }),
            "one branch per row, and the one the row's data called for");
    }

    /// <summary>
    /// An `if` runs its children BEFORE anything looks at what it decided, so a nested one is the
    /// last to have decided anything. Reading "the last verdict" would pair the outer else with
    /// the inner if — silently, and only when a task happens to nest.
    /// </summary>
    [Test]
    public async Task Otherwise_PairsWithItsOwnIf_NotWithANestedOne()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "outer", Action = StepAction.If,
                    Condition = new ConditionSpec { Left = Literal("yes"), Op = ConditionOp.NotEmpty },
                    Children =
                    [
                        new Step
                        {
                            Id = "inner", Action = StepAction.If,
                            Condition = new ConditionSpec { Left = Literal(""), Op = ConditionOp.NotEmpty },
                            Children = [Record("never", "inner-then")],
                        },
                    ],
                },
                new Step { Id = "otherwise", Action = StepAction.Else, Children = [Record("outer-else", "outer-else")] },
            ],
        };

        await Run(task, Browser());

        Assert.That(datasets.Exists("took.csv"), Is.False,
            "the outer if held, so its otherwise must not run — whatever the inner one decided");
    }

    [Test]
    public async Task Otherwise_WithNoIfBeforeIt_SaysSo()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "otherwise", Action = StepAction.Else, Children = [Record("x", "x")] }],
        };

        var events = await Run(task, Browser());

        var done = events.OfType<StepEvent.StepCompleted>().First();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain("straight after an 'if'"));
    }

    /// <summary>Outside a loop the message is about the binding being in the wrong place, not about
    /// the row — they are two different mistakes with two different fixes.</summary>
    [Test]
    public async Task AColumnBindingOutsideALoop_StillSaysItNeedsALoop()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "guard", Action = StepAction.If,
                    Condition = new ConditionSpec
                    {
                        Left = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "Name" },
                        Op = ConditionOp.NotEmpty,
                    },
                },
            ],
        };

        var events = await Run(task, Browser());

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message,
            Does.Contain("needs an enclosing for-each"));
    }

    private static BindingRef Literal(string value) =>
        new() { Kind = BindingKind.Literal, Literal = value };

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

    // ---- rows, one at a time ---------------------------------------------------------------

    private static ReplayOptions RowOptions()
    {
        var settings = Automata.Core.Automation.Settings.EngineSettingsResolver.Floor()
            with { DefaultStepTimeoutMs = 300 };
        return new ReplayOptions
        {
            DefaultStepTimeoutMs = 300,
            SettlePollMs = 1,
            Control = new ReplayControl(),
            ResolveForStep = _ => settings,
        };
    }

    private async Task<List<StepEvent>> RunRows(TaskDefinition task, FakeBrowserSurface browser)
    {
        var events = new List<StepEvent>();
        await foreach (var evt in Engine().RunAsync(task, RowOptions(), browser))
            events.Add(evt);
        return events;
    }

    private static Step RowLoop(params Step[] children) => new()
    {
        Id = "loop", Action = StepAction.ForEach, Label = "Each sku",
        ForEach = new ForEachSpec
        {
            Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "skus.csv" },
        },
        Children = [.. children],
    };

    private void WriteSkus(params string[] skus) =>
        datasets.Write("skus.csv", skus.Select(s => new Dictionary<string, string> { ["sku"] = s }), append: false);

    /// <summary>
    /// Every row runs, on the one browser, in the order the dataset gives them. The order is the
    /// assertion that matters: a row can leave the page somewhere the next row starts from, which
    /// is only true while nothing else is touching that browser.
    /// </summary>
    [Test]
    public async Task Rows_RunInOrderOnTheOneBrowser()
    {
        WriteSkus("a", "b", "c", "d");
        var browser = Browser();

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                RowLoop(new Step
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

        var events = await RunRows(task, browser);

        Assert.Multiple(() =>
        {
            Assert.That(NavigatedUrls(browser), Is.EqualTo(new[]
            {
                "https://shop.example/a", "https://shop.example/b",
                "https://shop.example/c", "https://shop.example/d",
            }), "every row runs exactly once, in the dataset's order");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        });
    }

    /// <summary>
    /// Every row keeps the position it started with. The index is read before the fork rather than
    /// counted as rows finish, so a row that fails does not renumber the ones after it.
    /// </summary>
    [Test]
    public async Task Rows_EachKeepsItsOwnPosition()
    {
        WriteSkus("a", "b", "c", "d");
        var browser = Browser();

        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [RowLoop(NavBoundTo("open", Column(ForEachSpec.RowNumberKey), "https://x.example/"))],
        };

        await RunRows(task, browser);

        Assert.That(NavigatedUrls(browser), Is.EqualTo(new[]
        {
            "https://x.example/1", "https://x.example/2",
            "https://x.example/3", "https://x.example/4",
        }));
    }

    /// <summary>
    /// A row's captured values belong to that row. After the loop nothing it published is in
    /// scope — which row's value would it even be?
    /// </summary>
    [Test]
    public async Task Rows_DoNotSeeEachOthersCapturedValues()
    {
        WriteSkus("a", "b");
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
            {
                if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
                if (script.Contains("__automataResolve(")) return ResolveFoundCss;
                if (script.Contains("textContent")) return """{ "ok": true, "value": "captured" }""";
                return "{}";
            },
        };

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                RowLoop(new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "captured" }],
                }),
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

        var events = await RunRows(task, browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message,
            Does.Contain("has not been produced yet"));
    }

    /// <summary>
    /// A row that fails stops the loop, because a failed step stops its task and rows share that
    /// task. This is the sequential trade being made deliberately: rows are not independent
    /// attempts, they are one job walking a list — so the first row that cannot do its work stops
    /// the walk rather than leaving a half-done list nobody looks at.
    /// </summary>
    [Test]
    public async Task Rows_AFailingRowStopsTheLoopAndFailsTheRun()
    {
        WriteSkus("a", "b");
        var browser = new FakeBrowserSurface
        {
            // Nothing resolves, so every row's step fails.
            DefaultEvalResponse = script => script.Contains("__automataResolve(")
                ? """{ "found": false, "ambiguous": false, "candidateCount": 0 }"""
                : """{ "isProcessing": false }""",
        };

        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                RowLoop(new Step
                {
                    Id = "click", Action = StepAction.Click, Label = "Click", Target = Target(), TimeoutMs = 5,
                }),
            ],
        };

        var events = await RunRows(task, browser);

        Assert.Multiple(() =>
        {
            Assert.That(events.OfType<StepEvent.StepCompleted>().Count(e => e.Status == StepStatus.Failed),
                Is.EqualTo(1), "the failure reaches the caller");
            Assert.That(events.OfType<StepEvent.Log>().Count(l => l.Message.Contains("row 2 of 2")), Is.Zero,
                "and the walk stops there rather than carrying on into the next row");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
        });
    }
}

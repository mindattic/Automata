using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ReplayEngineTests
{
    private static ReplayEngine Engine() => new(new FingerprintResolver { PollIntervalMs = 10 });

    private static ReplayOptions Options(ReplayMode mode = ReplayMode.Run, ReplayControl? control = null) => new()
    {
        Mode = mode,
        DefaultStepTimeoutMs = 300,
        SettlePollMs = 1,
        Control = control ?? new ReplayControl(),
    };

    private static ElementFingerprint Target() => new() { Tag = "input", CssSelector = "#field" };

    private const string ResolveFoundCss = """
        { "found": true, "unique": true, "strategy": "css", "ambiguous": false, "candidateCount": 1,
          "centerX": 10, "centerY": 20, "tag": "input", "text": "x" }
        """;

    /// <summary>Script-shape dispatcher covering the engine's standard evals; overridable per test.</summary>
    private static string DefaultResponder(string script)
    {
        if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
        if (script.Contains("__automataResolve(")) return ResolveFoundCss;
        if (script.Contains("__automataApplyValue")) return """{ "ok": true, "value": "cats" }""";
        if (script.Contains("textContent")) return """{ "ok": true, "value": "hello world" }""";
        if (script.Contains("document.activeElement")) return """{ "value": "cats" }""";
        return "{}";
    }

    private static async Task<List<StepEvent>> RunToEnd(
        ReplayEngine engine, TaskDefinition task, ReplayOptions options, FakeBrowserSurface browser)
    {
        var events = new List<StepEvent>();
        await foreach (var evt in engine.RunAsync(task, options, browser))
            events.Add(evt);
        return events;
    }

    [Test]
    public async Task HappyPath_EmitsOrderedEvents_IncludingSubstepRecursion()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "nav", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
                new Step
                {
                    Id = "grp", Action = StepAction.Group, Label = "Verify",
                    Children = [new Step { Id = "ext", Action = StepAction.ExtractText, Label = "Read", Target = Target() }],
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var kinds = events.Select(e => e switch
        {
            StepEvent.RunStarted => "run",
            StepEvent.StepStarted s => $"start:{s.StepId}",
            StepEvent.StepCompleted c => $"done:{c.StepId}:{c.Status}",
            StepEvent.RunCompleted r => $"end:{r.Success}",
            _ => e.GetType().Name,
        }).ToList();
        Assert.That(kinds, Is.EqualTo(new[]
        {
            "run",
            "start:nav", "done:nav:Passed",
            "start:grp", "done:grp:Passed",
            "start:ext", "done:ext:Passed",
            "end:True",
        }));
        Assert.That(events.OfType<StepEvent.StepCompleted>().Single(c => c.StepId == "ext").ExtractedText,
            Is.EqualTo("hello world"));
        Assert.That(browser.Calls.Any(c => c.Method == "Navigate" && c.Args == "https://x.example"), Is.True);
    }

    [Test]
    public async Task DryRun_StopsBeforeCommitPoint_WithoutExecutingIt()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "s1", Action = StepAction.SetValue, Label = "Fill", Value = "cats", Target = Target() },
                new Step { Id = "s2", Action = StepAction.Click, Label = "Submit", IsCommitPoint = true, Target = Target() },
                new Step { Id = "s3", Action = StepAction.SetValue, Label = "After", Value = "cats", Target = Target() },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(ReplayMode.DryRun), browser);

        var commit = events.OfType<StepEvent.StepCompleted>().Single(c => c.StepId == "s2");
        Assert.That(commit.Status, Is.EqualTo(StepStatus.Skipped));
        Assert.That(events.OfType<StepEvent.StepStarted>().Select(s => s.StepId), Does.Not.Contain("s2"));
        Assert.That(events.OfType<StepEvent.StepStarted>().Select(s => s.StepId), Does.Not.Contain("s3"));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False); // commit never clicked
    }

    [Test]
    public async Task PauseForUser_ParksUntilContinue()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var control = new ReplayControl();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "p1", Action = StepAction.ExtractText, Label = "Paused", PauseForUser = true, Target = Target() }],
        };

        var events = new List<StepEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var evt in Engine().RunAsync(task, Options(control: control), browser))
                lock (events) events.Add(evt);
        });

        // Wait until the engine reports the pause…
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (events) { if (events.Any(e => e is StepEvent.StepPaused)) break; }
            await Task.Delay(10);
        }
        lock (events) Assert.That(events.Any(e => e is StepEvent.StepPaused), Is.True);

        // …verify it's actually parked…
        await Task.Delay(100);
        Assert.That(run.IsCompleted, Is.False);

        // …then release it.
        control.Continue();
        var finished = await Task.WhenAny(run, Task.Delay(5000));
        Assert.That(finished, Is.EqualTo(run));
        lock (events)
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task Validate_ResolvesAndNavigates_ButNeverMutates()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "n", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
                new Step { Id = "c", Action = StepAction.Click, Label = "Click", Target = Target() },
                new Step { Id = "t", Action = StepAction.TypeText, Label = "Type", Value = "cats", Target = Target() },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(ReplayMode.Validate), browser);

        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        Assert.That(events.OfType<StepEvent.StepCompleted>().Count(c => c.Status == StepStatus.Passed), Is.EqualTo(3));
        Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False);
        Assert.That(browser.Calls.Any(c => c.Method == "TypeText"), Is.False);
        Assert.That(browser.Calls.Any(c => c.Method == "Navigate"), Is.True);   // multi-page validate
        Assert.That(browser.Calls.Any(c => c.Method == "Eval" && c.Args.Contains("__automataResolve(")), Is.True);
    }

    [Test]
    public async Task PostConditionMismatch_FailsStep_AndAbortsRun()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
                script.Contains("__automataApplyValue") ? """{ "ok": true, "value": "dogs" }""" : DefaultResponder(script),
        };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "s1", Action = StepAction.SetValue, Label = "Fill", Value = "cats", Target = Target() },
                new Step { Id = "s2", Action = StepAction.ExtractText, Label = "Never", Target = Target() },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var failed = events.OfType<StepEvent.StepCompleted>().Single(c => c.StepId == "s1");
        Assert.That(failed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(failed.Message, Does.Contain("dogs"));
        Assert.That(events.OfType<StepEvent.StepStarted>().Select(s => s.StepId), Does.Not.Contain("s2"));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
    }

    [Test]
    public async Task ResolveFailure_ReportsAmbiguity()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
                script.Contains("__automataResolve(")
                    ? """{ "found": false, "ambiguous": true, "candidateCount": 4 }"""
                    : DefaultResponder(script),
        };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "s1", Action = StepAction.Click, Label = "Click", Target = Target() }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var failed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(failed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(failed.Message, Does.Contain("ambiguous"));
    }

    /// <summary>Scripted provider: one tool call (log_note), then a final text turn.</summary>
    private sealed class FakeToolCallingLlm : Core.Operator.IToolCallingLlm
    {
        private int turn;
        public string Name => "Fake";
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);

        public Task<Core.Operator.ToolTurnResult> CreateTurnAsync(
            string systemPrompt,
            IReadOnlyList<Core.Operator.ToolLoopMessage> history,
            IReadOnlyList<Core.Operator.ToolDefinition> tools,
            int maxTokens,
            CancellationToken ct)
        {
            turn++;
            return Task.FromResult(new Core.Operator.ToolTurnResult(turn == 1
                ? [new Core.Operator.AssistantPart.ToolCall("t1", "log_note", """{ "message": "repaired" }""")]
                : [new Core.Operator.AssistantPart.Text("done")]));
        }
    }

    [Test]
    public async Task UnresolvableStep_WithLlmRepairAllowed_PassesViaRepair()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
                script.Contains("__automataResolve(")
                    ? """{ "found": false, "ambiguous": false, "candidateCount": 0 }"""
                    : DefaultResponder(script),
        };
        var repair = new Core.Operator.BrowserOperatorService(
            [new FakeToolCallingLlm()],
            new Core.Operator.BrowserToolRegistry([new Core.Operator.Tools.LogNoteTool()]),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Operator.BrowserOperatorService>.Instance);
        var engine = new ReplayEngine(new FingerprintResolver { PollIntervalMs = 10 }, repair);
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "s1", Action = StepAction.Click, Label = "Click 'Search'", Target = Target() }],
        };
        var options = new ReplayOptions
        {
            Mode = ReplayMode.Run,
            AllowLlmRepair = true,
            DefaultStepTimeoutMs = 50,
            SettlePollMs = 1,
            Control = new ReplayControl(),
        };

        var events = new List<StepEvent>();
        await foreach (var evt in engine.RunAsync(task, options, browser)) events.Add(evt);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Passed));
        Assert.That(completed.Message, Does.Contain("LLM repair"));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task SelfHeal_WritesRefreshedFingerprintBackIntoStep_AndReportsHealed()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
                script.Contains("__automataResolve(")
                    ? """
                      { "found": true, "unique": true, "strategy": "xpath", "ambiguous": false,
                        "candidateCount": 1, "centerX": 10, "centerY": 20, "tag": "h3", "text": "x",
                        "refreshedFingerprint": { "tag": "h3", "cssSelector": "#healed", "classList": [] } }
                      """
                    : DefaultResponder(script),
        };
        var step = new Step { Id = "s1", Action = StepAction.ExtractText, Label = "Read", Target = Target() };
        var task = new TaskDefinition { Name = "T", Steps = [step] };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Healed));
        Assert.That(step.Target!.CssSelector, Is.EqualTo("#healed"));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Summary, Does.Contain("self-healed"));
    }
}

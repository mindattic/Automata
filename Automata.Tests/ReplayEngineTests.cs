using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Settings;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ReplayEngineTests
{
    private static ReplayEngine Engine() => new(new FingerprintResolver { PollIntervalMs = 10 });

    private static ReplayOptions Options(ReplayControl? control = null, string? pauseBeforeStepId = null) => new()
    {
        DefaultStepTimeoutMs = 300,
        SettlePollMs = 1,
        Control = control ?? new ReplayControl(),
        PauseBeforeStepId = pauseBeforeStepId,
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

    // ---- zoom ------------------------------------------------------------------------------

    private static FakeBrowserSurface ZoomBrowser() =>
        new() { DefaultEvalResponse = DefaultResponder };

    /// <summary>The factors the engine actually asked the browser for, in order.</summary>
    private static List<string> ZoomsAskedFor(FakeBrowserSurface browser) =>
        browser.Calls.Where(c => c.Method == "SetZoom").Select(c => c.Args).ToList();

    [Test]
    public async Task SetZoom_AppliesTheLevelAndSaysSo()
    {
        var browser = ZoomBrowser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "z", Action = StepAction.SetZoom, Label = "Zoom out", ZoomPercent = 60 }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.Multiple(() =>
        {
            Assert.That(ZoomsAskedFor(browser), Is.EqualTo(new[] { "0.6" }),
                "the browser takes a factor, not a percentage");
            var done = events.OfType<StepEvent.StepCompleted>().Single();
            Assert.That(done.Status, Is.EqualTo(StepStatus.Passed));
            Assert.That(done.Message, Does.Contain("60%"));
        });
    }

    /// <summary>
    /// A zoom that did not take has to fail here. Passing it would leave the click after it aiming
    /// at coordinates from a layout the page is not in, and the failure would surface as an
    /// unrelated step missing its element several steps later.
    /// </summary>
    [Test]
    public async Task SetZoom_ThatThePageDidNotHonour_Fails()
    {
        var browser = ZoomBrowser();
        browser.ZoomResponse = _ => 1.0;   // a page that stayed exactly where it was
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "z", Action = StepAction.SetZoom, Label = "Zoom out", ZoomPercent = 60 }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var done = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(done.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(done.Message, Does.Contain("100%"), "the message should say what it measured");
    }

    /// <summary>An emulated viewport is a whole number of pixels, so a measured factor is almost
    /// never exactly the one asked for.</summary>
    [Test]
    public async Task SetZoom_ToleratesTheRoundingOfAWholePixelViewport()
    {
        var browser = ZoomBrowser();
        browser.ZoomResponse = _ => 0.3300330033;
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "z", Action = StepAction.SetZoom, Label = "Zoom", ZoomPercent = 33 }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Passed));
    }

    /// <summary>
    /// A zoom outside what a browser itself offers is far more likely a typo — 6 for 60 — than an
    /// intention, and a page at 6% cannot be automated at all. Refusing beats obeying.
    /// </summary>
    [Test]
    public async Task SetZoom_OutsideWhatABrowserOffers_FailsRatherThanObeying()
    {
        var browser = ZoomBrowser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "z", Action = StepAction.SetZoom, Label = "Zoom", ZoomPercent = 6 }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.Multiple(() =>
        {
            Assert.That(ZoomsAskedFor(browser), Is.Empty, "nothing should reach the browser");
            Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Failed));
        });
    }

    /// <summary>
    /// The zoom belongs to the run, not to the document. A navigation loads a fresh page at 100%,
    /// so the engine has to put it back — otherwise a task that zoomed out to reach a wide layout
    /// silently loses it at the first link, and the click after that lands on nothing.
    /// </summary>
    [Test]
    public async Task Navigating_ReappliesTheZoomTheRunAskedFor()
    {
        var browser = ZoomBrowser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "z", Action = StepAction.SetZoom, Label = "Zoom out", ZoomPercent = 50 },
                new Step { Id = "nav", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.Multiple(() =>
        {
            Assert.That(ZoomsAskedFor(browser), Is.EqualTo(new[] { "0.5", "0.5" }),
                "the new page should be zoomed too");
            Assert.That(events.OfType<StepEvent.StepCompleted>().Last().Message, Does.Contain("50%"),
                "and the run should say so, rather than leaving it to be discovered");
        });
    }

    /// <summary>A run that never zoomed must not pay for the feature on every navigation.</summary>
    [Test]
    public async Task Navigating_WithoutAZoomStep_TouchesTheZoomAtAll()
    {
        var browser = ZoomBrowser();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "nav", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" }],
        };

        await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(ZoomsAskedFor(browser), Is.Empty);
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
    public async Task PressEnter_WithoutTarget_SendsEnterToFocusedElement()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "e1", Action = StepAction.PressEnter, Label = "Press Enter" }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(browser.Calls.Any(c => c.Method == "PressEnter"), Is.True);
        Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False); // nothing to focus
        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Passed));
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task PressEnter_WithTarget_FocusesItFirst()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "e1", Action = StepAction.PressEnter, Label = "Press Enter", Target = Target() }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var clickIndex = browser.Calls.FindIndex(c => c.Method == "ClickAtPoint");
        var enterIndex = browser.Calls.FindIndex(c => c.Method == "PressEnter");
        Assert.That(clickIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(enterIndex, Is.GreaterThan(clickIndex)); // focus click precedes Enter
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task Continue_FiredBeforeWaitAsync_IsLatchedNotLost()
    {
        var control = new ReplayControl();

        control.Continue();   // UI wins the race: Continue lands before the engine parks

        var wait = control.WaitAsync(CancellationToken.None);
        var finished = await Task.WhenAny(wait, Task.Delay(2000));
        Assert.That(finished, Is.EqualTo(wait), "a pre-fired Continue must release the next WaitAsync");
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
    public async Task PauseBeforeStepId_ParksBeforeThatStep_WithoutPersistedFlag()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var control = new ReplayControl();
        var target = new Step { Id = "gap-target", Action = StepAction.ExtractText, Label = "Target", Target = Target() };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "before", Action = StepAction.ExtractText, Label = "Before", Target = Target() },
                target,
            ],
        };

        var events = new List<StepEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var evt in Engine().RunAsync(task, Options(control, pauseBeforeStepId: "gap-target"), browser))
                lock (events) events.Add(evt);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (events) { if (events.Any(e => e is StepEvent.StepPaused)) break; }
            await Task.Delay(10);
        }
        lock (events)
        {
            var paused = events.OfType<StepEvent.StepPaused>().Single();
            Assert.That(paused.StepId, Is.EqualTo("gap-target"));
            // "before" must have actually run before the pause — the pause fires right in front
            // of the gap's target step, not at the start of the whole run.
            Assert.That(events.OfType<StepEvent.StepCompleted>().Select(e => e.StepId), Does.Contain("before"));
        }
        Assert.That(target.PauseForUser, Is.False, "the transient gate must never mutate the persisted flag");

        control.Continue();
        var finished = await Task.WhenAny(run, Task.Delay(5000));
        Assert.That(finished, Is.EqualTo(run));
        lock (events)
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
    }

    [Test]
    public async Task PauseBeforeStepId_NeverFires_WhenAnEarlierStepFailsFirst()
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
                new Step { Id = "gap-target", Action = StepAction.ExtractText, Label = "Target", Target = Target() },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(pauseBeforeStepId: "gap-target"), browser);

        Assert.That(events.Any(e => e is StepEvent.StepPaused), Is.False);
        Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
    }

    [Test]
    public async Task PauseForUser_AndPauseBeforeStepId_OnSameStep_FiresExactlyOnePause()
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
            await foreach (var evt in Engine().RunAsync(task, Options(control, pauseBeforeStepId: "p1"), browser))
                lock (events) events.Add(evt);
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (events) { if (events.Any(e => e is StepEvent.StepPaused)) break; }
            await Task.Delay(10);
        }
        control.Continue();
        var finished = await Task.WhenAny(run, Task.Delay(5000));
        Assert.That(finished, Is.EqualTo(run));
        lock (events)
            Assert.That(events.OfType<StepEvent.StepPaused>().Count(), Is.EqualTo(1));
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

    // ---- scoped settings: retry and continue-on-error -------------------------------------------

    private const string ResolveNotFound = """
        { "found": false, "unique": false, "strategy": null, "ambiguous": false, "candidateCount": 0 }
        """;

    /// <summary>
    /// Floor settings with a 5ms step budget. The resolver breaks out of its poll loop as soon as
    /// elapsed + PollIntervalMs exceeds the budget, so one failed attempt costs exactly ONE
    /// __automataResolve eval — which is what lets these tests count attempts.
    /// </summary>
    private static ResolvedSettings FastFloor() =>
        EngineSettingsResolver.Floor() with { DefaultStepTimeoutMs = 5 };

    private static ReplayOptions OptionsWith(ResolvedSettings settings) => new()
    {
        DefaultStepTimeoutMs = 300,
        SettlePollMs = 1,
        Control = new ReplayControl(),
        ResolveForStep = _ => settings,
    };

    private static FakeBrowserSurface AlwaysUnresolvable(Action? onResolve = null) => new()
    {
        DefaultEvalResponse = script =>
        {
            if (!script.Contains("__automataResolve(")) return DefaultResponder(script);
            onResolve?.Invoke();
            return ResolveNotFound;
        },
    };

    private static TaskDefinition OneClick(string id = "s1") => new()
    {
        Name = "T",
        Steps = [new Step { Id = id, Action = StepAction.Click, Label = "Click", Target = Target() }],
    };

    [Test]
    public async Task FailingStep_WithRetry_SucceedsOnTheSecondAttempt()
    {
        var resolveCalls = 0;
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = script =>
            {
                if (!script.Contains("__automataResolve(")) return DefaultResponder(script);
                return ++resolveCalls == 1 ? ResolveNotFound : ResolveFoundCss;
            },
        };
        var settings = FastFloor() with { Retry = new RetryPolicy { MaxAttempts = 2, DelayMs = 1 } };

        var events = await RunToEnd(Engine(), OneClick(), OptionsWith(settings), browser);

        Assert.Multiple(() =>
        {
            Assert.That(resolveCalls, Is.EqualTo(2));
            Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Passed));
            Assert.That(events.OfType<StepEvent.Log>().Any(l => l.Message.Contains("retrying")), Is.True,
                "the retry should be visible in the run log, not silent");
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
        });
    }

    [Test]
    public async Task FailingStep_ExhaustsTheRetryBudgetThenFailsTheRun()
    {
        var resolveCalls = 0;
        var browser = AlwaysUnresolvable(() => resolveCalls++);
        var settings = FastFloor() with { Retry = new RetryPolicy { MaxAttempts = 3, DelayMs = 1 } };

        var events = await RunToEnd(Engine(), OneClick(), OptionsWith(settings), browser);

        Assert.Multiple(() =>
        {
            Assert.That(resolveCalls, Is.EqualTo(3), "MaxAttempts counts the first try, not extra tries");
            Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Failed));
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False);
        });
    }

    /// <summary>Regression guard on the floor: without scoped settings, nothing retries.</summary>
    [Test]
    public async Task WithNoScopedSettings_AFailingStepIsAttemptedExactlyOnce()
    {
        var resolveCalls = 0;
        var browser = AlwaysUnresolvable(() => resolveCalls++);
        var task = OneClick();
        task.Steps[0].TimeoutMs = 5;

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(resolveCalls, Is.EqualTo(1));
        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Status, Is.EqualTo(StepStatus.Failed));
    }

    [Test]
    public async Task FailingStep_WithContinueOnStepError_StillRunsTheNextSibling()
    {
        var browser = AlwaysUnresolvable();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Id = "bad", Action = StepAction.Click, Label = "Bad", Target = Target() },
                new Step { Id = "good", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
            ],
        };

        var events = await RunToEnd(
            Engine(), task, OptionsWith(FastFloor() with { ContinueOnStepError = true }), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(completed.Select(c => c.StepId), Is.EqualTo(new[] { "bad", "good" }));
            Assert.That(completed[0].Status, Is.EqualTo(StepStatus.Failed));
            Assert.That(completed[1].Status, Is.EqualTo(StepStatus.Passed));
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.False,
                "carrying on past a failure must not report the run as successful");
        });
    }

    /// <summary>
    /// Continue-on-error is about SIBLINGS. A failed step's own children still never run — its
    /// post-condition did not hold, so its substeps have nothing to stand on.
    /// </summary>
    [Test]
    public async Task FailingStep_WithContinueOnStepError_StillSkipsItsOwnChildren()
    {
        var browser = AlwaysUnresolvable();
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "bad", Action = StepAction.Click, Label = "Bad", Target = Target(),
                    Children = [new Step { Id = "child", Action = StepAction.Navigate, Url = "https://x.example" }],
                },
                new Step { Id = "sibling", Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
            ],
        };

        var events = await RunToEnd(
            Engine(), task, OptionsWith(FastFloor() with { ContinueOnStepError = true }), browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Select(c => c.StepId),
            Is.EqualTo(new[] { "bad", "sibling" }));
    }

    [Test]
    public async Task PerStepTimeout_StillBeatsTheResolvedScopeChain()
    {
        var browser = AlwaysUnresolvable();
        var task = OneClick();
        task.Steps[0].TimeoutMs = 5;

        var started = Environment.TickCount64;
        await RunToEnd(Engine(), task, OptionsWith(EngineSettingsResolver.Floor()), browser);

        Assert.That(Environment.TickCount64 - started, Is.LessThan(5_000),
            "a 5ms per-step timeout must win over the resolved 10s default");
    }

    // ---- v3 phase 4: waits, bindings, outputs, masking ------------------------------------------

    /// <summary>A fixed-offset zone, so "today or tomorrow" never depends on the test machine.</summary>
    private static TimeZoneInfo PlusTwo() =>
        TimeZoneInfo.CreateCustomTimeZone("Automata/Test+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    /// <summary>
    /// A zone that springs forward at 02:00 on 1 March, so the DST gap is deterministic instead of
    /// depending on whatever the machine's timezone database happens to contain.
    /// </summary>
    private static TimeZoneInfo GapZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Automata/TestGap", TimeSpan.Zero, "Gap", "Gap Standard", "Gap Daylight", [rule]);
    }

    [Test]
    public void MillisecondsUntil_UsesTodayWhenTheTimeIsStillAhead()
    {
        var now = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);   // 02:00 local

        var ms = WaitPlan.MillisecondsUntil(new TimeOnly(9, 0), PlusTwo(), now);

        Assert.That(ms, Is.EqualTo(TimeSpan.FromHours(7).TotalMilliseconds));
    }

    [Test]
    public void MillisecondsUntil_RollsToTomorrowWhenTheTimeHasPassed()
    {
        var now = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);   // 02:00 local

        var ms = WaitPlan.MillisecondsUntil(new TimeOnly(1, 0), PlusTwo(), now);

        Assert.That(ms, Is.EqualTo(TimeSpan.FromHours(23).TotalMilliseconds));
    }

    [Test]
    public void MillisecondsUntil_SpringForwardGapTakesTheFirstValidInstant()
    {
        // 02:30 does not exist on 1 March in this zone; the wait lands at 03:00 local (02:00 UTC).
        var now = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var ms = WaitPlan.MillisecondsUntil(new TimeOnly(2, 30), GapZone(), now);

        Assert.That(ms, Is.EqualTo(TimeSpan.FromHours(2).TotalMilliseconds));
    }

    [Test]
    public async Task WaitStep_ForADurationPasses()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "w", Action = StepAction.Wait, Label = "Settle",
                    Wait = new WaitSpec { Mode = WaitMode.Duration, DurationMs = 5 },
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Passed));
        Assert.That(completed.Message, Does.Contain("waited"));
    }

    [Test]
    public async Task WaitStep_WithAnUnknownTimeZone_FailsWithTheZoneNamed()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "w", Action = StepAction.Wait,
                    Wait = new WaitSpec
                    {
                        Mode = WaitMode.UntilTimeOfDay,
                        TimeOfDay = new TimeOnly(9, 0),
                        TimeZoneId = "Mars/Olympus_Mons",
                    },
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message,
            Does.Contain("Mars/Olympus_Mons"));
    }

    [Test]
    public async Task WaitStep_UntilAConditionSaysItNeedsTheWorkflowEngine()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps = [new Step { Id = "w", Action = StepAction.Wait, Wait = new WaitSpec { Mode = WaitMode.UntilCondition } }],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(completed.Message, Does.Contain("workflow engine"));
    }

    /// <summary>
    /// The orchestrated actions are modelled so the editor and the authoring layer can produce
    /// them, but a single-task replay has no dataset access or lane pool. Saying so beats the
    /// switch default's misleading "unsupported action".
    /// </summary>
    [TestCase(StepAction.ForEach)]
    [TestCase(StepAction.If)]
    [TestCase(StepAction.Else)]
    [TestCase(StepAction.RunTask)]
    [TestCase(StepAction.WriteDataset)]
    [TestCase(StepAction.Aggregate)]
    [TestCase(StepAction.ExtractAll)]
    public async Task OrchestratedActions_AreRejectedWithAClearReason(StepAction action)
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = DefaultResponder };
        var task = new TaskDefinition { Name = "T", Steps = [new Step { Id = "s", Action = action }] };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(completed.Message, Does.Contain("workflow engine"));
    }

    /// <summary>A responder whose reads all return the same text, so an extracted value can be
    /// followed through a binding into a later step's typed value.</summary>
    private static FakeBrowserSurface EchoingBrowser(string text) => new()
    {
        DefaultEvalResponse = script =>
        {
            if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
            if (script.Contains("__automataResolve(")) return ResolveFoundCss;
            if (script.Contains("textContent")) return $$"""{ "ok": true, "value": "{{text}}" }""";
            if (script.Contains("document.activeElement")) return $$"""{ "value": "{{text}}" }""";
            return "{}";
        },
    };

    [Test]
    public async Task ExtractedOutput_FeedsALaterStepThroughABinding()
    {
        var browser = EchoingBrowser("hello world");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Label = "Read total", Target = Target(),
                    Outputs = [new OutputField { Name = "total" }],
                },
                new Step
                {
                    Id = "write", Action = StepAction.TypeText, Label = "Type it", Target = Target(),
                    Value = "ignored literal",
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "total" },
                    },
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.Multiple(() =>
        {
            Assert.That(events.OfType<StepEvent.RunCompleted>().Single().Success, Is.True);
            Assert.That(browser.Calls.Any(c => c.Method == "TypeText" && c.Args == "hello world"), Is.True,
                "the bound value should have been typed, not the literal beside it");
            Assert.That(browser.Calls.Any(c => c.Args == "ignored literal"), Is.False);
        });
    }

    [Test]
    public async Task Binding_WrapsTheValueInItsPrefixAndSuffix()
    {
        var browser = EchoingBrowser("sku1");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(),
                    Outputs = [new OutputField { Name = "sku" }],
                },
                new Step
                {
                    Id = "go", Action = StepAction.Navigate, Label = "Open it",
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Url"] = new()
                        {
                            Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "sku",
                            Prefix = "https://shop.example/item/", Suffix = "?ref=automata",
                        },
                    },
                },
            ],
        };

        await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(browser.Calls.Any(c => c.Method == "Navigate"
            && c.Args == "https://shop.example/item/sku1?ref=automata"), Is.True);
    }

    [Test]
    public async Task Binding_ToAnOutputThatHasNotBeenProducedYet_FailsWithAReadableReason()
    {
        var browser = EchoingBrowser("x");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "write", Action = StepAction.TypeText, Target = Target(),
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "later", OutputField = "total" },
                    },
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.That(completed.Status, Is.EqualTo(StepStatus.Failed));
        Assert.That(completed.Message, Does.Contain("has not been produced yet"));
    }

    [Test]
    public async Task Binding_ToAnEnvironmentVariableResolves()
    {
        var name = "AUTOMATA_TEST_" + Guid.NewGuid().ToString("n")[..8];
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var browser = EchoingBrowser("from-env");
            var task = new TaskDefinition
            {
                Name = "T",
                Steps =
                [
                    new Step
                    {
                        Id = "write", Action = StepAction.TypeText, Target = Target(),
                        Bindings = new Dictionary<string, BindingRef>
                        {
                            ["Value"] = new() { Kind = BindingKind.EnvVar, EnvVarName = name },
                        },
                    },
                ],
            };

            await RunToEnd(Engine(), task, Options(), browser);

            Assert.That(browser.Calls.Any(c => c.Method == "TypeText" && c.Args == "from-env"), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Test]
    public async Task ADatasetBindingWithNoRowInScope_SaysSoRatherThanFallingBackToTheLiteral()
    {
        var browser = EchoingBrowser("x");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "write", Action = StepAction.TypeText, Target = Target(), Value = "literal",
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.DatasetColumn, DatasetName = "skus.csv", ColumnName = "sku" },
                    },
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(events.OfType<StepEvent.StepCompleted>().Single().Message,
            Does.Contain("enclosing for-each"));
        Assert.That(browser.Calls.Any(c => c.Args == "literal"), Is.False,
            "an unresolvable binding must not silently fall back to the literal beside it");
    }

    /// <summary>
    /// A masked step withholds its value entirely rather than scrubbing the message: a partial
    /// scrub that misses one interpolation is worse than a generic message.
    /// </summary>
    [Test]
    public async Task MaskedStep_WithholdsItsExtractedTextFromTheEventStream()
    {
        var browser = EchoingBrowser("hunter2");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(), Masked = true,
                    Outputs = [new OutputField { Name = "secret" }],
                },
            ],
        };

        var events = await RunToEnd(Engine(), task, Options(), browser);

        var completed = events.OfType<StepEvent.StepCompleted>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(StepStatus.Passed));
            Assert.That(completed.ExtractedText, Is.Not.EqualTo("hunter2"));
            Assert.That(completed.Message, Is.Null);
            Assert.That(events.Any(e => e.ToString()!.Contains("hunter2")), Is.False,
                "the secret must not appear anywhere in the event stream");
        });
    }

    /// <summary>Masking hides a value from watchers, not from the run itself - a later step can
    /// still bind to it.</summary>
    [Test]
    public async Task MaskedStep_StillPublishesItsOutputForBinding()
    {
        var browser = EchoingBrowser("hunter2");
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Id = "read", Action = StepAction.ExtractText, Target = Target(), Masked = true,
                    Outputs = [new OutputField { Name = "secret" }],
                },
                new Step
                {
                    Id = "use", Action = StepAction.TypeText, Target = Target(),
                    Bindings = new Dictionary<string, BindingRef>
                    {
                        ["Value"] = new() { Kind = BindingKind.StepOutput, SourceStepId = "read", OutputField = "secret" },
                    },
                },
            ],
        };

        await RunToEnd(Engine(), task, Options(), browser);

        Assert.That(browser.Calls.Any(c => c.Method == "TypeText" && c.Args == "hunter2"), Is.True);
    }
}

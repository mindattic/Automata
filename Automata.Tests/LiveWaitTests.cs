using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// A wait on a condition, watching the page.
/// <para>
/// Until this, <see cref="WaitMode.UntilCondition"/> re-evaluated the SAME captured values on every
/// poll, and nothing wrote to those values while the loop was running — one browser, one thing in
/// flight. So it was an assertion with a timeout wearing a wait's name: it could hold immediately
/// or time out, and no third outcome existed. These tests are about the third outcome.
/// </para>
/// </summary>
[TestFixture]
public class LiveWaitTests
{
    /// <summary>A resolve that succeeded, in the shape the envelope parser expects.</summary>
    private const string Found =
        """
        { "found": true, "unique": true, "strategy": "css", "candidateCount": 1,
          "centerX": 10, "centerY": 20, "tag": "div" }
        """;

    private string root = null!;
    private CollectionStore collections = null!;
    private FakeBrowserSurface browser = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-livewait-" + Guid.NewGuid().ToString("n")[..8]);
        collections = new CollectionStore(root);
        browser = new FakeBrowserSurface();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* a held file; the temp sweep gets it */ }
    }

    private WorkflowEngine Engine() => new(
        new ReplayEngine(new FingerprintResolver { PollIntervalMs = 5 }),
        collections,
        new DatasetStore(Path.Combine(root, "datasets")));

    /// <summary>A wait that watches <c>#status</c> until it says <paramref name="want"/>.</summary>
    private static Step Watching(string want, int timeoutMs = 2000) => new()
    {
        Id = "watch",
        Action = StepAction.Wait,
        Label = "Wait for the status",
        Target = new ElementFingerprint { Tag = "div", CssSelector = "#status" },
        Outputs = [new OutputField { Name = "value" }],
        Wait = new WaitSpec
        {
            Mode = WaitMode.UntilCondition,
            PollMs = 10,
            TimeoutMs = timeoutMs,
            Condition = new ConditionSpec
            {
                Left = new BindingRef
                {
                    Kind = BindingKind.StepOutput, SourceStepId = "watch", OutputField = "value",
                },
                Op = ConditionOp.Equals,
                Right = new BindingRef { Kind = BindingKind.Literal, Literal = want },
            },
        },
    };

    /// <summary>
    /// A little fake page whose element says something different on each poll — which is exactly
    /// what a page changing under a wait looks like, and the thing no captured value can imitate.
    /// <para>
    /// A null entry means the element is not on the page at that moment, so the resolve misses and
    /// no read happens at all. The last entry is what the page settles on, so a shorter script than
    /// the number of polls means "and then it stayed like that".
    /// </para>
    /// </summary>
    private void PageReadsInTurn(params string?[] readings)
    {
        var poll = 0;
        string? Now() => readings[Math.Min(poll, readings.Length - 1)];

        browser.DefaultEvalResponse = script =>
        {
            // The resolve script is the one carrying the cascade. It also marks the start of a
            // poll, which is what advances the page.
            if (script.Contains("__automataResolve"))
            {
                var present = Now() != null;
                poll++;
                return present ? Found : """{ "found": false, "ambiguous": false, "candidateCount": 0 }""";
            }
            // The settle-wait's page-busy probe, for whatever ordinary step follows the wait.
            if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
            // Anything else is the read, and it answers for the poll that just started.
            var value = readings[Math.Min(poll - 1, readings.Length - 1)];
            return value == null
                ? """{ "ok": false, "error": "no element resolved" }"""
                : $$"""{ "ok": true, "value": "{{value}}" }""";
        };
    }

    private async Task<List<StepEvent>> RunAsync(params Step[] steps)
    {
        var task = new TaskDefinition { Id = "t", Name = "T", Steps = [.. steps] };
        var events = new List<StepEvent>();
        await foreach (var e in Engine().RunAsync(task, new ReplayOptions(), browser))
            events.Add(e);
        return events;
    }

    private static string? MessageFor(List<StepEvent> events, string stepId) => events
        .OfType<StepEvent.StepCompleted>().FirstOrDefault(e => e.StepId == stepId)?.Message;

    private static StepStatus? StatusFor(List<StepEvent> events, string stepId) => events
        .OfType<StepEvent.StepCompleted>().FirstOrDefault(e => e.StepId == stepId)?.Status;

    // ---- the third outcome -----------------------------------------------------------------------

    /// <summary>
    /// The whole claim: the page says one thing, then another, and the wait carries on when the
    /// SECOND one arrives. Nothing but a re-read can produce that — the first reading is the only
    /// value a captured-output wait would ever see.
    /// </summary>
    [Test]
    public async Task AWaitOnATargetCarriesOnWhenThePageChangesUnderIt()
    {
        PageReadsInTurn("working", "working", "ready");

        var events = await RunAsync(Watching("ready"));

        Assert.Multiple(() =>
        {
            Assert.That(StatusFor(events, "watch"), Is.EqualTo(StepStatus.Passed));
            Assert.That(MessageFor(events, "watch"), Does.Contain("read 'ready'"));
        });
    }

    /// <summary>
    /// An element that is not there yet is precisely what a wait is for, so a condition that cannot
    /// be evaluated for want of a reading must be "not yet", not a failure. Getting this wrong is
    /// worse than the bug being fixed: the wait would fail on its FIRST poll, before the page had
    /// any chance to render.
    /// </summary>
    [Test]
    public async Task AnElementThatHasNotAppearedYetIsNotYetRatherThanAFailure()
    {
        PageReadsInTurn(null, null, "ready");

        var events = await RunAsync(Watching("ready"));

        Assert.That(StatusFor(events, "watch"), Is.EqualTo(StepStatus.Passed));
    }

    /// <summary>
    /// The other half of that rule, and the reason it is stated as narrowly as it is: a condition
    /// that cannot be evaluated for a reason the page will never fix — comparing something that is
    /// not a number — is a mistake in the task, and waiting the full timeout before saying so just
    /// delays the news.
    /// </summary>
    [Test]
    public async Task AConditionThatCanNeverHoldFailsAtOnceRatherThanAtTheTimeout()
    {
        PageReadsInTurn("not a number");
        var step = Watching("ignored", timeoutMs: 30_000);
        step.Wait!.Condition!.Op = ConditionOp.GreaterThan;

        var started = Environment.TickCount64;
        var events = await RunAsync(step);
        var elapsed = Environment.TickCount64 - started;

        Assert.Multiple(() =>
        {
            Assert.That(StatusFor(events, "watch"), Is.EqualTo(StepStatus.Failed));
            Assert.That(MessageFor(events, "watch"), Does.Contain("not a number"));
            Assert.That(elapsed, Is.LessThan(5_000), "it waited out a timeout for a condition that could not hold");
        });
    }

    /// <summary>
    /// A timeout has to say what it last saw. "still not met" alone leaves a person choosing
    /// between a selector that matched nothing and a value that never became the one they asked
    /// for, which are opposite fixes.
    /// </summary>
    [Test]
    public async Task ATimeoutSaysWhatTheElementLastSaid()
    {
        PageReadsInTurn("working");

        var events = await RunAsync(Watching("ready", timeoutMs: 60));

        Assert.Multiple(() =>
        {
            Assert.That(StatusFor(events, "watch"), Is.EqualTo(StepStatus.Failed));
            Assert.That(MessageFor(events, "watch"), Does.Contain("#status last read 'working'"));
        });
    }

    /// <summary>And the same when nothing ever resolved, which is the other diagnosis.</summary>
    [Test]
    public async Task ATimeoutSaysSoWhenTheElementNeverAppeared()
    {
        // The array, spelled out: a lone null argument to a params method is a null ARRAY, not an
        // array holding null, and this test is about an element that is never there.
        PageReadsInTurn(new string?[] { null });

        var events = await RunAsync(Watching("ready", timeoutMs: 60));

        Assert.That(MessageFor(events, "watch"), Does.Contain("not on the page"));
    }

    /// <summary>
    /// A wait WITHOUT a target keeps the old behaviour exactly, and that is deliberate rather than
    /// leftover: re-asking a question about values the run already holds is a real thing to want
    /// after a called task or a loop row, and the target is what distinguishes the two.
    /// </summary>
    [Test]
    public async Task AWaitWithNoTargetStillOnlyAsksAboutValuesTheRunAlreadyHolds()
    {
        browser.DefaultEvalResponse = _ => """{ "ok": true, "value": "x" }""";
        var step = Watching("ready", timeoutMs: 60);
        step.Target = null;

        var events = await RunAsync(step);

        Assert.Multiple(() =>
        {
            Assert.That(StatusFor(events, "watch"), Is.EqualTo(StepStatus.Failed));
            // No reading was ever taken, so the message carries no "last read" clause — and the
            // failure is the binding being unresolvable, which is what it has always been.
            Assert.That(MessageFor(events, "watch"), Does.Not.Contain("last read"));
        });
    }

    /// <summary>
    /// What the wait finally saw is published, so the step after it can use the value instead of
    /// going back to the page for a second, possibly different, reading.
    /// </summary>
    [Test]
    public async Task WhatTheWaitSawIsAvailableToTheStepAfterIt()
    {
        PageReadsInTurn("working", "ready");

        var events = await RunAsync(
            Watching("ready"),
            new Step
            {
                Id = "after", Action = StepAction.Navigate, Label = "Go on",
                Url = "https://example.test/",
                Bindings = new Dictionary<string, BindingRef>
                {
                    ["Url"] = new()
                    {
                        Kind = BindingKind.StepOutput, SourceStepId = "watch", OutputField = "value",
                        Prefix = "https://example.test/",
                    },
                },
            });

        Assert.That(browser.Calls.Any(c => c.Method == "Navigate" && c.Args == "https://example.test/ready"),
            Is.True, "the value the wait saw did not reach the step after it");
    }

    // ---- a wait always has an end ----------------------------------------------------------------
    //
    // The editor never wrote a timeout, so every wait-until-a-condition authored in the app arrived
    // here with none — and the engine read that as "poll forever". A condition that was never going
    // to hold produced a run that neither finished nor said why, holding its browser until somebody
    // noticed. These pin the floor, in the one place both the engine and the editor read it from.

    [Test]
    public void AFreshWaitSpec_AlreadyHasAnEnd()
    {
        Assert.That(new WaitSpec().TimeoutMs, Is.EqualTo(WaitSpec.DefaultConditionTimeoutMs));
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(-1)]
    public void AWaitNobodyGaveATimeout_GivesUpAtTheDefaultRatherThanNever(int? stated)
    {
        Assert.That(WaitSpec.EffectiveTimeoutMs(stated), Is.EqualTo(WaitSpec.DefaultConditionTimeoutMs));
    }

    [Test]
    public void AWaitThatWasGivenATimeout_KeepsIt()
    {
        Assert.That(WaitSpec.EffectiveTimeoutMs(1234), Is.EqualTo(1234));
    }

    /// <summary>The editor writes the same number the engine falls back to, so a step authored in
    /// the app and one hand-edited out of it wait the same length.</summary>
    [Test]
    public void TheEditorAndTheEngineAgreeOnHowLongAWaitRunsForByDefault()
    {
        var core = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "Automata.App", "wwwroot", "core.js"));

        Assert.That(core, Does.Contain(
            $"export const DEFAULT_WAIT_TIMEOUT_MS = {WaitSpec.DefaultConditionTimeoutMs};"));
    }

    /// <summary>Walks up from the test binary to the folder holding the solution.</summary>
    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Automata.slnx")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "could not find the repository root from the test directory");
        return dir!.FullName;
    }

    // ---- the two places the same fact is written down --------------------------------------------

    /// <summary>
    /// The Gherkin catalog and the engine each name the output a watching wait publishes, and they
    /// are in different assemblies. One string, two files, and nothing else to keep them together.
    /// </summary>
    [Test]
    public void TheCatalogAndTheEngineAgreeOnWhatAWatchingWaitPublishes()
    {
        Assert.That(StepDefinitionCatalog.LiveWaitOutput, Is.EqualTo(WorkflowEngine.LiveWaitOutput));
    }

    /// <summary>
    /// The phrase compiles to a wait that actually watches — a target, and a condition pointing at
    /// the step's own reading. The id a step's condition has to name is minted by the compiler
    /// AFTER the catalog has built the step, so a compiler that forgot to put the real one in would
    /// produce a step that looks right in the editor and fails on its first poll.
    /// </summary>
    [Test]
    public void ThePhraseCompilesToAWaitThatWatchesItsOwnTarget()
    {
        var result = GherkinFlowCompiler.Compile(Feature);
        Assert.That(result.HasErrors, Is.False,
            string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

        var step = result.Tasks.Single().Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(step.Target?.CssSelector, Is.EqualTo("#status"));
            Assert.That(step.Wait?.Mode, Is.EqualTo(WaitMode.UntilCondition));
            Assert.That(step.Wait?.Condition?.Left.SourceStepId, Is.EqualTo(step.Id),
                "the condition must name the step it is on, whatever id that step ended up with");
            Assert.That(step.Wait?.Condition?.Right?.Literal, Is.EqualTo("ready"));
            Assert.That(step.Outputs?.Single().Name, Is.EqualTo(WorkflowEngine.LiveWaitOutput));
        });
    }

    /// <summary>A watching wait survives being written back out as Gherkin and read in again —
    /// including its self-reference, which is re-pointed at the NEW step's id on the way back.</summary>
    [Test]
    public void AWatchingWaitRoundTripsThroughGherkin()
    {
        var first = GherkinFlowCompiler.Compile(Feature);
        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        Assert.That(written.Text, Does.Contain(@"I wait until ""#status"" says ""ready"""), written.Text);

        var again = GherkinFlowCompiler.Compile(written.Text);
        var step = again.Tasks.Single().Steps.Single();

        Assert.Multiple(() =>
        {
            Assert.That(again.HasErrors, Is.False);
            Assert.That(step.Target?.CssSelector, Is.EqualTo("#status"));
            Assert.That(step.Wait?.Condition?.Left.SourceStepId, Is.EqualTo(step.Id));
        });
    }

    private const string Feature =
        """
        Feature: Watching
          Scenario: Watch the status
            When I wait until "#status" says "ready"
        """;

    // ---- the example ----------------------------------------------------------------------------

    /// <summary>
    /// The slow example must be one no captured-output wait could pass. If its status settled
    /// before the read, the example would pass either way and demonstrate nothing — which is what
    /// it did before this phase.
    /// </summary>
    [Test]
    public void TheSlowExampleWatchesRatherThanReChecking()
    {
        var slow = DemoTasks.All(Path.Combine(Path.GetTempPath(), "automata-livewait-shape"))
            .Single(d => d.Key == "slow");
        var wait = slow.Steps.Single(s => s.Action == StepAction.Wait && s.Wait?.Mode == WaitMode.UntilCondition);

        Assert.Multiple(() =>
        {
            Assert.That(wait.Target, Is.Not.Null, "without a target it is not watching anything");
            Assert.That(wait.Wait?.Condition?.Left.SourceStepId, Is.EqualTo(wait.Id),
                "its condition must ask about its own live reading");
            Assert.That(slow.Steps.Any(s => s.Action == StepAction.WriteDataset), Is.True,
                "the example writes the two readings down; without that nothing checks they differ");
        });
    }
}

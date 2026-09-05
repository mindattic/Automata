using Automata.Core.Automation;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class FingerprintResolverTests
{
    private static ElementFingerprint SampleFingerprint() => new()
    {
        Tag = "input",
        NameAttr = "btnK",
        TypeAttr = "submit",
        VisibleText = "Google Search",
        CssSelector = "input[name=\"btnK\"]",
    };

    private static FingerprintResolver FastResolver() => new() { PollIntervalMs = 10 };

    [Test]
    public async Task Resolve_FoundViaFallbackStrategy_CarriesRefreshedFingerprint()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """
            {
              "found": true, "unique": true, "strategy": "xpath", "score": 0,
              "ambiguous": false, "candidateCount": 1,
              "centerX": 100.5, "centerY": 240,
              "tag": "input", "text": "Google Search",
              "refreshedFingerprint": { "tag": "input", "nameAttr": "btnK2", "cssSelector": "input[name=\"btnK2\"]", "classList": [] }
            }
            """);

        var result = await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: false, refingerprint: true, timeoutMs: 1000, CancellationToken.None);

        Assert.That(result.Found, Is.True);
        Assert.That(result.Strategy, Is.EqualTo("xpath"));
        Assert.That(result.CenterX, Is.EqualTo(100.5));
        Assert.That(result.Refreshed, Is.Not.Null);
        Assert.That(result.Refreshed!.NameAttr, Is.EqualTo("btnK2"));
    }

    [Test]
    public async Task Resolve_AmbiguousEnvelope_ReportsNotFoundWithCandidateCount()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "found": false, "ambiguous": true, "candidateCount": 3 }""",
        };

        var result = await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: false, refingerprint: false, timeoutMs: 50, CancellationToken.None);

        Assert.That(result.Found, Is.False);
        Assert.That(result.Ambiguous, Is.True);
        Assert.That(result.CandidateCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Resolve_PollsUntilALaterAttemptFinds()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{ "found": false, "ambiguous": false, "candidateCount": 0 }""");
        browser.EvalResponses.Enqueue(_ => """{ "found": false, "ambiguous": false, "candidateCount": 0 }""");
        browser.EvalResponses.Enqueue(_ => """
            { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
              "candidateCount": 1, "centerX": 1, "centerY": 2, "tag": "input", "text": "x" }
            """);

        var result = await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: false, refingerprint: false, timeoutMs: 5000, CancellationToken.None);

        Assert.That(result.Found, Is.True);
        Assert.That(browser.Calls.Count(c => c.Method == "Eval"), Is.EqualTo(3));
    }

    [Test]
    public async Task Resolve_TimesOut_ReturnsLastNotFoundResult()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "found": false, "ambiguous": false, "candidateCount": 0 }""",
        };

        var result = await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: false, refingerprint: false, timeoutMs: 40, CancellationToken.None);

        Assert.That(result.Found, Is.False);
        Assert.That(browser.Calls, Is.Not.Empty);
    }

    [Test]
    public async Task Resolve_ScriptContainsSerializedFingerprintAndBothEmbeddedScripts()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "found": false }""",
        };

        await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: true, refingerprint: true, timeoutMs: 10, CancellationToken.None);

        var script = browser.Calls[0].Args;
        Assert.That(script, Does.Contain("btnK"));                    // the fingerprint payload
        Assert.That(script, Does.Contain("__automataResolve"));       // resolver.js
        Assert.That(script, Does.Contain("__automataFingerprint"));   // fingerprint.js
        // fingerprint.js calls into this and would throw without it, taking the whole resolve with
        // it — and WebView2 swallows an exception in injected script, so the symptom would be a
        // resolve that silently never finds anything.
        Assert.That(script, Does.Contain("__automataStability"));      // stability.js
        Assert.That(script, Does.Contain("\"highlight\": true"));
    }

    [Test]
    public async Task Resolve_GarbageEvalResult_TreatedAsNotFound()
    {
        var browser = new FakeBrowserSurface { DefaultEvalResponse = _ => "not json at all" };

        var result = await FastResolver().ResolveAsync(
            browser, SampleFingerprint(), highlight: false, refingerprint: false, timeoutMs: 10, CancellationToken.None);

        Assert.That(result.Found, Is.False);
    }

    [Test]
    public void EmbeddedScripts_LoadNonEmpty()
    {
        Assert.That(AutomationScripts.FingerprintJs, Does.Contain("__automataFingerprint"));
        Assert.That(AutomationScripts.ResolverJs, Does.Contain("__automataResolve"));
        Assert.That(AutomationScripts.ResolverJs, Does.Contain("__automataHighlight"));
        Assert.That(AutomationScripts.StabilityJs, Does.Contain("__automataStability"));
    }

    /// <summary>
    /// Both scripts have to be given the filter, because both ask it the same question — and
    /// keeping their own copies is exactly how they drifted apart, one rejecting a class the other
    /// recorded as identity.
    /// </summary>
    [Test]
    public void BothScriptsDeferToTheSharedStabilityRule()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutomationScripts.FingerprintJs, Does.Contain("__automataStability"));
            Assert.That(AutomationScripts.HarvestJs, Does.Contain("__automataStability"));
            Assert.That(AutomationScripts.FingerprintJs, Does.Not.Contain("AUTO_CLASS"),
                "fingerprint.js should no longer carry a filter of its own");
        });
    }
}

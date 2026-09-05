using Automata.Core.Automation;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The C# half of reaching past a closed shadow root or a cross-origin frame.
/// <para>
/// The reaching itself is JavaScript, and JavaScript is checked against a real DOM by
/// <c>tools/verify-js.mjs</c> — a fake browser answering canned JSON could never show that a
/// closed root was actually opened. What lives here is everything the engine has to get right
/// AROUND that: the shape of the bundle the host installs, and the two places C# now has to wait
/// for an answer that has to cross a frame boundary before it can exist.
/// </para>
/// </summary>
[TestFixture]
public class BeyondBoundaryTests
{
    // ---- what the host installs ------------------------------------------------------------------

    /// <summary>
    /// The registry has to be IN the document-start bundle, because that is the only place it works.
    /// Injected on demand like the rest of the toolkit it patches nothing that has not already
    /// happened, and every closed root the page built during startup stays shut — a failure that
    /// looks exactly like the feature not existing.
    /// </summary>
    [Test]
    public void TheDocumentStartBundleCarriesTheClosedRootRegistryAndTheFrameBridge()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutomationScripts.DocumentStartJs, Does.Contain("__automataClosedRoots"));
            Assert.That(AutomationScripts.DocumentStartJs, Does.Contain("attachShadow"));
            Assert.That(AutomationScripts.DocumentStartJs, Does.Contain("__automataFrames"));
            Assert.That(AutomationScripts.DocumentStartJs, Does.Contain("__automataResolveLocal"),
                "a frame that cannot resolve for itself has nothing to answer with");
        });
    }

    /// <summary>
    /// stability.js has to come before fingerprint.js reads it, and resolver.js before frames.js
    /// calls into it. Concatenation order is the only thing that decides that, and getting it wrong
    /// produces a page where the toolkit is half-defined rather than an error anyone can see.
    /// </summary>
    [Test]
    public void TheDocumentStartBundleIsInDependencyOrder()
    {
        var js = AutomationScripts.DocumentStartJs;
        Assert.Multiple(() =>
        {
            Assert.That(js.IndexOf("__automataStability", StringComparison.Ordinal),
                Is.LessThan(js.IndexOf("__automataFingerprint", StringComparison.Ordinal)));
            Assert.That(js.IndexOf("__automataResolveLocal", StringComparison.Ordinal),
                Is.LessThan(js.IndexOf("__automataFrames =", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// An injected script travels to the browser as a string across a COM boundary, and a control
    /// character in it is rejected there rather than by any JavaScript parser.
    /// <para>
    /// This is not hypothetical: a stray NUL — written as a separator inside a string literal —
    /// passed <c>node --check</c>, passed the build, and made WebView2 refuse the entire bundle with
    /// "Invalid or unexpected token". Nothing was installed, in any frame, and the only visible
    /// symptom was every element suddenly being unfindable.
    /// </para>
    /// </summary>
    [Test]
    public void NoInjectedScriptCarriesAControlCharacter()
    {
        var scripts = new (string Name, string Js)[]
        {
            ("stability.js", AutomationScripts.StabilityJs),
            ("fingerprint.js", AutomationScripts.FingerprintJs),
            ("resolver.js", AutomationScripts.ResolverJs),
            ("harvest.js", AutomationScripts.HarvestJs),
            ("closed.js", AutomationScripts.ClosedRootsJs),
            ("frames.js", AutomationScripts.FramesJs),
        };

        foreach (var (name, js) in scripts)
        {
            var offending = js
                .Select((c, i) => (Char: c, Index: i))
                .Where(x => char.IsControl(x.Char) && x.Char != '\n' && x.Char != '\r' && x.Char != '\t')
                .ToList();
            Assert.That(offending, Is.Empty,
                $"{name} carries a control character at offset {(offending.Count > 0 ? offending[0].Index : -1)}");
        }
    }

    // ---- waiting for an answer that has to cross a boundary ---------------------------------------

    /// <summary>
    /// A resolve that has to ask a frame cannot answer in the call that asks. It reports itself as
    /// waiting, and the poll the resolver already runs — for elements that render late — collects
    /// the answer. The point of this test is that "waiting" is NOT mistaken for "not found": the
    /// resolve must keep going rather than give up on the first attempt.
    /// </summary>
    [Test]
    public async Task AResolveWaitingOnAFrameKeepsPollingUntilTheFrameAnswers()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ =>
            """{ "found": false, "ambiguous": false, "candidateCount": 0, "waitingOnFrames": true }""");
        browser.EvalResponses.Enqueue(_ => """
            {
              "found": true, "unique": true, "strategy": "css", "score": 0,
              "ambiguous": false, "candidateCount": 1,
              "centerX": 168, "centerY": 336, "tag": "button",
              "text": "The button in the cross-origin frame", "frameDepth": 1
            }
            """);

        var result = await new FingerprintResolver { PollIntervalMs = 10 }.ResolveAsync(
            browser, new ElementFingerprint { Tag = "button", CssSelector = "#in-opaque" },
            highlight: false, refingerprint: false, timeoutMs: 1000, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Found, Is.True);
            Assert.That(result.CenterY, Is.EqualTo(336),
                "the centre must be the one the parent translated, not the one the frame measured");
        });
    }

    /// <summary>
    /// The same waiting shape for an ACTION, which has its own faster poll — there is nothing to
    /// wait for here but one message crossing one boundary, so it is collected in hundredths of a
    /// second rather than halves.
    /// </summary>
    [Test]
    public async Task AnActionForwardedIntoAFrameIsCollectedOnASecondAttempt()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{ "ok": false, "waitingOnFrames": true }""");
        browser.EvalResponses.Enqueue(_ => """{ "ok": true, "value": "the cross-origin frame was clicked" }""");

        var read = await BrowserActions.ReadResolvedTextAsync(browser, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(read.Ok, Is.True);
            Assert.That(read.Value, Is.EqualTo("the cross-origin frame was clicked"));
        });
    }

    /// <summary>
    /// The action script has to carry its body twice: inlined for the ordinary case, and as a string
    /// for the case where it has to be run inside a frame. Losing either one is silent — the inline
    /// copy going means every page breaks, the string copy going means only frames do.
    /// </summary>
    [Test]
    public async Task AnActionScriptCarriesItsBodyBothInlineAndAsSomethingAFrameCanBeSent()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "ok": true, "value": "x" }""",
        };

        await BrowserActions.ReadResolvedTextAsync(browser, CancellationToken.None);

        var script = browser.Calls.Single(c => c.Method == "Eval").Args;
        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("__automataActInFrame"),
                "an element found in a frame would have nowhere to send its action");
            Assert.That(script, Does.Contain("window.__automataLastResolved"),
                "the ordinary case must still run here, inlined, so a page forbidding eval is unaffected");
        });
    }

    // ---- the one action that still stops at a boundary --------------------------------------------

    /// <summary>
    /// Uploading is the only action that does not go through the resolver — it matches its input
    /// with a selector against the top document, because that is what CDP's file-setting call needs.
    /// So it is the only action a boundary still stops, and the requirement is that it SAYS so.
    /// The alternative it replaced was ten seconds of retrying a selector that could never match,
    /// ending in "no element matching '[data-automata-upload]'" — which reads as the marker being
    /// broken rather than as the input being somewhere unreachable.
    /// </summary>
    [Test]
    public async Task AttachingAFileToAnInputBehindABoundaryFailsSayingWhatIsWrong()
    {
        var browser = new FakeBrowserSurface
        {
            // Marking the element succeeds — the resolver reached it. Looking for the mark from the
            // top document does not, which is the whole diagnosis.
            DefaultEvalResponse = script => script.Contains("document.querySelector")
                ? """{ "ok": false }"""
                : """{ "ok": true, "value": null }""",
        };

        var result = await BrowserActions.UploadToResolvedAsync(
            browser, "C:/nowhere/notes.txt", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("shadow root"));
            Assert.That(browser.Calls.Any(c => c.Method == "InjectFile"), Is.False,
                "nothing should have been attached — the input the injector would have found is not this one");
        });
    }
}

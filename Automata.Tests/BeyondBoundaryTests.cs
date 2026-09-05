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

    /// <summary>
    /// A harvest that has to read a list inside a cross-origin frame is answered by the copy of the
    /// harvester already running in there, called BY NAME over the bridge — so unlike a forwarded
    /// action it needs no eval, and a frame with a strict Content-Security-Policy still answers.
    /// A name is only callable if something put it there, which is why harvest.js is in the
    /// document-start bundle rather than only injected on demand.
    /// </summary>
    [Test]
    public void TheDocumentStartBundleCarriesTheHarvesterSoAFrameCanBeAskedForItsRows()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutomationScripts.DocumentStartJs, Does.Contain("__automataHarvest"));
            Assert.That(AutomationScripts.FramesJs, Does.Contain("askCall"),
                "without a call-by-name op there is no way to run it in there");
        });
    }

    /// <summary>
    /// The same waiting shape a third time, for a harvest. Worth its own test because a harvest has
    /// no poll loop to borrow — a resolve polls because elements render late, and a harvest reads a
    /// page that is already there — so this one had to be given a short loop of its own, and a
    /// caller that treated "waiting" as "no rows" would report the page as changed.
    /// </summary>
    [Test]
    public async Task AHarvestWaitingOnAFrameIsCollectedRatherThanReadAsAnEmptyPage()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{ "ok": false, "waitingOnFrames": true, "count": 0, "rows": [] }""");
        browser.EvalResponses.Enqueue(_ => """
            {
              "ok": true, "count": 2, "emptyFields": [],
              "rows": [{ "ref": "O-1", "text": "First" }, { "ref": "O-2", "text": "Second" }]
            }
            """);

        var result = await HarvestRunner.RunAsync(browser, new HarvestSpec
        {
            ItemSelector = "ul.opaque-list > li.opaque-row",
            DatasetName = "opaque.csv",
            Fields = [new HarvestField { Name = "ref" }, new HarvestField { Name = "text" }],
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(result.Rows, Has.Count.EqualTo(2));
        });
    }

    /// <summary>
    /// And when the frame never answers, the harvest has to say THAT. Running out of the wait
    /// budget left the last "waiting" envelope in hand, and the caller read its <c>ok: false</c> as
    /// the ordinary empty-page answer — reporting "'<i>selector</i>' matched nothing on this page,
    /// the page has probably changed" about a page that had not changed and a selector that was
    /// fine. A diagnosis that sends someone to re-pick a working selector is worse than none.
    /// </summary>
    [Test]
    public async Task AHarvestWhoseFrameNeverAnswersSaysSoRatherThanBlamingTheSelector()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "ok": false, "waitingOnFrames": true, "count": 0, "rows": [] }""",
        };

        var result = await HarvestRunner.RunAsync(browser, new HarvestSpec
        {
            ItemSelector = "ul.opaque-list > li.opaque-row",
            DatasetName = "opaque.csv",
            ExpectedCount = 24,
            Fields = [new HarvestField { Name = "ref" }],
        }, CancellationToken.None, frameAnswerMs: 150);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("frame"),
                "the reason is the frame that did not answer");
            Assert.That(result.Error, Does.Not.Contain("probably changed"),
                "and not the selector, which never got a chance to match anything");
        });
    }

    // ---- the one action that reaches by a different route ------------------------------------------

    /// <summary>
    /// Uploading does not go through the resolver's act path — CDP's file-setting call needs a
    /// handle on the element, not a script that acts on it. It gets one by evaluating
    /// <c>window.__automataLastResolved</c>, which is the resolver's answer whatever root it came
    /// from, rather than a selector against the top document. That single substitution is what
    /// opens an upload into a shadow root of either kind, and into a same-origin frame.
    /// </summary>
    [Test]
    public async Task AttachingAFileAsksForTheElementTheResolverFoundRatherThanASelector()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{ "ok": true }""",
        };

        var result = await BrowserActions.UploadToResolvedAsync(
            browser, "C:/nowhere/notes.txt", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(browser.Calls.Single(c => c.Method == "InjectFile").Args,
                Does.Contain("window.__automataLastResolved"),
                "a selector against the top document is exactly what stops at a shadow boundary");
        });
    }

    /// <summary>
    /// And the case that genuinely cannot work, checked for rather than discovered. A handle does
    /// not cross an origin boundary — the expression is evaluated in the top document's context and
    /// the element is not in it — so the alternative to saying so is attaching the file to whatever
    /// was left on <c>window.__automataLastResolved</c> up here, which would be a silent wrong
    /// answer rather than a loud missing one.
    /// </summary>
    [Test]
    public async Task AttachingAFileInsideACrossOriginFrameFailsSayingWhyItCannot()
    {
        var browser = new FakeBrowserSurface
        {
            // The resolve landed in a frame, which is what resolvedFrame being set means.
            DefaultEvalResponse = _ => """{ "ok": false }""",
        };

        var result = await BrowserActions.UploadToResolvedAsync(
            browser, "C:/nowhere/notes.txt", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("cross-origin frame"));
            Assert.That(browser.Calls.Any(c => c.Method == "InjectFile"), Is.False,
                "nothing should have been attached — the element the expression would find is not this one");
        });
    }
}

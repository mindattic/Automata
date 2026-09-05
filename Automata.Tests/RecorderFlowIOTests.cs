using System.Text.Json;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Model;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RecorderFlowIOTests
{
    /// <summary>Shaped like a real Chrome DevTools Recorder export, including the multi-strategy
    /// selector array that is the reason this format converts faithfully at all.</summary>
    private const string ChromeExport = """
        {
          "title": "Search flow",
          "steps": [
            { "type": "setViewport", "width": 1280, "height": 900, "deviceScaleFactor": 1,
              "isMobile": false, "hasTouch": false, "isLandscape": false },
            { "type": "navigate", "url": "https://www.google.com/" },
            { "type": "click",
              "selectors": [ ["textarea[name='q']"], ["aria/Search"], ["text/Search"], ["xpath///textarea"] ],
              "offsetX": 10, "offsetY": 12 },
            { "type": "change", "value": "wolf tshirts", "selectors": [ ["textarea[name='q']"] ] },
            { "type": "keyDown", "key": "Enter" },
            { "type": "keyUp", "key": "Enter" },
            { "type": "waitForElement", "selectors": [ ["#search"] ] }
          ]
        }
        """;

    [Test]
    public void ImportsAChromeRecordingIntoATask()
    {
        var result = RecorderFlowIO.Import(ChromeExport);

        Assert.Multiple(() =>
        {
            Assert.That(result.Task.Name, Is.EqualTo("Search flow"));
            Assert.That(result.Task.StartUrl, Is.EqualTo("https://www.google.com/"),
                "the opening navigate becomes the start URL rather than a duplicate first step");
            Assert.That(result.Task.Steps.Select(s => s.Action), Is.EqualTo(new[]
            {
                StepAction.Click, StepAction.SetValue, StepAction.PressEnter, StepAction.WaitForElement,
            }));
            Assert.That(result.Task.Steps[1].Value, Is.EqualTo("wolf tshirts"));
        });
    }

    /// <summary>
    /// The correspondence that makes this worth adopting: Recorder's selector alternatives are the
    /// same idea as a multi-strategy fingerprint, so all of them survive rather than one winning.
    /// </summary>
    [Test]
    public void EverySelectorStrategyLandsOnTheFingerprint()
    {
        var target = RecorderFlowIO.Import(ChromeExport).Task.Steps[0].Target!;

        Assert.Multiple(() =>
        {
            Assert.That(target.CssSelector, Is.EqualTo("textarea[name='q']"));
            Assert.That(target.AriaLabel, Is.EqualTo("Search"));
            Assert.That(target.VisibleText, Is.EqualTo("Search"));
            Assert.That(target.XPath, Is.EqualTo("//textarea"));
        });
    }

    [Test]
    public void KeyUpIsDroppedSilentlyBecauseKeyDownAlreadyCarriedIt()
    {
        var result = RecorderFlowIO.Import(ChromeExport);

        Assert.That(result.Warnings.Any(w => w.Contains("keyUp")), Is.False);
        Assert.That(result.Task.Steps.Count(s => s.Action == StepAction.PressEnter), Is.EqualTo(1));
    }

    [Test]
    public void UnsupportedStepsAreReportedRatherThanDroppedQuietly()
    {
        var result = RecorderFlowIO.Import("""
            { "title": "T", "steps": [
              { "type": "hover", "selectors": [["#a"]] },
              { "type": "scroll", "x": 0, "y": 200 },
              { "type": "waitForExpression", "expression": "1 === 1" }
            ] }
            """);

        Assert.That(result.Task.Steps, Is.Empty);
        Assert.That(result.Warnings, Has.Count.EqualTo(3));
        Assert.That(string.Join(" ", result.Warnings), Does.Contain("hover").And.Contain("scroll"));
    }

    [Test]
    public void ANonEnterKeyIsReportedRatherThanBecomingAnEnterPress()
    {
        var result = RecorderFlowIO.Import("""
            { "title": "T", "steps": [ { "type": "keyDown", "key": "Tab" } ] }
            """);

        Assert.That(result.Task.Steps, Is.Empty);
        Assert.That(result.Warnings.Single(), Does.Contain("Tab"));
    }

    /// <summary>Shadow DOM is a documented limitation; a pierce selector must say so, not pretend.</summary>
    [Test]
    public void APierceSelectorIsReportedAsUnsupported()
    {
        var result = RecorderFlowIO.Import("""
            { "title": "T", "steps": [
              { "type": "click", "selectors": [ ["pierce/#inner"], ["#outer"] ] }
            ] }
            """);

        Assert.That(result.Warnings.Any(w => w.Contains("pierce")), Is.True);
        Assert.That(result.Task.Steps.Single().Target!.CssSelector, Is.EqualTo("#outer"),
            "the usable alternative should still be taken");
    }

    /// <summary>
    /// A chain is one selector per shadow boundary, outermost first, so its LAST part names the
    /// element and everything before it names a host on the way there. Keeping the first part
    /// recorded a step that clicks the WRAPPER and never the control — the same mistake the
    /// recorder avoids by reading <c>composedPath()[0]</c>, arrived at from the other direction.
    /// The resolver has searched every open shadow root since phase 18, so the innermost part is
    /// the one it can actually find.
    /// </summary>
    [Test]
    public void AMultiPartSelectorChainKeepsItsInnermostPartAndSaysSo()
    {
        var result = RecorderFlowIO.Import("""
            { "title": "T", "steps": [
              { "type": "click", "selectors": [ ["#host", "#inner"] ] }
            ] }
            """);

        Assert.That(result.Task.Steps.Single().Target!.CssSelector, Is.EqualTo("#inner"));
        Assert.That(result.Warnings.Any(w => w.Contains("shadow boundary")), Is.True,
            "reaching through a boundary is still worth saying, because the chain itself is gone");
    }

    /// <summary>
    /// Chrome puts a setViewport at the head of EVERY recording, and <see cref="RecorderFlowIO"/>'s
    /// own Export writes one back — so warning about it fired on every real import and on Automata's
    /// own round trip. A warning with nothing at stake behind it is how people learn to stop reading
    /// warnings.
    /// </summary>
    [Test]
    public void AWholeChromeRecordingImportsWithNothingToReport()
    {
        var result = RecorderFlowIO.Import(ChromeExport);

        Assert.That(result.Warnings, Is.Empty, string.Join("; ", result.Warnings));
    }

    [Test]
    public void InvalidJsonIsReportedRatherThanThrowing()
    {
        var result = RecorderFlowIO.Import("{ not json");

        Assert.That(result.Task.Steps, Is.Empty);
        Assert.That(result.Warnings.Single(), Does.Contain("not valid JSON"));
    }

    [Test]
    public void SomethingThatIsNotARecordingIsReported()
    {
        var result = RecorderFlowIO.Import("""{ "hello": "world" }""");

        Assert.That(result.Warnings.Single(), Does.Contain("Recorder export"));
    }

    // ---- export ---------------------------------------------------------------------------------

    [Test]
    public void ExportsAFlowChromeCanRead()
    {
        var task = new TaskDefinition
        {
            Name = "Search flow",
            StartUrl = "https://www.google.com/",
            Steps =
            [
                new Step
                {
                    Action = StepAction.TypeText, Value = "wolf tshirts",
                    Target = new ElementFingerprint
                    {
                        CssSelector = "textarea[name='q']", AriaLabel = "Search", VisibleText = "Search",
                    },
                },
                new Step { Action = StepAction.PressEnter },
            ],
        };

        using var doc = JsonDocument.Parse(RecorderFlowIO.Export(task));
        var steps = doc.RootElement.GetProperty("steps");

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("Search flow"));
            Assert.That(steps[0].GetProperty("type").GetString(), Is.EqualTo("setViewport"));
            Assert.That(steps[1].GetProperty("type").GetString(), Is.EqualTo("navigate"));
            Assert.That(steps[2].GetProperty("type").GetString(), Is.EqualTo("change"));
            Assert.That(steps[2].GetProperty("selectors")[0].GetString(), Is.EqualTo("textarea[name='q']"));
            Assert.That(steps[2].GetProperty("selectors")[1].GetString(), Is.EqualTo("aria/Search"));
            Assert.That(steps[3].GetProperty("type").GetString(), Is.EqualTo("keyDown"));
        });
    }

    /// <summary>
    /// A control-flow step has no Recorder shape. Emitting an invented one would produce a file
    /// Chrome cannot replay, so it is left out.
    /// </summary>
    [Test]
    public void AutomataOnlyStepsAreLeftOutOfAnExport()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step { Action = StepAction.If, Condition = new ConditionSpec() },
                new Step { Action = StepAction.WriteDataset, WriteDataset = new DatasetWriteSpec() },
                new Step { Action = StepAction.Click, Target = new ElementFingerprint { CssSelector = "#go" } },
            ],
        };

        using var doc = JsonDocument.Parse(RecorderFlowIO.Export(task));
        var types = doc.RootElement.GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("type").GetString()).ToList();

        Assert.That(types, Is.EqualTo(new[] { "setViewport", "click" }));
    }

    [Test]
    public void ExportFlattensSubstepsInDocumentOrder()
    {
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Action = StepAction.Click, Target = new ElementFingerprint { CssSelector = "#a" },
                    Children = [new Step { Action = StepAction.Click, Target = new ElementFingerprint { CssSelector = "#b" } }],
                },
            ],
        };

        using var doc = JsonDocument.Parse(RecorderFlowIO.Export(task));
        var selectors = doc.RootElement.GetProperty("steps").EnumerateArray()
            .Where(s => s.TryGetProperty("selectors", out _))
            .Select(s => s.GetProperty("selectors")[0].GetString()).ToList();

        Assert.That(selectors, Is.EqualTo(new[] { "#a", "#b" }));
    }

    [Test]
    public void ExportThenImportRoundTripsTheOverlappingSubset()
    {
        var original = new TaskDefinition
        {
            Name = "Round trip",
            StartUrl = "https://x.example/",
            Steps =
            [
                new Step { Action = StepAction.Click, Target = new ElementFingerprint { CssSelector = "#go", AriaLabel = "Go" } },
                new Step { Action = StepAction.SetValue, Value = "abc", Target = new ElementFingerprint { CssSelector = "#field" } },
                new Step { Action = StepAction.PressEnter },
                new Step { Action = StepAction.WaitForElement, Target = new ElementFingerprint { CssSelector = "#done" } },
            ],
        };

        var back = RecorderFlowIO.Import(RecorderFlowIO.Export(original)).Task;

        Assert.Multiple(() =>
        {
            Assert.That(back.StartUrl, Is.EqualTo(original.StartUrl));
            Assert.That(back.Steps.Select(s => s.Action), Is.EqualTo(original.Steps.Select(s => s.Action)));
            Assert.That(back.Steps[0].Target!.CssSelector, Is.EqualTo("#go"));
            Assert.That(back.Steps[0].Target!.AriaLabel, Is.EqualTo("Go"));
            Assert.That(back.Steps[1].Value, Is.EqualTo("abc"));
        });
    }
}

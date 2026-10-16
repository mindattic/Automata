using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="CheckCheckboxTool"/> is a thin wrapper over <c>AutoWebNav.BrowserFormHelpers
/// .TickMatchingCheckboxesAsync</c>, which itself: (1) probes whether the page is still doing
/// server-side work, (2) if not, repeatedly locates and real-clicks the next unchecked match until
/// none remain. These tests drive that whole sequence through the fake surface's response queue,
/// one eval per probe/locate.
/// </summary>
[TestFixture]
public class CheckCheckboxToolTests
{
    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static BrowserOperatorContext Ctx(FakeBrowserSurface browser) => new() { Browser = browser };

    [Test]
    public async Task InvokeAsync_EmptyTextCandidates_ReturnsErrorWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new CheckCheckboxTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":[]}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
                Is.EqualTo("text_candidates was empty."));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_OnlyBlankTextCandidates_ReturnsErrorWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new CheckCheckboxTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["",""]}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
                Is.EqualTo("text_candidates was empty."));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_NoMatches_ReturnsZeroCheckedCount()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{"isProcessing":false}""");
        browser.EvalResponses.Enqueue(_ => """{"found":false}""");
        var tool = new CheckCheckboxTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["terms"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("checkedCount").GetInt32(), Is.EqualTo(0));
            Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False);
        });
    }

    [Test]
    public async Task InvokeAsync_TicksEveryMatchAtItsOwnReportedCenter()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{"isProcessing":false}""");
        browser.EvalResponses.Enqueue(_ => """{"found":true,"text":"Terms","centerX":5,"centerY":6}""");
        browser.EvalResponses.Enqueue(_ => """{"found":true,"text":"Privacy","centerX":7,"centerY":8}""");
        browser.EvalResponses.Enqueue(_ => """{"found":false}""");
        var tool = new CheckCheckboxTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["terms","privacy"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        var clicks = browser.Calls.Where(c => c.Method == "ClickAtPoint").Select(c => c.Args).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(clicks, Is.EqualTo(new[] { "5,6", "7,8" }));
            Assert.That(doc.RootElement.GetProperty("checkedCount").GetInt32(), Is.EqualTo(2));
            Assert.That(doc.RootElement.GetProperty("matches").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(new[] { "Terms", "Privacy" }));
        });
    }

    [Test]
    public async Task InvokeAsync_BlockedByProcessing_ReturnsWithoutClickingAnything()
    {
        var browser = new FakeBrowserSurface();
        browser.EvalResponses.Enqueue(_ => """{"isProcessing":true,"indicator":"Please wait while we save"}""");
        var tool = new CheckCheckboxTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["terms"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("checkedCount").GetInt32(), Is.EqualTo(0));
            Assert.That(doc.RootElement.GetProperty("blockedByProcessing").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("processingIndicator").GetString(),
                Is.EqualTo("Please wait while we save"));
            Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False,
                "a page still doing server-side work must never be clicked on");
            Assert.That(browser.Calls.Count(c => c.Method == "Eval"), Is.EqualTo(1),
                "the processing probe alone should have been enough to stop here");
        });
    }
}

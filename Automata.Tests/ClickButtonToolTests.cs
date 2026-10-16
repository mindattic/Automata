using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="ClickButtonTool"/> drives <see cref="IBrowserTool.InvokeAsync"/> directly against a
/// <see cref="FakeBrowserSurface"/> — no real browser involved, since the matching logic itself
/// lives in the eval'd JavaScript and is covered separately by <c>tools/verify-js.mjs</c>. What's
/// checked here is everything around that: candidate validation before any browser call is made,
/// correct escaping of candidates into the script, and the settle delay that only a real click
/// should cost.
/// </summary>
[TestFixture]
public class ClickButtonToolTests
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
        var tool = new ClickButtonTool();

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
        var tool = new ClickButtonTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["",""]}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
                Is.EqualTo("text_candidates was empty."));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_EscapesCandidatesIntoTheEvalScript()
    {
        string? script = null;
        var browser = new FakeBrowserSurface { DefaultEvalResponse = s => { script = s; return """{"clicked":false}"""; } };
        var tool = new ClickButtonTool();
        var candidates = new[] { "save \"now\"", "a\\b" };

        await tool.InvokeAsync(Args($$"""{"text_candidates":{{JsonSerializer.Serialize(candidates)}}}"""), Ctx(browser), CancellationToken.None);

        Assert.That(script, Does.Contain(JsonSerializer.Serialize(candidates)),
            "the candidates must reach the script through JSON serialization, not naive string concatenation");
    }

    [Test]
    public async Task InvokeAsync_BlankCandidatesAreFilteredOutOfTheScript()
    {
        string? script = null;
        var browser = new FakeBrowserSurface { DefaultEvalResponse = s => { script = s; return """{"clicked":false}"""; } };
        var tool = new ClickButtonTool();

        await tool.InvokeAsync(Args("""{"text_candidates":["","save"]}"""), Ctx(browser), CancellationToken.None);

        Assert.That(script, Does.Contain(JsonSerializer.Serialize(new[] { "save" })));
    }

    [Test]
    public async Task InvokeAsync_WhenClicked_SettlesBeforeReturning()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"clicked":true,"matchedText":"Save"}""",
        };
        var tool = new ClickButtonTool();

        var started = Environment.TickCount64;
        var result = await tool.InvokeAsync(Args("""{"text_candidates":["save"]}"""), Ctx(browser), CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;

        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(900),
                "a click that may have navigated or transitioned the page must be given time to settle");
            Assert.That(result, Does.Contain("\"clicked\":true"));
        });
    }

    [Test]
    public async Task InvokeAsync_WhenNotClicked_ReturnsWithoutSettling()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"clicked":false}""",
        };
        var tool = new ClickButtonTool();

        var started = Environment.TickCount64;
        var result = await tool.InvokeAsync(Args("""{"text_candidates":["save"]}"""), Ctx(browser), CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;

        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.LessThan(500), "nothing happened on the page, so there is nothing to wait for");
            Assert.That(result, Does.Contain("\"clicked\":false"));
        });
    }
}

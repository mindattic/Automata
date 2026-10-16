using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="SelectFormOptionTool"/>'s own matching logic is JavaScript, checked separately
/// against a real DOM — what's exercised here is the C# half: candidate validation before any
/// browser call, and the fork between a NATIVE result (already clicked by the script) and a
/// WIDGET result (which must still be clicked for real, at the center point the script reported).
/// </summary>
[TestFixture]
public class SelectFormOptionToolTests
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
        var tool = new SelectFormOptionTool();

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
        var tool = new SelectFormOptionTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":[""]}"""), Ctx(browser), CancellationToken.None);

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
        var browser = new FakeBrowserSurface { DefaultEvalResponse = s => { script = s; return """{"found":false}"""; } };
        var tool = new SelectFormOptionTool();
        var candidates = new[] { "I \"agree\"", "a\\b" };

        await tool.InvokeAsync(Args($$"""{"text_candidates":{{JsonSerializer.Serialize(candidates)}}}"""), Ctx(browser), CancellationToken.None);

        Assert.That(script, Does.Contain(JsonSerializer.Serialize(candidates)));
    }

    [Test]
    public async Task InvokeAsync_NativeResult_ReturnsSelectedWithoutClickingAgain()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":true,"kind":"native","clicked":true,"text":"Yes"}""",
        };
        var tool = new SelectFormOptionTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["yes"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("selected").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("matchedText").GetString(), Is.EqualTo("Yes"));
            Assert.That(doc.RootElement.GetProperty("kind").GetString(), Is.EqualTo("native"));
            Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False,
                "the script already clicked the native element directly — a second click would be redundant");
        });
    }

    [Test]
    public async Task InvokeAsync_NativeResult_PassesThroughAFailedClick()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":true,"kind":"native","clicked":false,"text":"Yes"}""",
        };
        var tool = new SelectFormOptionTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["yes"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.That(doc.RootElement.GetProperty("selected").GetBoolean(), Is.False);
    }

    [Test]
    public async Task InvokeAsync_WidgetResult_ClicksAtTheReportedCenter()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":true,"kind":"widget","text":"No","centerX":12.5,"centerY":34.5}""",
        };
        var tool = new SelectFormOptionTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["no"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(browser.Calls.Single(c => c.Method == "ClickAtPoint").Args, Is.EqualTo("12.5,34.5"),
                "a custom widget needs a real trusted click at the center the script measured");
            Assert.That(doc.RootElement.GetProperty("selected").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("kind").GetString(), Is.EqualTo("widget"));
            Assert.That(doc.RootElement.GetProperty("matchedText").GetString(), Is.EqualTo("No"));
        });
    }

    [Test]
    public async Task InvokeAsync_NotFound_ReturnsSelectedFalseWithoutClicking()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":false}""",
        };
        var tool = new SelectFormOptionTool();

        var result = await tool.InvokeAsync(Args("""{"text_candidates":["maybe"]}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("selected").GetBoolean(), Is.False);
            Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False);
        });
    }
}

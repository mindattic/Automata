using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="TypeIntoFieldTool"/> locates a field by label text, then hands off to
/// <c>AutoWebNav.BrowserActions.TypeViaKeystrokesAsync</c> for the actual click-focus,
/// select-all, type, read-back sequence. These tests check both halves: that an unfound field
/// never touches the click/type surface at all, and that a found one drives the surface in the
/// right order with the right arguments.
/// </summary>
[TestFixture]
public class TypeIntoFieldToolTests
{
    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static BrowserOperatorContext Ctx(FakeBrowserSurface browser) => new() { Browser = browser };

    [TestCase("""{"label_text":"","text":"hi"}""")]
    [TestCase("""{"label_text":"Name","text":""}""")]
    public async Task InvokeAsync_MissingLabelOrText_ReturnsErrorWithoutTouchingBrowser(string argsJson)
    {
        var browser = new FakeBrowserSurface();
        var tool = new TypeIntoFieldTool();

        var result = await tool.InvokeAsync(Args(argsJson), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
                Is.EqualTo("label_text and text are both required."));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_FieldNotFound_ReturnsFoundFalseWithoutClickingOrTyping()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":false}""",
        };
        var tool = new TypeIntoFieldTool();

        var result = await tool.InvokeAsync(Args("""{"label_text":"Price","text":"9.99"}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("found").GetBoolean(), Is.False);
            Assert.That(browser.Calls.Any(c => c.Method == "ClickAtPoint"), Is.False);
            Assert.That(browser.Calls.Any(c => c.Method == "TypeText"), Is.False);
            Assert.That(browser.Calls.Count(c => c.Method == "Eval"), Is.EqualTo(1),
                "an unfound field should stop after the one locate call");
        });
    }

    [Test]
    public async Task InvokeAsync_FieldFound_ClicksThenSelectsThenTypesThenReadsBack()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"ok":true,"value":null}""",
        };
        // The locate step runs before the generic default kicks in for the rest of the sequence.
        browser.EvalResponses.Enqueue(_ => """{"found":true,"centerX":100,"centerY":200}""");
        var tool = new TypeIntoFieldTool();

        var result = await tool.InvokeAsync(Args("""{"label_text":"Price","text":"9.99"}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(browser.Calls.Select(c => c.Method), Is.EqualTo(new[] { "Eval", "ClickAtPoint", "Eval", "TypeText", "Eval" }),
                "locate, click-focus, select-all, type, then read back — in that order");
            Assert.That(browser.Calls[1].Args, Is.EqualTo("100,200"),
                "the click must land exactly where the locate script measured the field's center");
            Assert.That(browser.Calls[3].Args, Is.EqualTo("9.99"));
            Assert.That(doc.RootElement.GetProperty("found").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("typed").GetString(), Is.EqualTo("9.99"));
            Assert.That(doc.RootElement.GetProperty("value").GetString(), Is.EqualTo("9.99"),
                "the field's real value afterwards — what a human typing over it would leave behind");
        });
    }
}

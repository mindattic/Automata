using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="SetFieldTool"/> builds one eval script per call and returns it verbatim — the
/// matching strategy (literal id, then label-text fallback) lives in that JavaScript and is
/// checked against a real DOM elsewhere. What's checked here is the C# side: the empty-field guard
/// before any browser call, that <c>field</c>/<c>value</c> reach the script JSON-escaped rather
/// than naively concatenated, and that the eval result passes straight back through unparsed.
/// </summary>
[TestFixture]
public class SetFieldToolTests
{
    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static BrowserOperatorContext Ctx(FakeBrowserSurface browser) => new() { Browser = browser };

    [Test]
    public async Task InvokeAsync_EmptyField_ReturnsErrorWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new SetFieldTool();

        var result = await tool.InvokeAsync(Args("""{"field":"","value":"x"}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
                Is.EqualTo("field was empty."));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_EscapesFieldAndValueIntoTheEvalScript()
    {
        string? script = null;
        var browser = new FakeBrowserSurface { DefaultEvalResponse = s => { script = s; return """{"found":false}"""; } };
        var tool = new SetFieldTool();

        await tool.InvokeAsync(Args("""{"field":"He said \"hi\"","value":"C:\\path\\x"}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain(JsonSerializer.Serialize("He said \"hi\"")),
                "the field name is used twice (as an id lookup and as a label query) and must be escaped both times");
            Assert.That(script, Does.Contain(JsonSerializer.Serialize("C:\\path\\x")));
        });
    }

    [Test]
    public async Task InvokeAsync_ReturnsTheEvalResultVerbatim()
    {
        var browser = new FakeBrowserSurface
        {
            DefaultEvalResponse = _ => """{"found":true,"strategy":"id","elementId":"price","value":"42"}""",
        };
        var tool = new SetFieldTool();

        var result = await tool.InvokeAsync(Args("""{"field":"price","value":"42"}"""), Ctx(browser), CancellationToken.None);

        Assert.That(result, Is.EqualTo("""{"found":true,"strategy":"id","elementId":"price","value":"42"}"""));
    }
}

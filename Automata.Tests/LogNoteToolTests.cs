using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="LogNoteTool"/> reaches the human only through the operator loop's own event stream —
/// it has no side channel and nothing to say to the browser.
/// </summary>
[TestFixture]
public class LogNoteToolTests
{
    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Test]
    public async Task InvokeAsync_ReturnsLoggedTrueWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new LogNoteTool();

        var result = await tool.InvokeAsync(
            Args("""{"message":"skipping the newsletter opt-in, already unchecked"}"""),
            new BrowserOperatorContext { Browser = browser }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("""{"logged":true}"""));
            Assert.That(browser.Calls, Is.Empty);
        });
    }
}

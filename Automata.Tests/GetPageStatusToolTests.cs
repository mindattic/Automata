using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="GetPageStatusTool"/> is a single fixed eval script with no arguments — it reads
/// whatever the page currently shows and returns the JSON verbatim, unparsed, so the LLM sees
/// exactly what the script measured.
/// </summary>
[TestFixture]
public class GetPageStatusToolTests
{
    private static BrowserOperatorContext Ctx(FakeBrowserSurface browser) => new() { Browser = browser };

    [Test]
    public async Task InvokeAsync_ReturnsTheEvalResultVerbatim()
    {
        const string response = """{"url":"https://x/","title":"X","heading":"Hi","banners":[],"isProcessing":false,"processingIndicators":[]}""";
        var browser = new FakeBrowserSurface { DefaultEvalResponse = _ => response };
        var tool = new GetPageStatusTool();

        var result = await tool.InvokeAsync(default, Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(response));
            Assert.That(browser.Calls, Has.Count.EqualTo(1));
            Assert.That(browser.Calls[0].Method, Is.EqualTo("Eval"));
        });
    }

    [Test]
    public async Task InvokeAsync_ScriptChecksForTheSharedProcessingWordPattern()
    {
        string? script = null;
        var browser = new FakeBrowserSurface { DefaultEvalResponse = s => { script = s; return "{}"; } };
        var tool = new GetPageStatusTool();

        await tool.InvokeAsync(default, Ctx(browser), CancellationToken.None);

        Assert.That(script, Does.Contain("please wait"),
            "it must reuse the same processing-word detection every other tool relies on, not its own copy");
    }
}

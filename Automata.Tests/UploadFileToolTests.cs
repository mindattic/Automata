using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Automata.Tests.Fakes;
using AutoWebNav;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// <see cref="UploadFileTool"/> never opens a native file dialog — it hands a local path straight
/// to <see cref="IBrowserSurface.InjectFileAsync"/>. These tests check the guard that stops a
/// nonexistent file before any browser call, the default/custom selector that reaches
/// <c>InjectFileAsync</c>, and that an exception from the surface comes back as an error rather
/// than propagating.
/// </summary>
[TestFixture]
public class UploadFileToolTests
{
    private string tempFile = null!;

    [SetUp]
    public void SetUp()
    {
        tempFile = Path.Combine(Path.GetTempPath(), "automata-upload-test-" + Guid.NewGuid().ToString("n")[..8] + ".txt");
        File.WriteAllText(tempFile, "contents");
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(tempFile); } catch { /* best effort */ }
    }

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static BrowserOperatorContext Ctx(IBrowserSurface browser) => new() { Browser = browser };

    [Test]
    public async Task InvokeAsync_FileDoesNotExist_ReturnsErrorWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new UploadFileTool();
        var missing = Path.Combine(Path.GetTempPath(), "automata-upload-missing-" + Guid.NewGuid().ToString("n") + ".txt");

        var result = await tool.InvokeAsync(Args($$"""{"file_path":{{JsonSerializer.Serialize(missing)}}}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(), Does.Contain(missing));
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_BlankFilePath_ReturnsErrorWithoutTouchingBrowser()
    {
        var browser = new FakeBrowserSurface();
        var tool = new UploadFileTool();

        var result = await tool.InvokeAsync(Args("""{"file_path":""}"""), Ctx(browser), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonDocument.Parse(result).RootElement.TryGetProperty("error", out _), Is.True);
            Assert.That(browser.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InvokeAsync_DefaultSelector_InjectsTheFile()
    {
        var browser = new FakeBrowserSurface();
        var tool = new UploadFileTool();

        var result = await tool.InvokeAsync(Args($$"""{"file_path":{{JsonSerializer.Serialize(tempFile)}}}"""), Ctx(browser), CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("ok").GetBoolean(), Is.True);
            var inject = browser.Calls.Single(c => c.Method == "InjectFile");
            Assert.That(inject.Args, Is.EqualTo($"{tempFile} -> input[type=file]"));
        });
    }

    [Test]
    public async Task InvokeAsync_CustomSelector_IsUsedInsteadOfTheDefault()
    {
        var browser = new FakeBrowserSurface();
        var tool = new UploadFileTool();

        await tool.InvokeAsync(
            Args($$"""{"file_path":{{JsonSerializer.Serialize(tempFile)}},"selector":"#cover-upload"}"""),
            Ctx(browser), CancellationToken.None);

        var inject = browser.Calls.Single(c => c.Method == "InjectFile");
        Assert.That(inject.Args, Is.EqualTo($"{tempFile} -> #cover-upload"));
    }

    /// <summary>A throwing surface, standing in for CDP rejecting the file (e.g. a non-file input).</summary>
    private sealed class ThrowingSurface : IBrowserSurface
    {
        public string CurrentUrl => "about:blank";
        public Task NavigateAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> EvalAsync(string script, CancellationToken ct) => throw new NotSupportedException();
        public Task InjectFileAsync(string filePath, string selector, CancellationToken ct) =>
            throw new InvalidOperationException("no such input on this page");
        public Task ClickAtPointAsync(double x, double y, CancellationToken ct) => throw new NotSupportedException();
        public Task TypeTextAsync(string text, CancellationToken ct) => throw new NotSupportedException();
        public Task PressEnterAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<double> SetZoomAsync(double factor, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task InvokeAsync_SurfaceThrows_ReturnsTheErrorMessageRatherThanPropagating()
    {
        var tool = new UploadFileTool();

        var result = await tool.InvokeAsync(
            Args($$"""{"file_path":{{JsonSerializer.Serialize(tempFile)}}}"""),
            Ctx(new ThrowingSurface()), CancellationToken.None);

        Assert.That(JsonDocument.Parse(result).RootElement.GetProperty("error").GetString(),
            Is.EqualTo("no such input on this page"));
    }
}

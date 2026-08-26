using System.Text.Json;
using Automata.Core.Operator;
using Microsoft.Web.WebView2.Core;

namespace Automata.App;

/// <summary>Real implementation of <see cref="IBrowserSurface"/> against a live WebView2 pane.
/// Ported from Prose.KdpPublish's <c>WebView2KdpBrowser</c>.</summary>
public class WebView2BrowserSurface : IBrowserSurface
{
    private readonly CoreWebView2 core;

    public WebView2BrowserSurface(CoreWebView2 core)
    {
        this.core = core;
    }

    public string CurrentUrl => core.Source;

    // Navigations get a longer leash than script calls — a cold page over a slow connection can
    // legitimately take a while, and the caller (replay engine) budgets per-step anyway.
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();

        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(NavigationTimeout, ct));
            if (winner != tcs.Task)
            {
                // A fired cancellation token also makes the delay win — report THAT truthfully
                // rather than misdiagnosing a user cancel as a page timeout.
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"Navigation to {url} did not complete within {NavigationTimeout.TotalSeconds}s.");
            }
            await tcs.Task;
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    // ExecuteScriptAsync/CallDevToolsProtocolMethodAsync take no CancellationToken and have no
    // built-in timeout — if the renderer's JS thread is ever blocked (a native alert/confirm
    // dialog, a print dialog, anything else that steals the message loop), the awaited call
    // simply never returns. This hard per-call timeout is the actual backstop: whatever the
    // cause, a single tool call can now never wedge the whole run forever.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> EvalAsync(string script, CancellationToken ct)
    {
        var raw = await WithTimeout(core.ExecuteScriptAsync(script), "ExecuteScriptAsync", ct);
        // ExecuteScriptAsync JSON-encodes the JS expression's result. Every tool script returns
        // JSON.stringify(...) (a JS string), so `raw` is a JSON-encoded STRING — unwrap that one
        // level so callers get the plain JSON text they expect to re-parse.
        return JsonSerializer.Deserialize<string>(raw) ?? raw;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string what, CancellationToken ct)
    {
        var winner = await Task.WhenAny(task, Task.Delay(CallTimeout, ct));
        if (winner != task)
        {
            // A fired cancellation token also makes the delay win — surface the cancel as a
            // cancel, not as a fake "page is unresponsive" timeout.
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{what} did not respond within {CallTimeout.TotalSeconds}s — the page may be showing a blocking dialog or is otherwise unresponsive.");
        }
        return await task;
    }

    public Task InjectFileAsync(string filePath, string selector, CancellationToken ct)
        => DomFileInjector.InjectAsync(core, filePath, selector);

    /// <summary>
    /// Dispatches a REAL mouse click at a viewport point via CDP Input.dispatchMouseEvent —
    /// distinct from calling .click() on an element in JS. Some component libraries specifically
    /// ignore untrusted synthetic click events; a CDP-dispatched mouse event is a genuine trusted
    /// input event indistinguishable from a real user click.
    /// </summary>
    public async Task ClickAtPointAsync(double x, double y, CancellationToken ct)
    {
        var pressParams = JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pressParams), "CallDevToolsProtocolMethodAsync(mousePressed)", ct);
        var releaseParams = JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
        await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", releaseParams), "CallDevToolsProtocolMethodAsync(mouseReleased)", ct);
    }

    /// <summary>
    /// Types each character via CDP Input.dispatchKeyEvent (keyDown carrying the character in
    /// its "text" field, then keyUp) — the same real-trusted-input technique as
    /// <see cref="ClickAtPointAsync"/>, applied to the keyboard. Targets whatever element
    /// currently has focus — the caller must focus the field first.
    /// </summary>
    /// <summary>
    /// A full Enter press with the virtual key code populated — pages listening for
    /// keydown keyCode 13 (search boxes, Enter-to-submit forms) only react to this shape,
    /// not to a bare text-carrying key event.
    /// </summary>
    public async Task PressEnterAsync(CancellationToken ct)
    {
        var down = JsonSerializer.Serialize(new
        {
            type = "keyDown",
            windowsVirtualKeyCode = 13,
            nativeVirtualKeyCode = 13,
            key = "Enter",
            code = "Enter",
            text = "\r",
            unmodifiedText = "\r",
        });
        await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", down), "CallDevToolsProtocolMethodAsync(Enter keyDown)", ct);
        var up = JsonSerializer.Serialize(new
        {
            type = "keyUp",
            windowsVirtualKeyCode = 13,
            nativeVirtualKeyCode = 13,
            key = "Enter",
            code = "Enter",
        });
        await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", up), "CallDevToolsProtocolMethodAsync(Enter keyUp)", ct);
    }

    public async Task TypeTextAsync(string text, CancellationToken ct)
    {
        foreach (var c in text)
        {
            var keyDownParams = JsonSerializer.Serialize(new { type = "keyDown", text = c.ToString(), unmodifiedText = c.ToString() });
            await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDownParams), "CallDevToolsProtocolMethodAsync(keyDown)", ct);
            var keyUpParams = JsonSerializer.Serialize(new { type = "keyUp", text = c.ToString(), unmodifiedText = c.ToString() });
            await WithTimeout(core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUpParams), "CallDevToolsProtocolMethodAsync(keyUp)", ct);
            await Task.Delay(40, ct);
        }
    }
}

using System.Text.Json;
using Automata.Core.Automation;
using Automata.Core.Operator;
using Microsoft.Web.WebView2.Core;

namespace Automata.Browser;

/// <summary>Real implementation of <see cref="IBrowserSurface"/> against a live WebView2 pane.
/// Ported from Prose.KdpPublish's <c>WebView2KdpBrowser</c>.</summary>
public class WebView2BrowserSurface : IBrowserSurface
{
    private readonly CoreWebView2 core;

    /// <summary>
    /// Applies the browser's zoom factor. Supplied by whoever owns the controller, because the
    /// zoom lives on the CONTROLLER and this type only holds the CoreWebView2 — and because the
    /// two owners reach it differently: the app has to hop to its UI thread, the runner does not.
    /// Null for a surface with no controller behind it, which then cannot zoom and says so.
    /// </summary>
    private readonly Action<double>? setZoom;

    public WebView2BrowserSurface(CoreWebView2 core, Action<double>? setZoom = null)
    {
        this.core = core;
        this.setZoom = setZoom;
        // Lazy, and shared: whichever call needs the page first pays for the registration, and every
        // call after it awaits the same completed task.
        install = new Lazy<Task>(() => core.AddScriptToExecuteOnDocumentCreatedAsync(
            AutomationScripts.DocumentStartJs));
    }

    private readonly Lazy<Task> install;

    /// <summary>
    /// Registers the toolkit to run at document-creation time, in this page and every frame inside
    /// it. Idempotent, and awaited by every call that touches the page.
    /// <para>
    /// Two things depend on being there BEFORE the page's own script, and only one of them is
    /// obvious. The closed-shadow-root registry can only see a root at the instant it is created, so
    /// arriving late means arriving after every root the page built on startup. Less obvious: a
    /// CROSS-ORIGIN frame can only be reached by a script that is already inside it, and nothing out
    /// here can put one there afterwards — WebView2 applies a document-created script to child
    /// frames, which is the entire reason reaching into one is possible at all.
    /// </para>
    /// <para>
    /// Callers that drive the page themselves — a WPF host with its own address bar — should await
    /// this once at startup rather than rely on the first replay step to trigger it.
    /// </para>
    /// </summary>
    public Task EnsureInstalledAsync(CancellationToken ct = default) => install.Value;

    public string CurrentUrl => core.Source;

    // Navigations get a longer leash than script calls — a cold page over a slow connection can
    // legitimately take a while, and the caller (replay engine) budgets per-step anyway.
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;

        // A previous navigation (e.g. a redirect the last step's action triggered) can still be
        // in flight when this call subscribes — without matching NavigationId, its eventual
        // NavigationCompleted would resolve `tcs` for the WRONG navigation, letting the caller
        // proceed while THIS url is still loading. NavigationStarting fires synchronously from
        // Navigate() below, so it's always seen before any NavigationCompleted for this call.
        void StartingHandler(object? sender, CoreWebView2NavigationStartingEventArgs e) => navigationId ??= e.NavigationId;
        void CompletedHandler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId == e.NavigationId) tcs.TrySetResult();
        }

        core.NavigationStarting += StartingHandler;
        core.NavigationCompleted += CompletedHandler;
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
            core.NavigationStarting -= StartingHandler;
            core.NavigationCompleted -= CompletedHandler;
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
        await EnsureInstalledAsync(ct);
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
    /// Zooms the page with the browser's OWN zoom — the controller's zoom factor, the same setting
    /// Ctrl+ and Ctrl- drive.
    /// <para>
    /// The two things that look easier both fail, and both were tried. CSS <c>zoom</c> on the root
    /// element does not move <c>getBoundingClientRect</c> in this Chromium, so the resolver would
    /// measure an element in one space and the click would be dispatched in another. And CDP's
    /// <c>Emulation.setDeviceMetricsOverride</c> is reverted the moment the DevTools session that
    /// set it detaches — which WebView2 does after every <c>CallDevToolsProtocolMethodAsync</c>, so
    /// the override survives long enough to be read back and no longer, giving a step that verifies
    /// itself and is wrong by the next one.
    /// </para>
    /// <para>
    /// Browser zoom has neither problem: it changes how many CSS pixels fit in the window, so
    /// element geometry and dispatched input coordinates stay in one space, and it persists across
    /// navigations because it belongs to the browser rather than to the document.
    /// </para>
    /// </summary>
    public async Task<double> SetZoomAsync(double factor, CancellationToken ct)
    {
        // A surface built without a controller cannot zoom. Reporting 1.0 lets the caller fail the
        // step with "asked for 60% but the page measured 100%", which is the truth.
        if (setZoom == null) return 1.0;

        setZoom(1.0);
        var native = await ViewportAsync(ct);
        if (Math.Abs(factor - 1.0) < 0.001 || native.Width <= 0) return 1.0;

        setZoom(factor);
        // What the window ended up holding, not what was asked for: zooming out fits more CSS
        // pixels across, so the ratio of the two viewports IS the zoom, measured by the page.
        var applied = await ViewportAsync(ct);
        return applied.Width <= 0 ? 1.0 : native.Width / applied.Width;
    }

    private async Task<(double Width, double Height)> ViewportAsync(CancellationToken ct)
    {
        var raw = await EvalAsync(
            "(function(){ return JSON.stringify({ w: window.innerWidth, h: window.innerHeight }); })()", ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return (doc.RootElement.GetProperty("w").GetDouble(), doc.RootElement.GetProperty("h").GetDouble());
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

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

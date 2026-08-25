namespace Automata.Core.Operator;

/// <summary>
/// The seam between the generic tool-calling engine/DOM toolkit in this assembly and whatever
/// actually hosts a live browser pane. Automata.Core stays browser-host-agnostic — it never
/// references WebView2 directly — the same way Prose.Core stayed agnostic behind IKdpBrowser.
/// Automata.App's <c>WebView2BrowserSurface</c> is the (currently only) real implementation.
/// </summary>
public interface IBrowserSurface
{
    /// <summary>The current page's URL.</summary>
    string CurrentUrl { get; }

    /// <summary>Navigate the pane to <paramref name="url"/> and complete when the navigation
    /// finishes (success or failure) — implementations must enforce their own timeout so a page
    /// that never fires navigation-completed can't hang a replay run forever.</summary>
    Task NavigateAsync(string url, CancellationToken ct);

    /// <summary>Evaluate JavaScript on the current page and return its result. Implementations
    /// are expected to enforce their own hard timeout — a blocked renderer (e.g. a native
    /// alert/confirm dialog) must never hang the caller forever.</summary>
    Task<string> EvalAsync(string script, CancellationToken ct);

    /// <summary>Attach a local file to a page's file input, matched by <paramref name="selector"/>
    /// (default: the first <c>input[type=file]</c> on the page), without ever opening a native
    /// OS file-picker dialog.</summary>
    Task InjectFileAsync(string filePath, string selector, CancellationToken ct);

    /// <summary>Dispatch a REAL, trusted mouse click at a viewport point — distinct from calling
    /// <c>.click()</c> on an element in JS. Required for custom ARIA widgets (e.g. a React
    /// <c>role=checkbox</c>/<c>role=radio</c> component) whose event handlers ignore synthetic,
    /// untrusted click events.</summary>
    Task ClickAtPointAsync(double x, double y, CancellationToken ct);

    /// <summary>Type text via real, trusted keystroke dispatch into whatever element currently
    /// has focus — the caller must focus the field first. Required for fields whose
    /// derived-value logic only fires off genuine keyboard events, not a value set via a
    /// property-setter + dispatched input/change events.</summary>
    Task TypeTextAsync(string text, CancellationToken ct);
}

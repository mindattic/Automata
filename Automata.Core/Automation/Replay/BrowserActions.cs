using System.Text.Json;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;

namespace Automata.Core.Automation.Replay;

public sealed record ValueReadback(bool Ok, string? Value, string? Error);
public sealed record CheckProbe(bool Ok, bool Checked, bool Native, double CenterX, double CenterY, string? Error);

/// <summary>
/// The shared "perform" half of browser interactions — the same mechanics the LLM tools proved
/// out (native-property-setter for React-controlled inputs, real CDP keystrokes for
/// derived-value fields, trusted point-clicks for ARIA widgets), reusable by both the tools and
/// the replay engine. Element-targeted methods act on <c>window.__automataLastResolved</c>, which
/// resolver.js sets on a successful resolve.
/// </summary>
public static class BrowserActions
{
    /// <summary>
    /// JS function <c>__automataApplyValue(el)</c>: sets a value the way a React-controlled input
    /// must be set — native property setter, then input/change events, then blur. A plain
    /// <c>el.value = x</c> never notifies the page's own state.
    /// </summary>
    public static string NativeSetterJsFunction(string valueJsLiteral) => $$"""
        function __automataApplyValue(el) {
            var proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
            var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
            setter.call(el, {{valueJsLiteral}});
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.blur();
        }
        """;

    /// <summary>
    /// Click-focus a field at its center, select-all, type via real CDP keystrokes, and read the
    /// resulting value back. The exact sequence (including settle delays) proven out by
    /// <c>TypeIntoFieldTool</c>.
    /// </summary>
    public static async Task<string?> TypeViaKeystrokesAsync(
        IBrowserSurface browser, double centerX, double centerY, string text, CancellationToken ct)
    {
        await browser.ClickAtPointAsync(centerX, centerY, ct);
        await Task.Delay(150, ct);
        await browser.EvalAsync("(function(){ var el = document.activeElement; if (el && el.select) el.select(); return 'ok'; })()", ct);
        await Task.Delay(100, ct);
        await browser.TypeTextAsync(text, ct);
        await Task.Delay(200, ct);

        var readBack = await browser.EvalAsync(
            "(function(){ var el = document.activeElement; return JSON.stringify({ value: el ? el.value : null }); })()", ct);
        using var doc = JsonDocument.Parse(readBack);
        return doc.RootElement.GetProperty("value").GetString();
    }

    /// <summary>Set the last-resolved element's value via the native setter; returns the read-back value.</summary>
    public static async Task<ValueReadback> SetValueOnResolvedAsync(
        IBrowserSurface browser, string value, CancellationToken ct)
    {
        var valueJs = JsonSerializer.Serialize(value);
        var raw = await EvalOnResolvedAsync(browser, $$"""
            {{NativeSetterJsFunction(valueJs)}}
            if (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA')
                return JSON.stringify({ ok: false, error: 'not a text input: ' + el.tagName });
            __automataApplyValue(el);
            return JSON.stringify({ ok: true, value: el.value });
            """, ct);
        return ParseReadback(raw);
    }

    /// <summary>Native JS click on the last-resolved element (native controls trust it).</summary>
    public static async Task<ValueReadback> ClickResolvedNativelyAsync(IBrowserSurface browser, CancellationToken ct)
    {
        var raw = await EvalOnResolvedAsync(browser,
            "el.click(); return JSON.stringify({ ok: true, value: null });", ct);
        return ParseReadback(raw);
    }

    /// <summary>Checked state + kind + center point of the last-resolved checkbox/radio.</summary>
    public static async Task<CheckProbe> ProbeResolvedCheckStateAsync(IBrowserSurface browser, CancellationToken ct)
    {
        var raw = await EvalOnResolvedAsync(browser, """
            var isNative = el.tagName === 'INPUT';
            var isChecked = isNative ? el.checked : el.getAttribute('aria-checked') === 'true';
            // The resolver's translated rect when it is there, because a widget inside an iframe
            // measures itself against that frame's viewport while the click below is dispatched
            // against the top document's.
            var rect = window.__automataViewportRect
                ? window.__automataViewportRect(el) : el.getBoundingClientRect();
            return JSON.stringify({ ok: true, checked: isChecked, native: isNative,
                centerX: rect.left + rect.width / 2, centerY: rect.top + rect.height / 2 });
            """, ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
                return new CheckProbe(false, false, false, 0, 0, GetError(root));
            return new CheckProbe(true,
                root.GetProperty("checked").GetBoolean(),
                root.GetProperty("native").GetBoolean(),
                root.GetProperty("centerX").GetDouble(),
                root.GetProperty("centerY").GetDouble(),
                null);
        }
        catch (JsonException)
        {
            return new CheckProbe(false, false, false, 0, 0, "unparseable probe result");
        }
    }

    /// <summary>
    /// Select the option whose visible text matches (exact first, then contains) on the
    /// last-resolved native &lt;select&gt;; dispatches input/change like a real pick.
    /// </summary>
    public static async Task<ValueReadback> SelectOptionOnResolvedAsync(
        IBrowserSurface browser, string optionText, CancellationToken ct)
    {
        var wantJs = JsonSerializer.Serialize(optionText);
        var raw = await EvalOnResolvedAsync(browser, $$"""
            if (el.tagName !== 'SELECT')
                return JSON.stringify({ ok: false, error: 'not a native select: ' + el.tagName });
            var want = {{wantJs}}.trim().toLowerCase();
            var exact = -1, fuzzy = -1;
            for (var i = 0; i < el.options.length; i++) {
                var t = (el.options[i].textContent || '').trim().toLowerCase();
                if (t === want) { exact = i; break; }
                if (fuzzy < 0 && t.indexOf(want) !== -1) fuzzy = i;
            }
            var idx = exact >= 0 ? exact : fuzzy;
            if (idx < 0) return JSON.stringify({ ok: false, error: 'no option matches ' + {{wantJs}} });
            el.selectedIndex = idx;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return JSON.stringify({ ok: true, value: (el.options[idx].textContent || '').trim() });
            """, ct);
        return ParseReadback(raw);
    }

    /// <summary>
    /// Zooms the page and confirms it actually changed size.
    /// <para>
    /// The confirmation is the point. <see cref="IBrowserSurface.SetZoomAsync"/> returns the factor
    /// the page measured for itself afterwards, and a zoom that did not take is a step that must
    /// fail here rather than one that passes and leaves the click after it landing on nothing.
    /// The tolerance is a rounding allowance: an emulated viewport is a whole number of pixels, so
    /// 1000px at 33% is 3030px and reads back as 0.3300330…, not 0.33.
    /// </para>
    /// </summary>
    public static async Task<ValueReadback> SetZoomAsync(
        IBrowserSurface browser, int percent, CancellationToken ct)
    {
        var wanted = percent / 100.0;
        double applied;
        try
        {
            applied = await browser.SetZoomAsync(wanted, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ValueReadback(false, null, $"the browser refused to zoom: {ex.Message}");
        }

        var measured = (int)Math.Round(applied * 100);
        return Math.Abs(applied - wanted) <= ZoomTolerance
            ? new ValueReadback(true, measured.ToString(), null)
            : new ValueReadback(false, null,
                $"asked for {percent}% but the page measured {measured}%");
    }

    /// <summary>How far a measured zoom may sit from the one asked for — one part in fifty, which
    /// covers the whole-pixel rounding of an emulated viewport and nothing more.</summary>
    private const double ZoomTolerance = 0.02;

    /// <summary>Read the last-resolved element's normalized text content.</summary>
    public static async Task<ValueReadback> ReadResolvedTextAsync(IBrowserSurface browser, CancellationToken ct)
    {
        var raw = await EvalOnResolvedAsync(browser, """
            var t = el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' ? el.value : el.textContent;
            return JSON.stringify({ ok: true, value: (t || '').replace(/\s+/g, ' ').trim() });
            """, ct);
        return ParseReadback(raw);
    }

    /// <summary>Read the last-resolved input's current value (post-condition check).</summary>
    public static async Task<ValueReadback> ReadResolvedValueAsync(IBrowserSurface browser, CancellationToken ct)
    {
        var raw = await EvalOnResolvedAsync(browser,
            "return JSON.stringify({ ok: true, value: el.value != null ? String(el.value) : null });", ct);
        return ParseReadback(raw);
    }

    /// <summary>Count of files attached to the last-resolved file input (-1 when unreadable).</summary>
    public static async Task<int> CountResolvedFilesAsync(IBrowserSurface browser, CancellationToken ct)
    {
        var raw = await EvalOnResolvedAsync(browser,
            "return JSON.stringify({ ok: true, value: el.files ? String(el.files.length) : '-1' });", ct);
        var readback = ParseReadback(raw);
        return readback.Ok && int.TryParse(readback.Value, out var n) ? n : -1;
    }

    /// <summary>
    /// Attach a local file to the last-resolved file input via CDP. The input is tagged with a
    /// temporary attribute so the injector's querySelector hits exactly the resolved element —
    /// the step's own selector may have matched only through a fallback strategy.
    /// </summary>
    public static async Task UploadToResolvedAsync(IBrowserSurface browser, string filePath, CancellationToken ct)
    {
        const string marker = "data-automata-upload";
        await EvalOnResolvedAsync(browser,
            $"el.setAttribute('{marker}', '1'); return JSON.stringify({{ ok: true, value: null }});", ct);
        try
        {
            await browser.InjectFileAsync(filePath, $"[{marker}]", ct);
        }
        finally
        {
            await browser.EvalAsync(
                $"(function(){{ var el = document.querySelector('[{marker}]'); if (el) el.removeAttribute('{marker}'); return 'ok'; }})()", ct);
        }
    }

    /// <summary>
    /// Poll the page-busy detector until it clears or <paramref name="capMs"/> elapses.
    /// Returns true when the page settled, false when the cap expired while still busy.
    /// </summary>
    public static async Task<bool> WaitForSettleAsync(
        IBrowserSurface browser, int capMs, int pollMs, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            var (isProcessing, _) = await BrowserFormHelpers.CheckIsProcessingAsync(
                browser, BrowserFormHelpers.DefaultProcessingWordsPattern, ct);
            if (!isProcessing) return true;
            if (Environment.TickCount64 - started + pollMs > capMs) return false;
            await Task.Delay(pollMs, ct);
        }
    }

    // ---- plumbing ----------------------------------------------------------------------------

    /// <summary>Run a JS body with <c>el</c> bound to the last-resolved element.</summary>
    private static Task<string> EvalOnResolvedAsync(IBrowserSurface browser, string bodyJs, CancellationToken ct)
        => browser.EvalAsync($$"""
            (function() {
                var el = window.__automataLastResolved;
                if (!el || !el.getBoundingClientRect)
                    return JSON.stringify({ ok: false, error: 'no element resolved' });
                {{bodyJs}}
            })()
            """, ct);

    private static ValueReadback ParseReadback(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new ValueReadback(ok, value, ok ? null : GetError(root));
        }
        catch (JsonException)
        {
            return new ValueReadback(false, null, "unparseable action result");
        }
    }

    private static string GetError(JsonElement root) =>
        root.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unknown error";
}

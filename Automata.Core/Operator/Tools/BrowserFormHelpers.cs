using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Shared checkbox-ticking and processing-state logic used by <see cref="CheckCheckboxTool"/>.
/// Generalized from Prose.KdpPublish's <c>KdpFormHelpers</c> — the mechanism (dual native-input/
/// custom-ARIA-widget handling, trusted-click fallback, a shared "is this page still doing
/// something server-side" detector) is fully site-agnostic; only KDP's specific confirmation
/// checkbox text and processing-word list were stripped out. Callers supply their own candidate
/// text and (optionally) their own processing-word list.
/// </summary>
public static class BrowserFormHelpers
{
    private const int MaxIterations = 10;

    /// <summary>Default words that commonly indicate a page is still doing server-side work
    /// (uploading, converting, submitting) as opposed to done/failed. Deliberately generic and
    /// small — callers with site-specific knowledge should pass their own list instead.</summary>
    public const string DefaultProcessingWordsPattern =
        "(loading|please wait|processing|submitting|uploading|saving)(?!\\s*(is\\s+)?(complete|completed|finished|done|successfully))";

    public sealed record TickResult(List<string> Matches, bool BlockedByProcessing, string? ProcessingIndicator);

    /// <summary>True (plus the matched snippet) if the page shows a visible, short status/progress
    /// element whose text matches <paramref name="processingWordsPattern"/>.</summary>
    public static Task<(bool IsProcessing, string? Indicator)> CheckIsProcessingAsync(
        BrowserOperatorContext ctx, string processingWordsPattern, CancellationToken ct)
        => CheckIsProcessingAsync(ctx.Browser, processingWordsPattern, ct);

    /// <summary>Surface-based overload so callers without a tool context (the replay engine's
    /// settle-wait) share the same page-busy detector.</summary>
    public static async Task<(bool IsProcessing, string? Indicator)> CheckIsProcessingAsync(
        IBrowserSurface browser, string processingWordsPattern, CancellationToken ct)
    {
        var result = await browser.EvalAsync(ProcessingCheckScript(processingWordsPattern), ct);
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        var isProcessing = root.GetProperty("isProcessing").GetBoolean();
        var indicator = isProcessing && root.TryGetProperty("indicator", out var el) ? el.GetString() : null;
        return (isProcessing, indicator);
    }

    /// <summary>Ticks every unchecked checkbox matching <paramref name="candidates"/> — both plain
    /// <c>&lt;input type=checkbox&gt;</c> and custom accessible <c>[role=checkbox]</c> widgets
    /// (some component libraries ignore synthetic/untrusted clicks, so widgets are ticked via a
    /// real trusted mouse click instead of a JS <c>.click()</c>). Refuses to click anything —
    /// returning <see cref="TickResult.BlockedByProcessing"/> instead — while the page still
    /// looks like it's doing server-side work.</summary>
    public static async Task<TickResult> TickMatchingCheckboxesAsync(
        BrowserOperatorContext ctx, string[] candidates, CancellationToken ct, string processingWordsPattern = DefaultProcessingWordsPattern)
    {
        var (isProcessing, indicator) = await CheckIsProcessingAsync(ctx, processingWordsPattern, ct);
        if (isProcessing) return new TickResult(new List<string>(), true, indicator);

        var candidatesJson = JsonSerializer.Serialize(candidates);
        var matches = new List<string>();

        // Process one match at a time rather than collecting all rects up front — clicking/
        // scrolling one element can shift page layout enough to invalidate other elements'
        // coordinates. Re-locating "the next unchecked match" after each click sidesteps that
        // entirely; capped at MaxIterations as a safety backstop against a click that didn't stick.
        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var locateResult = await ctx.Browser.EvalAsync(LocateNextScript(candidatesJson), ct);
            using var doc = JsonDocument.Parse(locateResult);
            var root = doc.RootElement;
            if (!root.GetProperty("found").GetBoolean()) break;

            var text = root.GetProperty("text").GetString() ?? "";
            var centerX = root.GetProperty("centerX").GetDouble();
            var centerY = root.GetProperty("centerY").GetDouble();

            await ctx.Browser.ClickAtPointAsync(centerX, centerY, ct);
            await Task.Delay(300, ct);
            matches.Add(text);
        }

        return new TickResult(matches, false, null);
    }

    // Scoped-elements-only, visible-only, length-capped: a whole-page text scan or a hidden-
    // element match reliably false-positives on static help text, hidden banner templates, and
    // permanent status chips that happen to contain a processing word without describing active
    // work — this narrower scan (short, specifically-classed, actually-rendered elements only)
    // does not.
    private static string ProcessingCheckScript(string processingWordsPattern) => $$"""
    (function() {
        function isVisible(el) {
            var r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
        }
        var processingWords = /{{processingWordsPattern}}/i;
        var processingEls = Array.from(document.querySelectorAll(
            '[class*="status"], [class*="progress"], [class*="spinner"], [class*="loading"], [class*="processing"], [class*="preparing"], [role="dialog"]'
        )).filter(isVisible)
          .map(function (el) { return (el.textContent || '').trim().replace(/\s+/g, ' '); })
          .filter(function (t) { return t.length > 0 && t.length < 200; });
        var matches = Array.from(new Set(processingEls.filter(function (t) { return processingWords.test(t); })));
        var isProcessing = matches.length > 0;
        var indicator = matches[0] || null;
        return JSON.stringify({ isProcessing: isProcessing, indicator: indicator });
    })()
    """;

    private static string LocateNextScript(string candidatesJson) => $$"""
    (function() {
        var candidates = {{candidatesJson}}.map(function (c) { return c.toLowerCase(); });

        // A checkbox's visible label isn't reliably in one specific place — real-world forms
        // wrap it in <label>, or use a <label for=id> sibling, or just put plain text as a
        // sibling/cousin with no label element at all. Walk up several ancestor levels AND
        // check immediate siblings, taking the first (nearest) text blob that contains the
        // candidate — nearer levels are strict subsets of farther ones for text content, so this
        // can't accidentally match the wrong checkbox.
        function candidateTexts(cb) {
            var texts = [];
            var label = cb.closest('label');
            if (label) texts.push(label.textContent);
            if (cb.id) {
                var forLabel = document.querySelector('label[for="' + cb.id + '"]');
                if (forLabel) texts.push(forLabel.textContent);
            }
            var node = cb;
            for (var depth = 0; depth < 5 && node.parentElement; depth++) {
                node = node.parentElement;
                texts.push(node.textContent);
            }
            if (cb.nextElementSibling) texts.push(cb.nextElementSibling.textContent);
            return texts.map(function (t) { return (t || '').trim().toLowerCase(); }).filter(Boolean);
        }

        function isChecked(el) {
            return el.tagName === 'INPUT' ? el.checked : el.getAttribute('aria-checked') === 'true';
        }

        // Match BOTH native <input type=checkbox> AND custom accessible <div role=checkbox>
        // widgets.
        var boxes = Array.from(document.querySelectorAll('input[type=checkbox], [role=checkbox]'));
        for (var i = 0; i < boxes.length; i++) {
            var cb = boxes[i];
            if (isChecked(cb)) continue;
            var texts = cb.tagName === 'INPUT' ? candidateTexts(cb) : [(cb.textContent || '').trim().toLowerCase()];
            for (var t = 0; t < texts.length; t++) {
                for (var j = 0; j < candidates.length; j++) {
                    if (texts[t].indexOf(candidates[j]) !== -1) {
                        cb.scrollIntoView({ block: 'center', inline: 'center' });
                        var rect = cb.getBoundingClientRect();
                        return JSON.stringify({
                            found: true,
                            text: texts[t].slice(0, 200),
                            centerX: rect.left + rect.width / 2,
                            centerY: rect.top + rect.height / 2
                        });
                    }
                }
            }
        }
        return JSON.stringify({ found: false });
    })()
    """;
}

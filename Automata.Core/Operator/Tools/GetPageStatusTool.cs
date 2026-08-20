using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Snapshots the current page's title, heading, URL, and any visible banner-like text
/// (success/error styling) so the LLM can decide whether a step actually completed — instead of
/// blind fixed-duration waits, the LLM calls this repeatedly until it sees the confirmation text
/// it's looking for. Ported from Prose.KdpPublish's <c>GetPageStatusTool</c>, with the KDP-tuned
/// processing-word list and titleId URL extraction removed (both were KDP-specific); the
/// "isProcessing" detection mechanism itself is retained via <see cref="BrowserFormHelpers"/>.
/// </summary>
public class GetPageStatusTool : IBrowserTool
{
    public string Name => "get_page_status";

    public string Description =>
        "Read the current page's title, main heading, URL, any visible banner/alert text " +
        "(success or error messages), and isProcessing — true if the page shows any sign it's " +
        "still doing server-side work (uploading, converting, submitting). Call this after an " +
        "action like upload_file or click_button to check whether the page finished before " +
        "moving to the next step. If isProcessing is true, wait and call get_page_status again " +
        "rather than assuming success or proceeding to check_checkbox.";

    public string ParametersJsonSchema => """{ "type": "object", "properties": {} }""";

    // Deliberately scoped-elements-only, visible-only, length-capped: a page commonly keeps
    // static help text, hidden banner templates, or permanent status chips that happen to
    // contain a banner-ish word without describing anything real. A short, specifically-classed,
    // actually-rendered element is far less likely to false-positive than a whole-page scan.
    private static string Script => $$"""
    (function() {
        function isVisible(el) {
            var r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
        }

        var banners = Array.from(document.querySelectorAll(
            '[class*="alert"], [class*="success"], [class*="error"], [class*="banner"], [role="alert"], [role="dialog"]'
        )).filter(isVisible)
          .map(function (el) { return (el.textContent || '').trim().replace(/\s+/g, ' '); })
          .filter(function (t) { return t.length > 0 && t.length < 400; });
        var uniqueBanners = Array.from(new Set(banners));

        var processingWords = /{{BrowserFormHelpers.DefaultProcessingWordsPattern}}/i;
        var processingEls = Array.from(document.querySelectorAll(
            '[class*="status"], [class*="progress"], [class*="spinner"], [class*="loading"], [class*="processing"], [class*="preparing"], [role="dialog"]'
        )).filter(isVisible)
          .map(function (el) { return (el.textContent || '').trim().replace(/\s+/g, ' '); })
          .filter(function (t) { return t.length > 0 && t.length < 200; });
        var processingMatches = Array.from(new Set(processingEls.filter(function (t) { return processingWords.test(t); })));
        var isProcessing = processingMatches.length > 0;

        var h1 = document.querySelector('h1, h2');
        return JSON.stringify({
            url: location.href,
            title: document.title,
            heading: h1 ? (h1.textContent || '').trim() : null,
            banners: uniqueBanners.slice(0, 8),
            isProcessing: isProcessing,
            processingIndicators: processingMatches.slice(0, 5),
        });
    })()
    """;

    public async Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct) =>
        await ctx.Browser.EvalAsync(Script, ct);
}

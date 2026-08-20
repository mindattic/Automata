using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Finds and clicks the first button/link/input whose visible text contains any of the given
/// candidate substrings — a site's exact button wording drifts across redesigns ("Save and
/// Continue" today, something else tomorrow), so matching by text is more resilient than a
/// guessed CSS selector the LLM has no way to verify in advance. Ported from Prose.KdpPublish's
/// <c>ClickButtonTool</c>, with the KDP-specific auto-tick of a confirmation checkbox removed —
/// use <see cref="CheckCheckboxTool"/> explicitly instead.
/// </summary>
public class ClickButtonTool : IBrowserTool
{
    public string Name => "click_button";

    public string Description =>
        "Find and click the first clickable element (button, link, or submit input) whose " +
        "visible text contains any of the given candidate phrases (case-insensitive). Pass " +
        "several plausible variants of the label you're looking for (e.g. [\"save and " +
        "continue\", \"next\"]) since exact wording can drift. Returns " +
        "{clicked:true, matchedText} or {clicked:false} if nothing matched.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "text_candidates": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Candidate substrings to match against visible text, e.g. [\"save and continue\"]."
        }
      },
      "required": ["text_candidates"]
    }
    """;

    public async Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct)
    {
        var candidates = args.GetProperty("text_candidates")
            .EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        if (candidates.Length == 0)
            return JsonSerializer.Serialize(new { error = "text_candidates was empty." });

        var candidatesJson = JsonSerializer.Serialize(candidates);
        var script = $$"""
        (function() {
            var candidates = {{candidatesJson}}.map(function (c) { return c.toLowerCase(); });

            function textOf(el) {
                // Icon-only buttons (e.g. a modal's "X" close button) commonly have NO visible
                // text at all — just an icon child rendered via CSS — so textContent is empty
                // and this element would never match "close"/"done" without falling back to
                // aria-label/title, which is where their accessible name actually lives.
                var direct = el.tagName === 'INPUT' ? (el.value || '') : (el.textContent || '');
                direct = direct.trim();
                if (direct) return direct.toLowerCase();
                var fallback = el.getAttribute('aria-label') || el.getAttribute('title') || '';
                return fallback.trim().toLowerCase();
            }
            function inFooterOrNav(el) {
                return !!el.closest('footer, nav, [class*="footer" i], [id*="footer" i], [class*="nav" i], [id*="nav" i]');
            }
            function tryMatch(elements) {
                for (var i = 0; i < elements.length; i++) {
                    var el = elements[i];
                    var text = textOf(el);
                    // A real action button's label is short — a long multi-sentence match (like a
                    // footer blurb that happens to contain the word "publish") is never the real
                    // target, no matter what tag it's on.
                    if (!text || text.length > 60) continue;
                    for (var j = 0; j < candidates.length; j++) {
                        if (text.indexOf(candidates[j]) !== -1) return { el: el, text: text };
                    }
                }
                return null;
            }

            // Pass 1: real form-action elements only (never <a> here).
            var found = tryMatch(document.querySelectorAll('button, input[type=submit], input[type=button]'));
            // Pass 2: links, but never ones living inside a footer/nav region.
            if (!found) {
                var links = Array.from(document.querySelectorAll('a')).filter(function (a) { return !inFooterOrNav(a); });
                found = tryMatch(links);
            }

            if (found) {
                found.el.click();
                return JSON.stringify({ clicked: true, matchedText: found.text });
            }
            return JSON.stringify({ clicked: false });
        })()
        """;

        var result = await ctx.Browser.EvalAsync(script, ct);

        // A click that navigates or triggers a client-side transition can leave a subsequent
        // read (get_page_status, etc.) looking at stale/pre-transition content. A short settle
        // delay after a real click gives an SPA a moment to start rendering the next state.
        if (result.Contains("\"clicked\":true"))
            await Task.Delay(1000, ct);

        return result;
    }
}

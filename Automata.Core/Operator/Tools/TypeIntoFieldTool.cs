using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Sets a field's value via REAL keystroke dispatch (<see cref="IBrowserSurface.TypeTextAsync"/>)
/// rather than the native-property-setter technique <see cref="SetFieldTool"/> uses — for the
/// rare field whose downstream derived-value logic (e.g. a price field that recalculates other
/// currencies) may only fire off genuine keyboard events, not a value that merely appears in the
/// input via a dispatched event. Ported from Prose.KdpPublish's KDP-specific <c>SetPriceTool</c>,
/// generalized to any labeled field.
/// </summary>
public class TypeIntoFieldTool : IBrowserTool
{
    public string Name => "type_into_field";

    public string Description =>
        "Set a field's value by REAL keystroke dispatch instead of set_field's value-setter " +
        "trick — use this when a field's own logic seems to depend on genuine typing (e.g. it " +
        "derives other on-page values only after real keystrokes, not after set_field). " +
        "Locates the field the same way set_field's label fallback does (searching for a label " +
        "matching label_text). Clears any existing value first, then types the given text " +
        "character by character. Returns {found:false} if no input near that label exists on " +
        "the current page.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "label_text": { "type": "string", "description": "Label text near the field, e.g. \"Amazon.com\"." },
        "text": { "type": "string", "description": "The text to type." }
      },
      "required": ["label_text", "text"]
    }
    """;

    public async Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct)
    {
        var labelText = args.GetProperty("label_text").GetString() ?? "";
        var text = args.GetProperty("text").GetString() ?? "";
        if (labelText.Length == 0 || text.Length == 0)
            return JsonSerializer.Serialize(new { error = "label_text and text are both required." });

        var labelJs = JsonSerializer.Serialize(labelText);
        var locateResult = await ctx.Browser.EvalAsync($$"""
        (function() {
            var query = {{labelJs}}.trim().toLowerCase();
            var labelEls = Array.from(document.querySelectorAll('label, span, div, legend, h5')).filter(function (el) {
                var t = (el.textContent || '').trim().toLowerCase();
                return t === query || t.indexOf(query) === 0;
            });
            for (var i = 0; i < labelEls.length; i++) {
                var label = labelEls[i];
                var input = null;
                if (label.htmlFor) {
                    var byFor = document.getElementById(label.htmlFor);
                    if (byFor && byFor.tagName === 'INPUT') input = byFor;
                }
                if (!input) {
                    var container = label.closest('div, form') || label.parentElement;
                    if (container) input = container.querySelector('input[type="text"], input[type="number"], input:not([type])');
                }
                if (input) {
                    input.scrollIntoView({ block: 'center', inline: 'center' });
                    var rect = input.getBoundingClientRect();
                    return JSON.stringify({ found: true, centerX: rect.left + rect.width / 2, centerY: rect.top + rect.height / 2 });
                }
            }
            return JSON.stringify({ found: false });
        })()
        """, ct);

        using var doc = JsonDocument.Parse(locateResult);
        if (!doc.RootElement.GetProperty("found").GetBoolean())
            return JsonSerializer.Serialize(new { found = false });

        var centerX = doc.RootElement.GetProperty("centerX").GetDouble();
        var centerY = doc.RootElement.GetProperty("centerY").GetDouble();

        // A real click both focuses the field AND (via its native text-input behavior) puts the
        // caret in it — then a real select-all + real typing replaces the existing value with
        // exactly what a human typing over it would produce.
        var value = await Automation.Replay.BrowserActions.TypeViaKeystrokesAsync(
            ctx.Browser, centerX, centerY, text, ct);
        return JsonSerializer.Serialize(new { found = true, typed = text, value });
    }
}

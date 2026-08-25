using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Sets a plain native text/number input's value, using the native-property-setter + dispatched
/// input/change events trick — required because a plain <c>input.value = x</c> assignment
/// doesn't notify a React-controlled (or similar) input's own state, so the page's own validation
/// never sees the change. <paramref name="field"/> is tried as a literal DOM id first, then as a
/// label-text substring match against the nearest input on the page. Ported from
/// Prose.KdpPublish's <c>SetFieldTool</c> with the KDP-specific known-id map removed — any field
/// not identified by a literal id can be found by its visible label instead.
/// </summary>
public class SetFieldTool : IBrowserTool
{
    public string Name => "set_field";

    public string Description =>
        "Set a plain text/number input's value on the current page. `field` is tried two ways " +
        "in order: (1) a literal DOM element id, (2) a label-text substring match " +
        "(case-insensitive) against the nearest input on the page — use this second form for " +
        "any field whose id you don't already know. Does NOT work for rich-text editor widgets " +
        "or for radio/checkbox controls (use select_form_option for those). Returns " +
        "{found:false, tried:[...]} if nothing matched by either strategy.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "field": { "type": "string", "description": "A literal element id, or label text near the field (e.g. \"List Price\")." },
        "value": { "type": "string" }
      },
      "required": ["field", "value"]
    }
    """;

    public async Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct)
    {
        var field = args.GetProperty("field").GetString() ?? "";
        var value = args.GetProperty("value").GetString() ?? "";
        if (field.Length == 0)
            return JsonSerializer.Serialize(new { error = "field was empty." });

        var idJs = JsonSerializer.Serialize(field);
        var labelJs = JsonSerializer.Serialize(field);
        var valueJs = JsonSerializer.Serialize(value);

        var script = $$"""
        (function() {
            {{Automation.Replay.BrowserActions.NativeSetterJsFunction(valueJs)}}
            var apply = __automataApplyValue;

            var byId = document.getElementById({{idJs}});
            if (byId) { apply(byId); return JSON.stringify({ found: true, strategy: 'id', elementId: {{idJs}}, value: byId.value }); }

            // Label-text fallback — walk to the nearest input after a matching label.
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
                    if (byFor && (byFor.tagName === 'INPUT' || byFor.tagName === 'TEXTAREA')) input = byFor;
                }
                if (!input) {
                    var container = label.closest('div, form') || label.parentElement;
                    if (container) input = container.querySelector('input[type="text"], input[type="number"], input:not([type]), textarea');
                }
                if (input) { apply(input); return JSON.stringify({ found: true, strategy: 'label', matchedLabel: label.textContent.trim(), value: input.value }); }
            }

            return JSON.stringify({ found: false, tried: [{{idJs}}, {{labelJs}} + ' (label search)'] });
        })()
        """;

        return await ctx.Browser.EvalAsync(script, ct);
    }
}

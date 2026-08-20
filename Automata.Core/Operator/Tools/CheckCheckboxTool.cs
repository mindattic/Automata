using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Finds and checks (ticks) every unchecked checkbox whose associated text contains any of the
/// given candidate phrases. Handles both plain <c>&lt;input type=checkbox&gt;</c> and custom
/// accessible widgets (<c>&lt;div role="checkbox" aria-checked="false"&gt;...&lt;/div&gt;</c>) —
/// confirmed live on KDP that some component libraries' React handlers ignore a synthetic
/// <c>.click()</c> on the latter shape, so widgets are ticked via a real trusted mouse click
/// instead. Ported from Prose.KdpPublish's <c>CheckCheckboxTool</c>.
/// </summary>
public class CheckCheckboxTool : IBrowserTool
{
    public string Name => "check_checkbox";

    public string Description =>
        "Find and check (tick) EVERY unchecked checkbox whose label text contains any of " +
        "the given candidate phrases (case-insensitive). Some pages repeat the same " +
        "confirmation checkbox more than once (e.g. once per section that changed) — this " +
        "tool ticks all matches in one call. Returns {checkedCount, matches:[matchedText,...]} " +
        "— checkedCount:0 means nothing matched or everything was already checked.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "text_candidates": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Candidate substrings to match against the checkbox's label text."
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

        var result = await BrowserFormHelpers.TickMatchingCheckboxesAsync(ctx, candidates, ct);
        if (result.BlockedByProcessing)
        {
            return JsonSerializer.Serialize(new
            {
                checkedCount = 0,
                blockedByProcessing = true,
                processingIndicator = result.ProcessingIndicator,
                hint = "The page still looks like it's doing server-side work — checkboxes are unreliable until this clears. Call get_page_status and wait, then retry.",
            });
        }

        return JsonSerializer.Serialize(new { checkedCount = result.Matches.Count, matches = result.Matches });
    }
}

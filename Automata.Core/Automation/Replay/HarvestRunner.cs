using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Operator;

namespace Automata.Core.Automation.Replay;

/// <summary>What a harvest read off the page, before any of it reaches a dataset.</summary>
public sealed record HarvestResult(
    bool Ok,
    IReadOnlyList<Dictionary<string, string>> Rows,
    int MatchedRows,
    IReadOnlyList<string> EmptyFields,
    string? Error);

/// <summary>
/// Runs a <see cref="HarvestSpec"/> against a live page and shapes what comes back.
/// <para>
/// The browser half is one <c>EvalAsync</c>; everything after it — de-duplication, the row cap,
/// and the checks that decide whether a harvest is trustworthy — is deliberately pure and lives in
/// <see cref="Shape"/>, so the rules that matter are unit-testable without a browser anywhere near
/// them.
/// </para>
/// </summary>
public static class HarvestRunner
{
    public static async Task<HarvestResult> RunAsync(
        IBrowserSurface browser, HarvestSpec spec, CancellationToken ct)
    {
        var invalid = Validate(spec);
        if (invalid != null) return Fail(invalid);

        var specJson = JsonSerializer.Serialize(
            new
            {
                itemSelector = spec.ItemSelector,
                fields = spec.Fields.Select(f => new
                {
                    name = f.Name,
                    selector = f.Selector,
                    source = f.Source.ToString().ToLowerInvariant(),
                    attributeName = f.AttributeName,
                }),
            },
            AutomataJson.Options);

        var script = $$"""
        (function() {
        {{AutomationScripts.HarvestJs}}
        return window.__automataHarvest({{specJson}});
        })()
        """;

        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(await browser.EvalAsync(script, ct), AutomataJson.Options); }
        catch (JsonException) { return Fail("the page returned something that was not a harvest"); }

        if (env == null) return Fail("the page returned nothing");
        if (!env.Ok)
        {
            return Fail(
                $"'{spec.ItemSelector}' matched nothing on this page" +
                (spec.ExpectedCount is int n ? $" (it matched {n} when the harvest was built)" : "") +
                " — the page has probably changed, or the run is on the wrong page");
        }

        return Shape(spec, env.Rows, env.EmptyFields);
    }

    /// <summary>
    /// De-duplicates, caps, and decides whether the result is honest enough to store.
    /// <para>
    /// A harvest that matched rows but filled no column is the dangerous case: it "succeeds", the
    /// dataset gets rows, and every one of them is blank — so a later ForEach loops the right
    /// number of times over nothing. It is refused here instead.
    /// </para>
    /// </summary>
    public static HarvestResult Shape(
        HarvestSpec spec,
        IReadOnlyList<Dictionary<string, string>>? rows,
        IReadOnlyList<string>? emptyFields)
    {
        rows ??= [];
        emptyFields ??= [];
        var matched = rows.Count;

        if (matched == 0) return Fail("no rows matched");

        if (emptyFields.Count == spec.Fields.Count && spec.Fields.Count > 0)
        {
            return new HarvestResult(false, [], matched, emptyFields,
                $"matched {matched} row(s) but every column came back empty — the field selectors no " +
                "longer point at anything inside a row");
        }

        var kept = new List<Dictionary<string, string>>();
        if (!string.IsNullOrWhiteSpace(spec.DedupeBy))
        {
            if (!spec.Fields.Any(f => f.Name == spec.DedupeBy))
            {
                return Fail($"cannot de-duplicate by '{spec.DedupeBy}' — the harvest has no such column");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = row.TryGetValue(spec.DedupeBy!, out var v) ? v : "";

                // A blank key is not a duplicate of another blank key: two rows whose id failed to
                // read are two unread rows, and collapsing them would hide the failure.
                if (key.Length > 0 && !seen.Add(key)) continue;
                kept.Add(row);
            }
        }
        else
        {
            kept.AddRange(rows);
        }

        if (spec.MaxRows is int max && max > 0 && kept.Count > max)
            kept = [.. kept.Take(max)];

        return new HarvestResult(true, kept, matched, emptyFields, null);
    }

    /// <summary>The stored spec's own coherence, checked before a page is even touched.</summary>
    public static string? Validate(HarvestSpec? spec)
    {
        if (spec == null) return "no harvest configured";
        if (string.IsNullOrWhiteSpace(spec.ItemSelector)) return "no rows chosen — pick one item on the page first";
        if (spec.Fields.Count == 0) return "no columns chosen — a harvest with no fields would write empty rows";
        if (string.IsNullOrWhiteSpace(spec.DatasetName)) return "no dataset named to write to";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in spec.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name)) return "a column has no name";
            if (!names.Add(field.Name)) return $"two columns are both called '{field.Name}'";
            if (field.Source == HarvestSource.Attribute && string.IsNullOrWhiteSpace(field.AttributeName))
                return $"column '{field.Name}' reads an attribute but no attribute is named";
        }
        return null;
    }

    private static HarvestResult Fail(string error) => new(false, [], 0, [], error);

    private sealed class Envelope
    {
        public bool Ok { get; set; }
        public int Count { get; set; }
        public List<Dictionary<string, string>>? Rows { get; set; }
        public List<string>? EmptyFields { get; set; }
        public string? Error { get; set; }
    }
}

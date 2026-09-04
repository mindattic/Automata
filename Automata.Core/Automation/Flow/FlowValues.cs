using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Flow;

/// <summary>
/// Shared value and target parsing for the Gherkin surface: how a quoted string becomes an element
/// fingerprint, and how a written value becomes a literal or a binding.
/// </summary>
public static class FlowValues
{
    /// <summary>
    /// A written target becomes a <b>partial</b> fingerprint, never a full one.
    /// <para>
    /// A feature file cannot invent what a recorder captured, and pretending otherwise would
    /// produce a brittle selector. Partial fingerprints are exactly what the resolver's tail
    /// strategies already handle (aria label → label text → visible text), and self-heal upgrades
    /// them to a precise identity on the first successful run — so an authored step gets more
    /// robust the first time it executes.
    /// </para>
    /// </summary>
    public static ElementFingerprint TargetFor(string written)
    {
        var text = written.Trim();

        if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("(/", StringComparison.Ordinal))
            return new ElementFingerprint { XPath = text };

        if (text.StartsWith('#') || text.StartsWith('.') || text.Contains('[') || text.Contains('>'))
            return new ElementFingerprint { CssSelector = text };

        // Plain words are a human description of the control, so offer the resolver every text
        // strategy it knows rather than betting on one.
        return new ElementFingerprint
        {
            AriaLabel = text,
            NearbyLabelText = text,
            VisibleText = text,
        };
    }

    /// <summary>True when a written value is an Examples placeholder, e.g. <c>&lt;sku&gt;</c>.</summary>
    public static bool IsPlaceholder(string written) =>
        written.Length > 2 && written[0] == '<' && written[^1] == '>';

    public static string PlaceholderName(string written) => written[1..^1].Trim();

    public static int DurationMs(int amount, string unit) => unit.ToLowerInvariant() switch
    {
        "ms" => amount,
        "s" => amount * 1000,
        "m" => amount * 60_000,
        "h" => amount * 3_600_000,
        _ => amount,
    };

    public static string ShortUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(unknown)";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.AbsolutePath is "/" or "" ? "" : uri.AbsolutePath)
            : url;
    }

    /// <summary>Renders a value back to how it would be written, for the decompiler.</summary>
    public static string Write(string? literal, BindingRef? binding)
    {
        if (binding == null) return Quote(literal);
        return binding.Kind switch
        {
            BindingKind.DatasetColumn => Quote("<" + binding.ColumnName + ">"),
            BindingKind.StepOutput => binding.OutputField ?? "",
            BindingKind.EnvVar => "env." + binding.EnvVarName,
            _ => Quote(binding.Literal),
        };
    }

    public static string Quote(string? text) => "\"" + (text ?? "").Replace("\"", "'") + "\"";
}

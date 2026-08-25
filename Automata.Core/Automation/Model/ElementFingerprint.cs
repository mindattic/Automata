namespace Automata.Core.Automation.Model;

/// <summary>
/// Multi-strategy identity of a DOM element, captured at record time. Replay resolves it against
/// the live DOM via a fixed path-of-least-resistance cascade (id → css → name → class → xpath →
/// aria → label → text), so the step survives markup churn as long as any one strategy still
/// uniquely identifies the element.
/// </summary>
public sealed class ElementFingerprint
{
    /// <summary>Element id attribute — omitted at capture time when it looks auto-generated.</summary>
    public string? Id { get; set; }

    /// <summary>Computed stable-ish CSS selector.</summary>
    public string? CssSelector { get; set; }

    /// <summary>Absolute positional XPath — the brittlest strategy, tried late.</summary>
    public string? XPath { get; set; }

    /// <summary>Lowercase tag name ("button", "input", …). Used to verify id/xpath hits.</summary>
    public string Tag { get; set; } = "";

    /// <summary>Class list filtered of utility/hashed classes.</summary>
    public List<string> ClassList { get; set; } = [];

    public string? NameAttr { get; set; }

    /// <summary>Input type attribute — used for candidate scoring.</summary>
    public string? TypeAttr { get; set; }

    /// <summary>Own visible text, trimmed, capped at 120 chars.</summary>
    public string? VisibleText { get; set; }

    /// <summary>Explicit or implicit ARIA role.</summary>
    public string? AriaRole { get; set; }

    /// <summary>aria-label, or the text of the aria-labelledby target.</summary>
    public string? AriaLabel { get; set; }

    /// <summary>Wrapping/for-associated label text, else nearest preceding label/legend/heading.</summary>
    public string? NearbyLabelText { get; set; }

    public string? Placeholder { get; set; }
}

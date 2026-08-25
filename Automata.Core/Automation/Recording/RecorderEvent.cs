using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Recording;

/// <summary>
/// One raw event out of the recording pipeline: DOM events posted by the injected recorder
/// script (click/input/change), plus host-side navigation events the App appends itself
/// (NavigationCompleted is more reliable than page-side unload hooks).
/// </summary>
public sealed class RecorderEvent
{
    /// <summary>"click" | "input" | "change" | "navigate".</summary>
    public string Kind { get; set; } = "";

    public ElementFingerprint? Fingerprint { get; set; }

    /// <summary>Element classification computed at capture time:
    /// "checkbox" | "radio" | "select" | "option" | "file" | "text" | "button" | "other".</summary>
    public string? TargetKind { get; set; }

    /// <summary>Input/change value; for a click on an option, the option's text.</summary>
    public string? Value { get; set; }

    public bool? Checked { get; set; }

    /// <summary>Selected option's visible text for change events on selects.</summary>
    public string? SelectedText { get; set; }

    /// <summary>True when the value was withheld at capture (password fields).</summary>
    public bool Masked { get; set; }

    /// <summary>Page URL at event time; the destination for navigate events.</summary>
    public string? Url { get; set; }

    /// <summary>Milliseconds-epoch timestamp (JS Date.now()).</summary>
    public long Ts { get; set; }
}

using System.Text.RegularExpressions;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Recording;

/// <summary>
/// Pure coalescer: turns the raw recorder event stream into an editable step list. Keystroke
/// bursts collapse into one TypeText, focus-clicks before typing disappear, checkbox toggles
/// collapse to the final state, dropdown-opening clicks vanish into the SelectOption they led to,
/// submit-looking clicks get auto-flagged as commit points. Re-run on the full event list after
/// every event, so the sidebar's live preview and the final Stop result are always identical.
/// </summary>
public static partial class RecorderSessionBuilder
{
    [GeneratedRegex(@"\b(submit|save|publish|purchase|place order|confirm|pay|send|apply|checkout)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CommitWords();

    private const int NavigateDedupeMs = 1000;

    public static List<Step> Build(IReadOnlyList<RecorderEvent> events)
    {
        var steps = new List<Step>();
        long lastNavTs = long.MinValue;
        string? lastNavUrl = null;

        foreach (var evt in events)
        {
            switch (evt.Kind)
            {
                case "navigate":
                    if (evt.Url == lastNavUrl && evt.Ts - lastNavTs <= NavigateDedupeMs) break;
                    lastNavUrl = evt.Url;
                    lastNavTs = evt.Ts;
                    steps.Add(new Step
                    {
                        Action = StepAction.Navigate,
                        Url = evt.Url,
                        Label = $"Go to {ShortUrl(evt.Url)}",
                    });
                    break;

                case "click":
                    OnClick(steps, evt);
                    break;

                case "input":
                    OnInput(steps, evt);
                    break;

                case "change":
                    OnChange(steps, evt);
                    break;

                case "key":
                    if (evt.Value == "Enter" && evt.Fingerprint != null)
                    {
                        steps.Add(new Step
                        {
                            Action = StepAction.PressEnter,
                            Target = evt.Fingerprint,
                            Label = $"Press Enter in {TargetName(evt.Fingerprint)}",
                        });
                    }
                    break;
            }
        }
        return steps;
    }

    private static void OnClick(List<Step> steps, RecorderEvent evt)
    {
        if (evt.Fingerprint == null) return;
        switch (evt.TargetKind)
        {
            case "select":
            case "file":
                // Opening a dropdown / a native file picker does nothing by itself — the
                // meaningful action arrives as the change event.
                return;

            case "checkbox":
                UpsertCheckState(steps, evt, evt.Checked ?? true);
                return;

            case "radio":
                UpsertRadio(steps, evt);
                return;

            case "option":
                UpsertSelectOption(steps, evt, evt.Value);
                return;

            default:
                var text = evt.Fingerprint.VisibleText ?? evt.Fingerprint.AriaLabel ?? TargetName(evt.Fingerprint);
                steps.Add(new Step
                {
                    Action = StepAction.Click,
                    Target = evt.Fingerprint,
                    Label = $"Click '{Truncate(text, 40)}'",
                    IsCommitPoint = LooksLikeCommit(evt.Fingerprint),
                });
                return;
        }
    }

    private static void OnInput(List<Step> steps, RecorderEvent evt)
    {
        if (evt.Fingerprint == null) return;
        var last = steps.LastOrDefault();

        // Keystroke burst on the same element → keep one TypeText carrying the final value.
        if (last is { Action: StepAction.TypeText } && SameElement(last.Target, evt.Fingerprint))
        {
            ApplyTypedValue(last, evt);
            return;
        }

        // The click that focused the field was just focus — the typing is the real step.
        if (last is { Action: StepAction.Click } && SameElement(last.Target, evt.Fingerprint))
            steps.RemoveAt(steps.Count - 1);

        var step = new Step { Action = StepAction.TypeText, Target = evt.Fingerprint };
        ApplyTypedValue(step, evt);
        steps.Add(step);
    }

    private static void OnChange(List<Step> steps, RecorderEvent evt)
    {
        if (evt.Fingerprint == null) return;
        switch (evt.TargetKind)
        {
            case "checkbox":
                UpsertCheckState(steps, evt, evt.Checked ?? false);
                return;

            case "radio":
                UpsertRadio(steps, evt);
                return;

            case "select":
                UpsertSelectOption(steps, evt, evt.SelectedText);
                return;

            case "file":
                steps.Add(new Step
                {
                    Action = StepAction.UploadFile,
                    Target = evt.Fingerprint,
                    Value = "",
                    Label = $"Upload file into {TargetName(evt.Fingerprint)}" +
                            (string.IsNullOrEmpty(evt.Value) ? "" : $" (recorded: {evt.Value} — set a local path)"),
                });
                return;

            default:
            {
                var last = steps.LastOrDefault();
                // The change that follows a typing burst just confirms the final value.
                if (last is { Action: StepAction.TypeText } && SameElement(last.Target, evt.Fingerprint))
                {
                    ApplyTypedValue(last, evt);
                    return;
                }
                // Enter fires keydown BEFORE the field's change event: look past a trailing
                // PressEnter so the change still folds into the typing burst behind it.
                if (last is { Action: StepAction.PressEnter } && steps.Count >= 2
                    && steps[^2] is { Action: StepAction.TypeText } typed
                    && SameElement(typed.Target, evt.Fingerprint))
                {
                    ApplyTypedValue(typed, evt);
                    return;
                }
                // A change with no typing behind it (autofill, paste via menu) → direct SetValue.
                steps.Add(new Step
                {
                    Action = StepAction.SetValue,
                    Target = evt.Fingerprint,
                    Value = evt.Masked ? "" : evt.Value,
                    Label = $"Set {TargetName(evt.Fingerprint)} to '{Truncate(evt.Masked ? "(masked)" : evt.Value, 30)}'",
                });
                return;
            }
        }
    }

    private static void UpsertCheckState(List<Step> steps, RecorderEvent evt, bool isChecked)
    {
        var action = isChecked ? StepAction.Check : StepAction.Uncheck;
        var label = $"{(isChecked ? "Check" : "Uncheck")} '{Truncate(TargetName(evt.Fingerprint!), 40)}'";
        var last = steps.LastOrDefault();
        // Click+change pairs on the same box, and toggle-toggle sequences, collapse to final state.
        if (last is { Action: StepAction.Check or StepAction.Uncheck } && SameElement(last.Target, evt.Fingerprint))
        {
            last.Action = action;
            last.Label = label;
            last.Target = evt.Fingerprint;
            return;
        }
        steps.Add(new Step { Action = action, Target = evt.Fingerprint, Label = label });
    }

    private static void UpsertRadio(List<Step> steps, RecorderEvent evt)
    {
        var last = steps.LastOrDefault();
        if (last is { Action: StepAction.SelectRadio } && SameElement(last.Target, evt.Fingerprint))
            return; // click+change pair on the same radio
        steps.Add(new Step
        {
            Action = StepAction.SelectRadio,
            Target = evt.Fingerprint,
            Label = $"Select '{Truncate(TargetName(evt.Fingerprint!), 40)}'",
        });
    }

    private static void UpsertSelectOption(List<Step> steps, RecorderEvent evt, string? optionText)
    {
        var last = steps.LastOrDefault();
        // An option click followed by the select's change event describes ONE pick.
        if (last is { Action: StepAction.SelectOption })
        {
            last.Value = optionText ?? last.Value;
            last.Label = $"Select '{Truncate(last.Value, 30)}' in {TargetName(last.Target ?? evt.Fingerprint!)}";
            return;
        }
        steps.Add(new Step
        {
            Action = StepAction.SelectOption,
            Target = evt.Fingerprint,
            Value = optionText,
            Label = $"Select '{Truncate(optionText, 30)}' in {TargetName(evt.Fingerprint!)}",
        });
    }

    private static void ApplyTypedValue(Step step, RecorderEvent evt)
    {
        step.Target = evt.Fingerprint;
        step.Value = evt.Masked ? "" : evt.Value;
        step.Label = evt.Masked
            ? $"Type (masked — fill in editor) into {TargetName(evt.Fingerprint!)}"
            : $"Type '{Truncate(evt.Value, 30)}' into {TargetName(evt.Fingerprint!)}";
    }

    private static bool LooksLikeCommit(ElementFingerprint fp) =>
        string.Equals(fp.TypeAttr, "submit", StringComparison.OrdinalIgnoreCase)
        || (fp.VisibleText != null && CommitWords().IsMatch(fp.VisibleText))
        || (fp.AriaLabel != null && CommitWords().IsMatch(fp.AriaLabel));

    private static bool SameElement(ElementFingerprint? a, ElementFingerprint? b)
    {
        if (a == null || b == null) return false;
        return Key(a) == Key(b);
    }

    private static string Key(ElementFingerprint fp) =>
        fp.CssSelector ?? fp.XPath ?? $"{fp.Tag}|{fp.Id}|{fp.NameAttr}";

    private static string TargetName(ElementFingerprint fp) =>
        fp.NearbyLabelText ?? fp.AriaLabel ?? fp.Placeholder ?? fp.NameAttr ?? fp.VisibleText ?? fp.Tag;

    private static string ShortUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(unknown)";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.AbsolutePath is "/" or "" ? "" : Truncate(uri.AbsolutePath, 30))
            : Truncate(url, 40);
    }

    private static string Truncate(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}

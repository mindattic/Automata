using Automata.Core.Automation.Model;
using Automata.Core.Automation.Recording;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RecorderCoalescingTests
{
    private static ElementFingerprint Field(string css, string? label = null, string? typeAttr = null,
        string? visibleText = null) => new()
    {
        Tag = "input",
        CssSelector = css,
        TypeAttr = typeAttr,
        NearbyLabelText = label,
        VisibleText = visibleText,
    };

    private static RecorderEvent Ev(string kind, ElementFingerprint? fp = null, string? targetKind = null,
        string? value = null, bool? isChecked = null, string? selectedText = null, string? url = null,
        long ts = 0, bool masked = false) => new()
    {
        Kind = kind,
        Fingerprint = fp,
        TargetKind = targetKind,
        Value = value,
        Checked = isChecked,
        SelectedText = selectedText,
        Url = url,
        Ts = ts,
        Masked = masked,
    };

    [Test]
    public void KeystrokeBurst_CoalescesToOneTypeText_WithFinalValue()
    {
        var q = Field("input[name=q]", label: "Search");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("input", q, "text", "c"),
            Ev("input", q, "text", "ca"),
            Ev("input", q, "text", "cat"),
            Ev("input", q, "text", "cats"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.TypeText));
        Assert.That(steps[0].Value, Is.EqualTo("cats"));
        Assert.That(steps[0].Label, Does.Contain("cats").And.Contain("Search"));
    }

    [Test]
    public void FocusClickBeforeTyping_IsDropped()
    {
        var q = Field("input[name=q]");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", q, "text"),
            Ev("input", q, "text", "hi"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.TypeText));
    }

    [Test]
    public void CheckboxToggleToggle_CollapsesToFinalState()
    {
        var cb = Field("#terms", label: "I agree");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", cb, "checkbox", isChecked: true),
            Ev("change", cb, "checkbox", isChecked: true),   // click+change pair
            Ev("click", cb, "checkbox", isChecked: false),   // toggled off again
            Ev("change", cb, "checkbox", isChecked: false),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.Uncheck));
        Assert.That(steps[0].Label, Does.Contain("I agree"));
    }

    [Test]
    public void RadioClickChangePair_YieldsSingleSelectRadio()
    {
        var radio = Field("#opt-yes", label: "Yes");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", radio, "radio", isChecked: true),
            Ev("change", radio, "radio", isChecked: true),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.SelectRadio));
    }

    [Test]
    public void SelectFlow_DropsOpeningClick_YieldsOneSelectOption()
    {
        var select = new ElementFingerprint { Tag = "select", CssSelector = "#country", NearbyLabelText = "Country" };
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", select, "select"),                                  // opening the dropdown
            Ev("change", select, "select", selectedText: "Canada"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.SelectOption));
        Assert.That(steps[0].Value, Is.EqualTo("Canada"));
        Assert.That(steps[0].Label, Does.Contain("Canada").And.Contain("Country"));
    }

    /// <summary>
    /// Two dropdowns filled one after the other are two picks. Collapsing on "the last step was a
    /// SelectOption" alone lost the first one entirely and left the survivor claiming the second
    /// dropdown's value under the first dropdown's name.
    /// </summary>
    [Test]
    public void TwoDifferentDropdowns_StayTwoSteps()
    {
        var country = new ElementFingerprint { Tag = "select", CssSelector = "#country", NearbyLabelText = "Country" };
        var province = new ElementFingerprint { Tag = "select", CssSelector = "#province", NearbyLabelText = "Province" };
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", country, "select"),
            Ev("change", country, "select", selectedText: "Canada"),
            Ev("click", province, "select"),
            Ev("change", province, "select", selectedText: "Ontario"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Value, Is.EqualTo("Canada"));
            Assert.That(steps[0].Target!.CssSelector, Is.EqualTo("#country"));
            Assert.That(steps[1].Value, Is.EqualTo("Ontario"));
            Assert.That(steps[1].Target!.CssSelector, Is.EqualTo("#province"));
        });
    }

    /// <summary>Changing one's mind about the SAME dropdown is still one step, at the final value.</summary>
    [Test]
    public void SameDropdownChangedTwice_CollapsesToTheFinalPick()
    {
        var select = new ElementFingerprint { Tag = "select", CssSelector = "#country", NearbyLabelText = "Country" };
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("change", select, "select", selectedText: "Canada"),
            Ev("change", select, "select", selectedText: "Mexico"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Value, Is.EqualTo("Mexico"));
    }

    /// <summary>
    /// A click on an option and the select's own change are one pick, and the step has to end up
    /// pointing at the SELECT: replaying one aimed at an &lt;option&gt; fails with "not a native
    /// select: OPTION".
    /// </summary>
    [Test]
    public void OptionClickThenSelectChange_TargetsTheSelect()
    {
        var option = new ElementFingerprint
        {
            Tag = "option", CssSelector = "#country > option:nth-of-type(2)", VisibleText = "Canada",
        };
        var select = new ElementFingerprint { Tag = "select", CssSelector = "#country", NearbyLabelText = "Country" };
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", option, "option", value: "Canada"),
            Ev("change", select, "select", selectedText: "Canada"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Action, Is.EqualTo(StepAction.SelectOption));
            Assert.That(steps[0].Value, Is.EqualTo("Canada"));
            Assert.That(steps[0].Target!.CssSelector, Is.EqualTo("#country"));
            Assert.That(steps[0].Label, Does.Contain("Country"));
        });
    }

    [Test]
    public void FileChange_BecomesUploadFileNeedingALocalPath()
    {
        var file = new ElementFingerprint { Tag = "input", TypeAttr = "file", CssSelector = "#upload", NearbyLabelText = "Manuscript" };
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", file, "file"),                       // opens the (native) picker — dropped
            Ev("change", file, "file", value: "book.docx"),
        ]);

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Action, Is.EqualTo(StepAction.UploadFile));
        Assert.That(steps[0].Value, Is.Empty);
        Assert.That(steps[0].Label, Does.Contain("book.docx").And.Contain("local path"));
    }

    [Test]
    public void SameUrlNavigationsWithinASecond_Dedupe()
    {
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("navigate", url: "https://x.example/", ts: 1000),
            Ev("navigate", url: "https://x.example/", ts: 1500),   // duplicate fire
            Ev("navigate", url: "https://x.example/page2", ts: 2000),
        ]);

        Assert.That(steps, Has.Count.EqualTo(2));
        Assert.That(steps.Select(s => s.Action), Has.All.EqualTo(StepAction.Navigate));
        Assert.That(steps[1].Url, Is.EqualTo("https://x.example/page2"));
    }

    [Test]
    public void SubmitLookingClicks_AutoFlagAsCommitPoints()
    {
        var submit = Field("input[name=btnK]", typeAttr: "submit", visibleText: "Google Search");
        var save = new ElementFingerprint { Tag = "button", CssSelector = "#save", VisibleText = "Save changes" };
        var plain = new ElementFingerprint { Tag = "button", CssSelector = "#more", VisibleText = "Show more" };

        var steps = RecorderSessionBuilder.Build(
        [
            Ev("click", submit, "button"),
            Ev("click", save, "button"),
            Ev("click", plain, "button"),
        ]);

        Assert.That(steps[0].IsCommitPoint, Is.True);   // type=submit
        Assert.That(steps[1].IsCommitPoint, Is.True);   // "Save changes"
        Assert.That(steps[2].IsCommitPoint, Is.False);
    }

    [Test]
    public void MaskedPasswordInput_RecordsEmptyValueWithEditorHint()
    {
        var pw = Field("#pw", label: "Password", typeAttr: "password");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("input", pw, "text", value: "", masked: true),
        ]);

        Assert.That(steps[0].Action, Is.EqualTo(StepAction.TypeText));
        Assert.That(steps[0].Value, Is.Empty);
        Assert.That(steps[0].Label, Does.Contain("masked"));
    }

    [Test]
    public void AutofillChangeWithoutTyping_BecomesSetValue()
    {
        var email = Field("#email", label: "Email");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("change", email, "text", value: "dave@gmail.com"),
        ]);

        Assert.That(steps[0].Action, Is.EqualTo(StepAction.SetValue));
        Assert.That(steps[0].Value, Is.EqualTo("dave@gmail.com"));
    }

    [Test]
    public void EnterKeydown_BecomesPressEnterStep_AfterTheTypingBurst()
    {
        var q = Field("textarea[name=q]", label: "Search");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("input", q, "text", "cats"),
            Ev("key", q, "text", value: "Enter"),
            Ev("change", q, "text", "cats"),   // change fires AFTER the Enter keydown
        ]);

        Assert.That(steps.Select(s => s.Action),
            Is.EqualTo(new[] { StepAction.TypeText, StepAction.PressEnter }));
        Assert.That(steps[0].Value, Is.EqualTo("cats"));   // change folded into the burst
        Assert.That(steps[1].Label, Does.Contain("Enter").And.Contain("Search"));
    }

    [Test]
    public void FullRecordingScenario_ProducesCleanStepList()
    {
        var q = Field("textarea[name=q]", label: "Search");
        var submit = Field("input[name=btnK]", typeAttr: "submit", visibleText: "Google Search");
        var steps = RecorderSessionBuilder.Build(
        [
            Ev("navigate", url: "https://www.google.com/", ts: 0),
            Ev("click", q, "text", ts: 500),
            Ev("input", q, "text", "c", ts: 600),
            Ev("input", q, "text", "ca", ts: 650),
            Ev("input", q, "text", "cats", ts: 700),
            Ev("change", q, "text", "cats", ts: 800),
            Ev("click", submit, "button", ts: 1200),
            Ev("navigate", url: "https://www.google.com/search?q=cats", ts: 1900),
        ]);

        Assert.That(steps.Select(s => s.Action), Is.EqualTo(new[]
        {
            StepAction.Navigate, StepAction.TypeText, StepAction.Click, StepAction.Navigate,
        }));
        Assert.That(steps[1].Value, Is.EqualTo("cats"));
        Assert.That(steps[2].IsCommitPoint, Is.True);
    }
}

using Automata.Core.Automation.Data;
using Automata.Core.Automation.Model;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Storage;

/// <summary>App-level user settings, editable from the sidebar's Settings section.</summary>
public sealed class AutomataSettings
{
    /// <summary>The themes the app ships, and the one place their names are spelled.</summary>
    public static class Themes
    {
        public const string Dark = "dark";
        public const string Light = "light";

        /// <summary>The name if it is one this app knows, and the default if it is not — an
        /// imported or hand-edited settings value must not be able to leave the panel unstyled.</summary>
        public static string Coerce(string? name) =>
            string.Equals(name, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
    }

    /// <summary>Which LLM drives the AI task / LLM-repair paths:
    /// "claude" | "openai" | "gemini" | "kimi". The others remain fallbacks in case the
    /// selected one has no usable credentials.</summary>
    public string Provider { get; set; } = "claude";

    // BYO keys themselves live in MindAttic.Vault, not here — see AutomationController's
    // ownKeys field and key handling. Each app gets its own Vault-backed override (so
    // testing a key in Automata never touches what another MindAttic app resolves), falling
    // back to the shared %APPDATA%\MindAttic\LLM\ keys.

    /// <summary>Corner rounding (px, 0–10) applied to the sidebar's buttons and inputs.</summary>
    public int BorderRadius { get; set; } = 5;

    /// <summary>
    /// <c>"dark"</c> or <c>"light"</c>. Dark is the default because it is the only look this app
    /// has ever had, and a stored preference should be the thing that changes it.
    /// <para>
    /// A string rather than a bool: the third value is coming — following the OS — and a
    /// <c>bool IsLight</c> would have to be replaced rather than extended when it does.
    /// </para>
    /// </summary>
    public string Theme { get; set; } = Themes.Dark;

    /// <summary>
    /// Width of the sidebar column in device-independent pixels, restored on launch and saved
    /// when the splitter is released or the window closes. Clamped to the MinWidth/MaxWidth on
    /// MainWindow's SidebarColumn, so a hand-edited value can never wedge the layout.
    /// </summary>
    public double SidebarWidth { get; set; } = 420;

    /// <summary>
    /// Whether the sidebar is living in its own window rather than docked beside the browser.
    /// <para>
    /// Restored on launch, so someone who works with the panel on a second monitor gets it back
    /// there without re-detaching every time.
    /// </para>
    /// </summary>
    public bool PanelDetached { get; set; }

    /// <summary>
    /// Where the detached sidebar window sat, in device-independent pixels. Null means "never
    /// placed" and the window centres — which is also where a position on a monitor that has since
    /// been unplugged degrades to, since the bounds are checked against the virtual screen before
    /// they are used.
    /// <para>
    /// Nullable rather than NaN, and this is not a style preference: <c>double.NaN</c> cannot be
    /// written as JSON at all (and the settings travel as JSON in a workspace export), so a NaN
    /// here threw on save and took the whole action that was saving with it. A value this type cannot serialise has
    /// no business being a default.
    /// </para>
    /// </summary>
    public double? PanelWindowLeft { get; set; }

    public double? PanelWindowTop { get; set; }

    public double PanelWindowWidth { get; set; } = 460;

    public double PanelWindowHeight { get; set; } = 900;

    /// <summary>
    /// The outermost scope of the engine settings chain (global -> collection -> task -> step).
    /// Null means every engine setting sits at its floor, which is the behavior the app had
    /// before scoped settings existed.
    /// </summary>
    public EngineSettingsOverride? EngineDefaults { get; set; }
}

/// <summary>
/// App settings: the single row of the <c>Settings</c> table (engine defaults as a complex type on
/// it). Read on every access — it is one small row — so a value saved in the sidebar takes effect
/// on the next run without a restart. API keys are not here; they live in MindAttic.Vault.
/// </summary>
public sealed class AutomataSettingsStore
{
    private const int RowId = 1;

    public AutomataSettingsStore(AutomataDatabase database) => Database = database;

    public AutomataDatabase Database { get; }

    /// <summary>Whether settings have ever been saved here (rather than being the defaults).</summary>
    public bool HasSaved
    {
        get
        {
            using var db = Database.CreateDbContext();
            return db.Settings.Any();
        }
    }

    public AutomataSettings Load()
    {
        using var db = Database.CreateDbContext();
        return db.Settings.AsNoTracking().FirstOrDefault() ?? new AutomataSettings();
    }

    public void Save(AutomataSettings settings)
    {
        // An override that overrides nothing is stored as no override at all.
        if (settings.EngineDefaults is { IsEmpty: true }) settings.EngineDefaults = null;

        using var db = Database.CreateDbContext();
        var exists = db.Settings.Any();
        var entry = db.Entry(settings);
        entry.Property(AutomataDb.SettingsKey).CurrentValue = RowId;
        entry.State = exists ? EntityState.Modified : EntityState.Added;
        db.SaveChanges();
    }
}

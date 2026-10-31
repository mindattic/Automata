using Automata.Core.Automation.Data;
using Automata.Core.Automation.Model;
using Microsoft.EntityFrameworkCore;
using MindAttic.Vault.Settings;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// The settings row: window placement (sidebar width, detached panel) owned by the app, plus the app-level
/// settings MindAttic Vault owns (<see cref="AutomataVaultSettings"/>), which <see cref="AutomataSettingsStore"/>
/// overlays on load. The row keeps every column so workspace exports and imports are unchanged.
/// </summary>
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

    // API keys are the shared MindAttic named keys (MindAttic.Vault.Desktop's Keys tab), never here;
    // see AutomataKeys.

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
/// App settings: MindAttic Vault's <see cref="AutomataVaultSettings"/> (provider, theme, border radius, global
/// engine defaults; edited in MindAttic.Vault.Desktop) overlaid on the single row of the <c>Settings</c> table,
/// which still holds the window placement and still travels in workspace exports. The first load under Vault
/// imports the row's values into Vault (the row is left as it was). Read on every access, so a value saved in
/// Vault takes effect on the next run without a restart; <see cref="Watch"/> also announces it to a running window.
/// API keys are not here; they are the shared MindAttic named keys (see <see cref="AutomataKeys"/>).
/// </summary>
public sealed class AutomataSettingsStore : IDisposable
{
    private const int RowId = 1;
    private bool seeded;
    private FileSystemWatcher? watcher;
    private Timer? debounce;

    /// <param name="database">The database whose row holds window state.</param>
    /// <param name="vault">Where the Vault values live; null picks <see cref="VaultFor"/>.</param>
    public AutomataSettingsStore(AutomataDatabase database, JsonSettingsStore<AutomataVaultSettings>? vault = null)
    {
        Database = database;
        Vault = vault ?? VaultFor(database);
    }

    public AutomataDatabase Database { get; }

    /// <summary>The Vault settings file this store overlays.</summary>
    public JsonSettingsStore<AutomataVaultSettings> Vault { get; }

    /// <summary>Raised (on a worker thread) after the Vault file changed on disk, e.g. a save in MindAttic.Vault.Desktop.</summary>
    public event Action? Changed;

    /// <summary>
    /// The real database gets the real Vault file (<c>%APPDATA%\MindAttic\Automata\settings.json</c>). A scratch
    /// database (tests, the UI harnesses' <c>AUTOMATA_DB_PATH</c>/<c>AUTOMATA_SETTINGS_PATH</c>) gets a Vault file
    /// beside it, so no harness or test can read or change the user's settings.
    /// </summary>
    public static JsonSettingsStore<AutomataVaultSettings> VaultFor(AutomataDatabase database) =>
        string.Equals(Path.GetFullPath(database.DatabasePath), Path.GetFullPath(AutomataDatabase.DefaultPath), StringComparison.OrdinalIgnoreCase)
            ? JsonSettingsStore<AutomataVaultSettings>.ForSettingsApp()
            : new JsonSettingsStore<AutomataVaultSettings>(Path.Combine(database.DataDirectory, "vault", AutomataVaultSettings.AppId));

    /// <summary>Whether settings have ever been saved here (rather than being the defaults): a database row, or
    /// Vault values that differ from the defaults.</summary>
    public bool HasSaved
    {
        get
        {
            using (var db = Database.CreateDbContext())
                if (db.Settings.Any()) return true;
            return Vault.Exists() && !Vault.Load().SameAs(new AutomataVaultSettings());
        }
    }

    public AutomataSettings Load()
    {
        AutomataSettings row;
        using (var db = Database.CreateDbContext())
            row = db.Settings.AsNoTracking().FirstOrDefault() ?? new AutomataSettings();
        VaultValues(row).ApplyTo(row);
        return row;
    }

    public void Save(AutomataSettings settings)
    {
        // An override that overrides nothing is stored as no override at all.
        if (settings.EngineDefaults is { IsEmpty: true }) settings.EngineDefaults = null;

        using (var db = Database.CreateDbContext())
        {
            var exists = db.Settings.Any();
            var entry = db.Entry(settings);
            entry.Property(AutomataDb.SettingsKey).CurrentValue = RowId;
            entry.State = exists ? EntityState.Modified : EntityState.Added;
            db.SaveChanges();
        }

        // Window-state saves leave the Vault file alone (no rewrite, no change event) unless a Vault value changed.
        var next = AutomataVaultSettings.From(settings);
        if (!Vault.Exists() || !next.SameAs(Vault.Load())) Vault.Save(next);
    }

    /// <summary>
    /// The Vault values; on the first call imports today's row into Vault when there is no Vault file yet, then
    /// seeds/writes the manifest (<see cref="JsonSettingsStore{T}.LoadOrSeed"/>).
    /// </summary>
    private AutomataVaultSettings VaultValues(AutomataSettings row)
    {
        if (seeded) return Vault.Load();
        try
        {
            if (!Vault.Exists()) Vault.Save(AutomataVaultSettings.From(row));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only profile still starts, on the row's values.
            return AutomataVaultSettings.From(row);
        }
        var values = Vault.LoadOrSeed();
        seeded = true;
        return values;
    }

    /// <summary>Starts raising <see cref="Changed"/> when the Vault file changes.</summary>
    public void Watch()
    {
        if (watcher is not null) return;
        Directory.CreateDirectory(Vault.Directory);
        watcher = new FileSystemWatcher(Vault.Directory, Vault.FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler changed = (_, _) => (debounce ??= new Timer(_ => Changed?.Invoke())).Change(300, Timeout.Infinite);
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (s, e) => changed(s, e);
        watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        watcher?.Dispose();
        debounce?.Dispose();
    }
}

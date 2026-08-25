using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>App-level user settings, editable from the sidebar's Settings section.</summary>
public sealed class AutomataSettings
{
    /// <summary>
    /// BYO-key: an Anthropic API key that OVERRIDES the default credential chain (Claude Code
    /// OAuth session → shared MindAttic credential store) for the AI task / LLM-repair paths —
    /// the escape hatch when the OAuth session is rate-limited or out of quota.
    /// Null/empty = use the default chain.
    /// </summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>Corner rounding (px, 0–10) applied to the sidebar's buttons and inputs.</summary>
    public int BorderRadius { get; set; } = 5;
}

/// <summary>
/// Tiny JSON settings file at %APPDATA%\MindAttic\Automata\settings.json. Read on every access
/// (it's one small file) so a key saved in the sidebar takes effect on the next run without a
/// restart. Note: stored as plain text in the user's roaming profile — same trust level as the
/// machine account itself.
/// </summary>
public sealed class AutomataSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MindAttic", "Automata", "settings.json");

    public string FilePath { get; }

    public AutomataSettingsStore(string? filePath = null) => FilePath = filePath ?? DefaultPath;

    public AutomataSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AutomataSettings();
            return JsonSerializer.Deserialize<AutomataSettings>(File.ReadAllText(FilePath), AutomataJson.Options)
                ?? new AutomataSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AutomataSettings();
        }
    }

    public void Save(AutomataSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, AutomataJson.Options));
    }
}

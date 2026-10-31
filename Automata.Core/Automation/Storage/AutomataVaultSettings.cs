using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Settings;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Settings.Manifest;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Automata's app-level settings in MindAttic Vault (<c>%APPDATA%\MindAttic\Automata\settings.json</c>), edited
/// only in MindAttic.Vault.Desktop (LAMP, AUT-T27). <see cref="AutomataSettingsStore"/> overlays them on the
/// database's settings row, which still holds the window placement and still travels in workspace exports.
/// API keys are the shared MindAttic named keys (Vault's Keys tab), assigned to Automata on its tab.
/// Per-collection/task/step engine overrides are workflow data and stay in the app.
/// </summary>
[SettingsApp(AppId, "Automata", Icon = "robot",
    Description = "Records, edits and replays browser tasks, with an LLM for free-text authoring and self-heal repair.")]
[UsesCredential(LlmProviderIds.Claude, Purpose = "Free-text task authoring and LLM repair (first choice by default)")]
[UsesCredential(LlmProviderIds.OpenAi, Purpose = "Fallback LLM")]
[UsesCredential(LlmProviderIds.Gemini, Purpose = "Fallback LLM")]
[UsesCredential(LlmProviderIds.Kimi, Purpose = "Fallback LLM")]
public sealed class AutomataVaultSettings
{
    public const string AppId = "Automata";

    [Setting("Use first", Group = "AI provider", Order = 1,
        Help = "The LLM that drives free-text authoring and LLM repair; the others are fallbacks when it has no usable key.")]
    [SettingChoices(LlmProviderIds.Claude, LlmProviderIds.OpenAi, LlmProviderIds.Gemini, LlmProviderIds.Kimi)]
    public string Provider { get; set; } = LlmProviderIds.Claude;

    [Setting("Theme", Group = "Appearance", Order = 1)]
    [SettingChoices(AutomataSettings.Themes.Dark, AutomataSettings.Themes.Light)]
    public string Theme { get; set; } = AutomataSettings.Themes.Dark;

    [Setting("Border radius (px)", Group = "Appearance", Order = 2, Help = "Corner rounding of the sidebar's buttons and inputs.")]
    [SettingRange(0, 10)]
    public int BorderRadius { get; set; } = 5;

    [Setting("Engine defaults", Group = "Engine defaults", Order = 1,
        Help = "The global scope of the engine settings chain (global, collection, task, step). Empty means the built-in default; collections, tasks and steps can still override each value in the app.")]
    public AutomataEngineDefaults EngineDefaults { get; set; } = new();

    /// <summary>The settings in the database row's terms (today's storage, and the shape exports carry).</summary>
    public static AutomataVaultSettings From(AutomataSettings row) => new()
    {
        Provider = row.Provider,
        Theme = row.Theme,
        BorderRadius = row.BorderRadius,
        EngineDefaults = AutomataEngineDefaults.From(row.EngineDefaults),
    };

    /// <summary>Overlays these values on the row, bounded the way the old Settings dialog bounded them.</summary>
    public void ApplyTo(AutomataSettings row)
    {
        row.Provider = LlmProviderIds.IsKnown(Provider) ? Provider.ToLowerInvariant() : LlmProviderIds.Claude;
        row.Theme = AutomataSettings.Themes.Coerce(Theme);
        row.BorderRadius = Math.Clamp(BorderRadius, 0, 10);
        row.EngineDefaults = (EngineDefaults ?? new()).ToOverride();
    }

    /// <summary>True when both hold the same values (compared as their JSON).</summary>
    public bool SameAs(AutomataVaultSettings other) => Json(this) == Json(other);

    private static string Json(AutomataVaultSettings s) =>
        JsonSerializer.Serialize(new { s.Provider, s.Theme, s.BorderRadius, EngineDefaults = s.EngineDefaults ?? new AutomataEngineDefaults() });

    /// <summary>What the sidebar's cog calls: MindAttic.Vault.Desktop at Automata's tab (optionally one setting).
    /// False when Vault.Desktop isn't installed or running.</summary>
    public static bool OpenInVault(string? setting = null) => VaultSettings.Open(AppId, setting);
}

/// <summary>The global engine defaults. Every value is optional: empty inherits the built-in default
/// (<see cref="EngineSettingsResolver.Floor"/>). Same JSON shape as <see cref="EngineSettingsOverride"/>.</summary>
public sealed class AutomataEngineDefaults
{
    [Setting("Step timeout (ms)", Group = "Engine defaults", Order = 2,
        Help = "Resolve/settle budget per step. Empty = 10000. A step's own timeout still wins.")]
    [SettingRange(0, 3600000)]
    public int? DefaultStepTimeoutMs { get; set; }

    [Setting("Self-heal selectors", Group = "Engine defaults", Order = 3,
        Help = "Re-find an element by its fingerprint when its selector stops matching. Empty = on.")]
    public bool? SelfHeal { get; set; }

    [Setting("Allow LLM repair", Group = "Engine defaults", Order = 4,
        Help = "Ask the LLM to repair a step that self-heal couldn't. Empty = off.")]
    public bool? AllowLlmRepair { get; set; }

    [Setting("Retry a failed step", Group = "Engine defaults", Order = 5,
        Help = "Empty = no retry (one attempt).")]
    public AutomataRetryDefaults? Retry { get; set; }

    [Setting("Continue after a failed step", Group = "Engine defaults", Order = 6,
        Help = "Keep running the remaining sibling steps after one fails. Empty = off.")]
    public bool? ContinueOnStepError { get; set; }

    [Setting("Continue after a failed task", Group = "Engine defaults", Order = 7,
        Help = "Keep running a collection's remaining tasks after one fails. Empty = on.")]
    public bool? ContinueOnTaskError { get; set; }

    [Setting("Browser profile", Group = "Engine defaults", Order = 8,
        Help = "Named browser profile: the same name shares cookies and logins. Empty = \"default\".")]
    public string? BrowserProfile { get; set; }

    [Setting("Screenshot on failure", Group = "Engine defaults", Order = 9, Help = "Empty = off.")]
    public bool? ScreenshotOnFailure { get; set; }

    [Setting("LLM provider for runs", Group = "Engine defaults", Order = 10,
        Help = "Overrides \"Use first\" for runs only. Empty = \"Use first\".")]
    [SettingChoices(LlmProviderIds.Claude, LlmProviderIds.OpenAi, LlmProviderIds.Gemini, LlmProviderIds.Kimi)]
    public string? LlmProvider { get; set; }

    public static AutomataEngineDefaults From(EngineSettingsOverride? o) => o is null ? new() : new()
    {
        DefaultStepTimeoutMs = o.DefaultStepTimeoutMs,
        SelfHeal = o.SelfHeal,
        AllowLlmRepair = o.AllowLlmRepair,
        Retry = o.Retry is null ? null : new AutomataRetryDefaults
        {
            MaxAttempts = o.Retry.MaxAttempts,
            DelayMs = o.Retry.DelayMs,
            BackoffMultiplier = o.Retry.BackoffMultiplier,
        },
        ContinueOnStepError = o.ContinueOnStepError,
        ContinueOnTaskError = o.ContinueOnTaskError,
        BrowserProfile = o.BrowserProfile,
        ScreenshotOnFailure = o.ScreenshotOnFailure,
        LlmProvider = o.LlmProvider,
    };

    /// <summary>The engine's override for the global scope; null when nothing is set (as the row stores it).</summary>
    public EngineSettingsOverride? ToOverride()
    {
        var o = new EngineSettingsOverride
        {
            DefaultStepTimeoutMs = DefaultStepTimeoutMs,
            SelfHeal = SelfHeal,
            AllowLlmRepair = AllowLlmRepair,
            Retry = Retry is null ? null : new RetryPolicy
            {
                MaxAttempts = Retry.MaxAttempts,
                DelayMs = Retry.DelayMs,
                BackoffMultiplier = Retry.BackoffMultiplier,
            },
            ContinueOnStepError = ContinueOnStepError,
            ContinueOnTaskError = ContinueOnTaskError,
            BrowserProfile = string.IsNullOrWhiteSpace(BrowserProfile) ? null : BrowserProfile,
            ScreenshotOnFailure = ScreenshotOnFailure,
            LlmProvider = string.IsNullOrWhiteSpace(LlmProvider) ? null : LlmProvider,
        };
        return o.IsEmpty ? null : o;
    }
}

/// <summary>The global retry policy; set as a whole (attempts and spacing are one decision).</summary>
public sealed class AutomataRetryDefaults
{
    [Setting("Retry: attempts", Group = "Engine defaults", Order = 1, Help = "Total attempts including the first. 1 = no retry.")]
    [SettingRange(1, 100)]
    public int MaxAttempts { get; set; } = 1;

    [Setting("Retry: delay between attempts (ms)", Group = "Engine defaults", Order = 2)]
    [SettingRange(0, 3600000)]
    public int DelayMs { get; set; } = 2000;

    [Setting("Retry: backoff multiplier", Group = "Engine defaults", Order = 3,
        Help = "Each further attempt waits this multiple of the previous delay. 1 = fixed.")]
    [SettingRange(0, 100)]
    public double BackoffMultiplier { get; set; } = 1.0;
}

/// <summary>The LLM providers Automata's roster knows, spelled once.</summary>
public static class LlmProviderIds
{
    public const string Claude = "claude";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Kimi = "kimi";

    public static IReadOnlyList<string> All { get; } = [Claude, OpenAi, Gemini, Kimi];

    public static bool IsKnown(string? id) => id is not null && All.Contains(id, StringComparer.OrdinalIgnoreCase);
}

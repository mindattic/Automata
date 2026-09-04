using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Settings;

/// <summary>
/// Flattens the global → collection → task → step override chain into one <see cref="ResolvedSettings"/>.
/// <para>
/// Two rules, and they differ on purpose:
/// </para>
/// <list type="bullet">
/// <item>Most settings are <b>deepest-wins</b> — the innermost scope that states a value decides it.</item>
/// <item><see cref="EngineSettingsOverride.MaxConcurrency"/> is <b>tighten-only</b> — the global
/// value is a ceiling and each deeper scope may lower it but never raise it.</item>
/// </list>
/// <para>
/// <see cref="Floor"/> is the contract that matters most: with nothing overridden anywhere, it
/// must reproduce the engine's behavior from before scoped settings existed. A regression test
/// pins every field of it.
/// </para>
/// </summary>
public static class EngineSettingsResolver
{
    public const int FloorStepTimeoutMs = 10_000;
    public const string FloorBrowserProfile = "default";

    /// <summary>
    /// Exactly today's behavior: a 10s step budget, self-heal on, LLM repair off, one attempt,
    /// a failed step aborts its task but a failed task does NOT abort its collection, one lane,
    /// the shared browser profile, and no screenshots.
    /// </summary>
    public static ResolvedSettings Floor() => new(
        DefaultStepTimeoutMs: FloorStepTimeoutMs,
        SelfHeal: true,
        AllowLlmRepair: false,
        Retry: new RetryPolicy(),
        // Asymmetric, and matching what the engine already did: ReplayEngine stops a task at the
        // first failed step, while RunCollectionAsync keeps going to the next task.
        ContinueOnStepError: false,
        ContinueOnTaskError: true,
        Isolation: FailureIsolation.IsolateLane,
        MaxConcurrency: 1,
        BrowserProfile: FloorBrowserProfile,
        ScreenshotOnFailure: false,
        LlmProvider: "claude");

    public static ResolvedSettings Resolve(
        AutomataSettings? global = null,
        EngineSettingsOverride? collection = null,
        EngineSettingsOverride? task = null,
        EngineSettingsOverride? step = null)
    {
        var floor = Floor();
        var globalScope = global?.EngineDefaults;
        var chain = new[] { globalScope, collection, task, step };

        T Deepest<T>(Func<EngineSettingsOverride, T?> get, T fallback) where T : struct
        {
            var value = fallback;
            foreach (var scope in chain)
                if (scope != null && get(scope) is { } v)
                    value = v;
            return value;
        }

        T DeepestRef<T>(Func<EngineSettingsOverride, T?> get, T fallback) where T : class
        {
            var value = fallback;
            foreach (var scope in chain)
                if (scope != null && get(scope) is { } v)
                    value = v;
            return value;
        }

        // Tighten-only: start at the global ceiling, then let deeper scopes lower it.
        var maxConcurrency = globalScope?.MaxConcurrency is int ceiling && ceiling > 0
            ? ceiling
            : floor.MaxConcurrency;
        foreach (var scope in new[] { collection, task, step })
            if (scope?.MaxConcurrency is int v && v > 0)
                maxConcurrency = Math.Min(maxConcurrency, v);

        var provider = DeepestRef(s => string.IsNullOrWhiteSpace(s.LlmProvider) ? null : s.LlmProvider,
            string.IsNullOrWhiteSpace(global?.Provider) ? floor.LlmProvider : global!.Provider);

        return floor with
        {
            DefaultStepTimeoutMs = Deepest(s => s.DefaultStepTimeoutMs is > 0 ? s.DefaultStepTimeoutMs : null,
                floor.DefaultStepTimeoutMs),
            SelfHeal = Deepest(s => s.SelfHeal, floor.SelfHeal),
            AllowLlmRepair = Deepest(s => s.AllowLlmRepair, floor.AllowLlmRepair),
            Retry = DeepestRef(s => s.Retry, floor.Retry),
            ContinueOnStepError = Deepest(s => s.ContinueOnStepError, floor.ContinueOnStepError),
            ContinueOnTaskError = Deepest(s => s.ContinueOnTaskError, floor.ContinueOnTaskError),
            Isolation = Deepest(s => s.Isolation, floor.Isolation),
            MaxConcurrency = maxConcurrency,
            BrowserProfile = DeepestRef(
                s => string.IsNullOrWhiteSpace(s.BrowserProfile) ? null : s.BrowserProfile,
                floor.BrowserProfile),
            ScreenshotOnFailure = Deepest(s => s.ScreenshotOnFailure, floor.ScreenshotOnFailure),
            LlmProvider = provider,
        };
    }
}

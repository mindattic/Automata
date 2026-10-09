using Automata.Core.Automation.Data;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MindAttic.Log;
using MindAttic.Log.Extensions;
using MindAttic.Vault.Credentials;
using AutoWebNav;

namespace Automata.Core.Extensions;

/// <summary>
/// Single registration point for the generic browser-automation engine — mirrors Prose.Core's
/// <c>AddProseServices</c> shape (one call, shared by every front end).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutomataCore(this IServiceCollection services)
    {
        // First MindAttic app migrated onto the shared pipeline (see MindAttic.Log's
        // docs/MIGRATION.md) — proves the app-owned-SQLite tier: the sink's MindAttic_Log table
        // lives in the SAME automata.db file EF already owns, not a second file, so AppData
        // backup/restore and AUTOMATA_DB_PATH overrides keep covering logs for free. The sink
        // only ever touches its own table (see MindAttic.Log's LOG-LAW-1), so this is safe
        // alongside EF's own migrations on the same file. Apps keep calling ILogger<T> — nothing
        // else in this method changes.
        services.AddMindAtticLog(o =>
        {
            o.Application = "Automata";
            o.Destination = LogDestination.Sqlite;
            o.SqlitePath = AutomataDatabase.ResolvePath();
        });

        // One SQLite database holds everything. Its path comes from AUTOMATA_DB_PATH (or sits
        // beside a harness's AUTOMATA_SETTINGS_PATH) so a test or the UI harness runs against a
        // scratch database instead of the developer's real one — see AutomataDatabase.
        services.AddSingleton(_ => new AutomataDatabase());
        services.AddSingleton<IDbContextFactory<AutomataDb>>(sp => sp.GetRequiredService<AutomataDatabase>());
        services.AddSingleton(sp => new AutomataSettingsStore(sp.GetRequiredService<AutomataDatabase>()));

        services.AddHttpClient();                    // generic factory for the LLM adapters
        services.AddHttpClient<AnthropicToolClient>();

        // BYO-key: every MindAttic app gets its OWN Vault-backed override — entering a key in
        // Automata's Settings writes to this app's own scoped id ("automata-<provider>"), never
        // to the shared id another app resolves — falling back to that shared
        // %APPDATA%\MindAttic\LLM\ key (Claude: OAuth session first, then the shared key) only
        // when this app has none of its own. Both tiers are read live per call, so saving a key
        // needs no restart.
        var ownKeys = new AppScopedCredentialStore("automata", LlmCredentialStore.Default);

        // Every provider's Settings UI stores a LIST of keys (one per line) so a user with
        // several accounts can rotate/fail over between them — this app's own key pool wins
        // outright over the shared fallback pool when it has any keys at all.
        Func<IReadOnlyList<string>> KeyPoolResolver(string providerId, Func<IReadOnlyList<string>> fallback) =>
            () =>
            {
                var ownPool = ownKeys.GetKeys(providerId).Select(k => k.Key).ToList();
                return ownPool.Count > 0 ? ownPool : fallback();
            };

        services.AddSingleton(sp => new AnthropicToolCallingLlm(
            sp.GetRequiredService<AnthropicToolClient>(),
            KeyPoolResolver("claude", AnthropicToolCallingLlm.DefaultResolveApiKeys)));

        // Multi-LLM Master Switch-Over: the roster orders the user's selected provider first
        // (live, per run) with the rest as fallbacks — first provider with credentials wins.
        // Kimi (Moonshot) is OpenAI-wire-compatible and reuses that adapter; Gemini needs its
        // own pathway (different function-calling format).
        services.AddSingleton<IReadOnlyList<IToolCallingLlm>>(sp =>
        {
            var settings = sp.GetRequiredService<AutomataSettingsStore>();
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var openAiLog = sp.GetRequiredService<ILogger<OpenAiToolCallingLlm>>();

            var openAi = new OpenAiToolCallingLlm(httpFactory.CreateClient("llm"), openAiLog,
                KeyPoolResolver("openai", () => LlmCredentialStore.Default.GetKeys("openai").Select(k => k.Key).ToList()));
            var kimi = new OpenAiToolCallingLlm(httpFactory.CreateClient("llm"), openAiLog,
                KeyPoolResolver("kimi", () => LlmCredentialStore.Default.GetKeys("kimi").Select(k => k.Key).ToList()),
                model: "kimi-latest", name: "Kimi", endpoint: "https://api.moonshot.ai/v1/chat/completions");
            var gemini = new GeminiToolCallingLlm(httpFactory.CreateClient("llm"),
                sp.GetRequiredService<ILogger<GeminiToolCallingLlm>>(),
                KeyPoolResolver("gemini", () => LlmCredentialStore.Default.GetKeys("gemini").Select(k => k.Key).ToList()));

            return new ProviderRoster(
            [
                ("claude", sp.GetRequiredService<AnthropicToolCallingLlm>()),
                ("openai", openAi),
                ("gemini", gemini),
                ("kimi", kimi),
            ], () => settings.Load().Provider);
        });

        services.AddSingleton<IBrowserTool, ClickButtonTool>();
        services.AddSingleton<IBrowserTool, CheckCheckboxTool>();
        services.AddSingleton<IBrowserTool, SelectFormOptionTool>();
        services.AddSingleton<IBrowserTool, SetFieldTool>();
        services.AddSingleton<IBrowserTool, TypeIntoFieldTool>();
        services.AddSingleton<IBrowserTool, UploadFileTool>();
        services.AddSingleton<IBrowserTool, GetPageStatusTool>();
        services.AddSingleton<IBrowserTool, LogNoteTool>();

        services.AddSingleton<BrowserToolRegistry>();
        services.AddSingleton<BrowserOperatorService>();

        services.AddSingleton(sp => new CollectionStore(
            sp.GetRequiredService<AutomataDatabase>(), sp.GetRequiredService<ILogger<CollectionStore>>()));
        services.AddSingleton(sp => new ArchiveService(
            sp.GetRequiredService<CollectionStore>(),
            sp.GetRequiredService<ILogger<ArchiveService>>(),
            sp.GetRequiredService<DatasetStore>(),
            sp.GetRequiredService<RunStore>(),
            sp.GetRequiredService<ScheduleStore>(),
            sp.GetRequiredService<ParkedRunStore>(),
            sp.GetRequiredService<AutomataSettingsStore>()));
        services.AddSingleton(sp =>
            new FingerprintResolver(sp.GetRequiredService<ILogger<FingerprintResolver>>()));
        services.AddSingleton(sp => new ReplayEngine(
            sp.GetRequiredService<FingerprintResolver>(),
            sp.GetRequiredService<BrowserOperatorService>(),   // last-resort LLM repair path
            sp.GetRequiredService<ILogger<ReplayEngine>>()));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp => new ScheduleStore(sp.GetRequiredService<AutomataDatabase>()));
        services.AddSingleton(sp => new DatasetStore(sp.GetRequiredService<AutomataDatabase>()));
        services.AddSingleton(sp => new RunStore(sp.GetRequiredService<AutomataDatabase>()));
        services.AddSingleton(sp => new ParkedRunStore(sp.GetRequiredService<AutomataDatabase>()));
        services.AddSingleton(sp => new DemoSeeder(
            sp.GetRequiredService<CollectionStore>(),
            demoRoot: Environment.GetEnvironmentVariable("AUTOMATA_DEMOS_ROOT"),
            // The roster example iterates a ragged JSON list, and a list is an asset rather than a
            // page — so the generator needs somewhere to put it.
            datasets: sp.GetRequiredService<DatasetStore>()));
        // The workflow engine wraps the replay engine rather than replacing it: it owns the tree
        // walk so control-flow steps can decide whether and how often their children run.
        services.AddSingleton(sp => new FlowAuthoringService(
            sp.GetRequiredService<IReadOnlyList<IToolCallingLlm>>(),
            sp.GetRequiredService<ILogger<FlowAuthoringService>>()));
        services.AddSingleton(sp => new WorkflowEngine(
            sp.GetRequiredService<ReplayEngine>(),
            sp.GetRequiredService<CollectionStore>(),
            sp.GetRequiredService<DatasetStore>(),
            sp.GetRequiredService<ILogger<WorkflowEngine>>()));

        return services;
    }

    /// <summary>
    /// Creates the database or brings it up to the current schema, then — once per database, on
    /// its first launch — imports the old <c>Documents\Automata</c> JSON files (which are left
    /// in place). Call once at startup, from every front door, before anything reads a store.
    /// </summary>
    public static LegacyImportReport MigrateAutomataDatabase(this IServiceProvider services)
    {
        var database = services.GetRequiredService<AutomataDatabase>();
        database.EnsureMigrated();
        var log = services.GetService<ILoggerFactory>()?.CreateLogger("Automata.Storage");
        return new LegacyWorkspaceImporter(database, LegacyLocations.FromEnvironment(), log).ImportOnce();
    }
}

using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MindAttic.Legion;

namespace Automata.Core.Extensions;

/// <summary>
/// Single registration point for the generic browser-automation engine — mirrors Prose.Core's
/// <c>AddProseServices</c> shape (one call, shared by every front end).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutomataCore(this IServiceCollection services)
    {
        // Every store here takes its root from an AUTOMATA_* variable so a test or the UI
        // harness can run against a scratch workspace instead of the developer's real one.
        services.AddSingleton(_ => new AutomataSettingsStore(
            filePath: Environment.GetEnvironmentVariable("AUTOMATA_SETTINGS_PATH")));

        services.AddHttpClient();                    // generic factory for the LLM adapters
        services.AddHttpClient<AnthropicToolClient>();

        // BYO-key: a key saved in the sidebar's Settings overrides that provider's default
        // credential chain (Claude: OAuth → credential store; others: the Vault key named
        // below). Resolvers run live per call, so saving a key needs no restart.
        static Func<string?> KeyResolver(
            AutomataSettingsStore store, Func<AutomataSettings, string?> byo, Func<string?> fallback) =>
            () =>
            {
                var key = byo(store.Load());
                return !string.IsNullOrWhiteSpace(key) ? key : fallback();
            };

        services.AddSingleton(sp => new AnthropicToolCallingLlm(
            sp.GetRequiredService<AnthropicToolClient>(),
            KeyResolver(sp.GetRequiredService<AutomataSettingsStore>(),
                s => s.AnthropicApiKey, AnthropicToolCallingLlm.DefaultResolveApiKey)));

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
                KeyResolver(settings, s => s.OpenAiApiKey, () => MindAtticCredentialStore.GetKey("openai")));
            var kimi = new OpenAiToolCallingLlm(httpFactory.CreateClient("llm"), openAiLog,
                KeyResolver(settings, s => s.KimiApiKey, () => MindAtticCredentialStore.GetKey("kimi")),
                model: "kimi-latest", name: "Kimi", endpoint: "https://api.moonshot.ai/v1/chat/completions");
            var gemini = new GeminiToolCallingLlm(httpFactory.CreateClient("llm"),
                sp.GetRequiredService<ILogger<GeminiToolCallingLlm>>(),
                KeyResolver(settings, s => s.GeminiApiKey, () => MindAtticCredentialStore.GetKey("gemini")));

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

        services.AddSingleton(sp =>
            new CollectionStore(
                rootPath: Environment.GetEnvironmentVariable("AUTOMATA_COLLECTIONS_ROOT"),
                sp.GetRequiredService<ILogger<CollectionStore>>()));
        services.AddSingleton(sp => new ArchiveService(
            sp.GetRequiredService<CollectionStore>(), sp.GetRequiredService<ILogger<ArchiveService>>()));
        services.AddSingleton(sp =>
            new FingerprintResolver(sp.GetRequiredService<ILogger<FingerprintResolver>>()));
        services.AddSingleton(sp => new ReplayEngine(
            sp.GetRequiredService<FingerprintResolver>(),
            sp.GetRequiredService<BrowserOperatorService>(),   // last-resort LLM repair path
            sp.GetRequiredService<ILogger<ReplayEngine>>()));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => new ScheduleStore(
            filePath: Environment.GetEnvironmentVariable("AUTOMATA_SCHEDULE_PATH")));
        services.AddSingleton(_ => new DatasetStore(
            rootPath: Environment.GetEnvironmentVariable("AUTOMATA_DATASETS_ROOT")));
        services.AddSingleton(_ => new RunStore(
            rootPath: Environment.GetEnvironmentVariable("AUTOMATA_RUNS_ROOT")));
        services.AddSingleton(_ => new ParkedRunStore(
            rootPath: Environment.GetEnvironmentVariable("AUTOMATA_PARKED_ROOT")));
        services.AddSingleton(_ => new LiveLaneStore(
            rootPath: Environment.GetEnvironmentVariable("AUTOMATA_LIVE_ROOT")));
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
}

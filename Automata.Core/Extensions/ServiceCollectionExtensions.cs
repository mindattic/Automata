using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Automata.Core.Extensions;

/// <summary>
/// Single registration point for the generic browser-automation engine — mirrors Prose.Core's
/// <c>AddProseServices</c> shape (one call, shared by every front end).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutomataCore(this IServiceCollection services)
    {
        services.AddHttpClient<AnthropicToolClient>();
        services.AddSingleton<AnthropicToolCallingLlm>();

        services.AddHttpClient<OpenAiToolCallingLlm>();

        // Multi-LLM Master Switch-Over: tried in preference order, first configured provider wins.
        services.AddSingleton<IReadOnlyList<IToolCallingLlm>>(sp => new List<IToolCallingLlm>
        {
            sp.GetRequiredService<AnthropicToolCallingLlm>(),
            sp.GetRequiredService<OpenAiToolCallingLlm>(),
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
            new CollectionStore(rootPath: null, sp.GetRequiredService<ILogger<CollectionStore>>()));
        services.AddSingleton(sp => new ArchiveService(
            sp.GetRequiredService<CollectionStore>(), sp.GetRequiredService<ILogger<ArchiveService>>()));
        services.AddSingleton(sp =>
            new FingerprintResolver(sp.GetRequiredService<ILogger<FingerprintResolver>>()));
        services.AddSingleton(sp => new ReplayEngine(
            sp.GetRequiredService<FingerprintResolver>(), sp.GetRequiredService<ILogger<ReplayEngine>>()));

        return services;
    }
}

using Automata.Browser;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Automata.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Automata.Runner;

/// <summary>
/// Runs Automata tasks without the desktop app.
/// <para>
/// A thin host on purpose: every decision lives in <see cref="RunnerCliDispatcher"/> over in
/// Automata.Core, which is plain net10.0 and therefore unit-testable. This file only assembles the
/// dependencies and hands over — the same split the WPF app uses.
/// </para>
/// <para>
/// Lanes are off-screen WebView2 windows, which need an interactive desktop: WebView2 cannot render
/// in Windows session 0, so a scheduled task must be registered to run only when the user is logged
/// on.
/// </para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddAutomataCore();
        using var provider = services.BuildServiceProvider();

        var profileRoot = Environment.GetEnvironmentVariable("AUTOMATA_LANE_PROFILE_ROOT")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MindAttic", "Automata", "Lanes");

        var dispatcher = new RunnerCliDispatcher(
            provider.GetRequiredService<CollectionStore>(),
            provider.GetRequiredService<RunStore>(),
            provider.GetRequiredService<WorkflowEngine>(),
            provider.GetRequiredService<AutomataSettingsStore>(),
            new OffscreenWebView2LaneFactory(profileRoot),
            Console.Out,
            provider.GetRequiredService<ScheduleStore>(),
            provider.GetRequiredService<IClock>(),
            new SchTasksRegistrar(Environment.ProcessPath ?? "automata-runner.exe"),
            provider.GetRequiredService<ParkedRunStore>(),
            provider.GetRequiredService<LiveLaneStore>());

        using var cancelling = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // let the run unwind and write its summary
            cancelling.Cancel();
        };

        return await dispatcher.DispatchAsync(args, cancelling.Token);
    }
}

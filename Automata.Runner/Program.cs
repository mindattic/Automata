using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Automata.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using AutoWebNav;
using AutoWebNav.WebView2;

namespace Automata.Runner;

/// <summary>
/// Runs Automata tasks without the desktop app.
/// <para>
/// A thin host on purpose: every decision lives in <see cref="RunnerCliDispatcher"/> over in
/// Automata.Core, which is plain net10.0 and therefore unit-testable. This file only assembles the
/// dependencies and hands over — the same split the WPF app uses.
/// </para>
/// <para>
/// The browser is an off-screen WebView2 window, which needs an interactive desktop: WebView2
/// cannot render in Windows session 0, so a scheduled task must be registered to run only when the
/// user is logged on.
/// </para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            // Windows Task Scheduler runs this fresh on every tick with no debugger and no
            // console attached to read a message from: left unhandled, a startup failure (e.g.
            // the database write below hitting a full disk) kills the process with a WER crash
            // popup and an Application-log entry instead of a line anyone can act on. Logging and
            // exiting cleanly means the next scheduled tick just tries again on its own.
            var logPath = Path.Combine(Path.GetTempPath(), "automata-runner-error.log");
            try { File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n\n"); } catch { /* best effort */ }
            Console.Error.WriteLine($"error: {ex.Message} (details logged to {logPath})");
            return RunnerExitCode.Fault;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var services = new ServiceCollection();
        services.AddAutomataCore();
        using var provider = services.BuildServiceProvider();

        // Schema first, and on a brand-new database the one-time move off the old JSON files —
        // whichever of the app and the runner starts first does it, and only once.
        var imported = provider.MigrateAutomataDatabase();
        if (imported.Ran) Console.Error.WriteLine(imported.Describe());

        var profileRoot = Environment.GetEnvironmentVariable("AUTOMATA_BROWSER_PROFILE_ROOT")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MindAttic", "Automata", "Browsers");

        var dispatcher = new RunnerCliDispatcher(
            provider.GetRequiredService<CollectionStore>(),
            provider.GetRequiredService<RunStore>(),
            provider.GetRequiredService<WorkflowEngine>(),
            provider.GetRequiredService<AutomataSettingsStore>(),
            new OffscreenWebView2Factory(profileRoot),
            Console.Out,
            provider.GetRequiredService<ScheduleStore>(),
            provider.GetRequiredService<IClock>(),
            new SchTasksRegistrar(Environment.ProcessPath ?? "automata-runner.exe"),
            provider.GetRequiredService<ParkedRunStore>(),
            provider.GetRequiredService<DemoSeeder>());

        using var cancelling = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // let the run unwind and write its summary
            cancelling.Cancel();
        };

        return await dispatcher.DispatchAsync(args, cancelling.Token);
    }
}

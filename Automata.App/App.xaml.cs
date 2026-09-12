using System.Windows;
using Automata.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

namespace Automata.App;

/// <summary>
/// One DI registration point shared by the whole app — mirrors Prose.KdpPublish's App.xaml.cs
/// shape (Host.CreateDefaultBuilder + one ConfigureServices call).
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Log any unhandled exception anywhere in the app (WPF dispatcher + AppDomain-wide) in
        // every build, not just DEBUG — a Release build with no handler at all means a real
        // user's crash leaves nothing behind for them to report and nothing to debug from. The
        // dispatcher case additionally recovers rather than terminating: a published build has no
        // debugger attached to catch the default WPF crash, so someone hitting one unexpected
        // exception loses the whole session and everything unsaved in it for no reason a plain
        // message box and a log line couldn't have prevented.
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "automata-error.log");
        DispatcherUnhandledException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] DISPATCHER: {ex.Exception}\n\n");
            MessageBox.Show(
                $"Automata hit an unexpected error and logged details to:\n{logPath}\n\nYou can keep working.",
                "Automata", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] APPDOMAIN: {ex.ExceptionObject}\n\n");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] TASK: {ex.Exception}\n\n");
            ex.SetObserved();
        };

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, cfg) => cfg.AddMindAtticVaultFiles())
            .ConfigureServices((ctx, services) => services
                .AddMindAtticVault(ctx.Configuration)
                .AddAutomataCore())
            .Build();

        Services = host.Services;

        new MainWindow().Show();
    }
}

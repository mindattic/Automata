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

#if DEBUG
        // Log any unhandled exception anywhere in the app (WPF dispatcher + AppDomain-wide).
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "automata-error.log");
        DispatcherUnhandledException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] DISPATCHER: {ex.Exception}\n\n");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] APPDOMAIN: {ex.ExceptionObject}\n\n");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] TASK: {ex.Exception}\n\n");
            ex.SetObserved();
        };
#endif

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

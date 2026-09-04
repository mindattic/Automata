using System.Diagnostics;
using System.Text;
using Automata.Core.Automation.Scheduling;

namespace Automata.Runner;

/// <summary>
/// Registers the heartbeat with Windows Task Scheduler by shelling out to <c>schtasks.exe</c>.
/// <para>
/// Two entries, both idempotent: a recurring one, and an at-logon one so a firing missed while the
/// machine was off is picked up promptly rather than waiting out the whole interval.
/// </para>
/// <para>
/// <b>Both use an interactive token (<c>/IT</c>), never "run whether user is logged on or not".</b>
/// That flag runs the task in Windows session 0, where WebView2 cannot render — so the task would
/// start on time, open nothing, and fail every step. Registering it honestly as a logged-on task
/// is the difference between a schedule that works and one that looks scheduled.
/// </para>
/// </summary>
public sealed class SchTasksRegistrar(string exePath) : IScheduledTaskRegistrar
{
    public const string TickTaskName = "MindAttic.Automata.Tick";
    public const string LogonTaskName = "MindAttic.Automata.Logon";

    public async Task<string> InstallAsync(int intervalMinutes, CancellationToken ct)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var report = new StringBuilder();

        report.AppendLine(await RunAsync(
        [
            "/Create", "/F", "/TN", TickTaskName, "/TR", $"\"{exePath}\" tick",
            "/SC", "MINUTE", "/MO", intervalMinutes.ToString(), "/RU", user, "/IT",
        ], ct));

        report.Append(await RunAsync(
        [
            "/Create", "/F", "/TN", LogonTaskName, "/TR", $"\"{exePath}\" tick",
            "/SC", "ONLOGON", "/RU", user, "/IT",
        ], ct));

        return report.ToString().Trim();
    }

    public async Task<string> UninstallAsync(CancellationToken ct)
    {
        var report = new StringBuilder();
        foreach (var name in new[] { TickTaskName, LogonTaskName })
            report.AppendLine(await RunAsync(["/Delete", "/F", "/TN", name], ct));
        return report.ToString().Trim();
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start schtasks.exe");

        // Both streams are drained concurrently: reading one to completion before the other can
        // deadlock when the child fills the pipe it is not being read from.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var text = ((await stdout) + (await stderr)).Trim();
        return process.ExitCode == 0 ? text : $"schtasks failed ({process.ExitCode}): {text}";
    }
}

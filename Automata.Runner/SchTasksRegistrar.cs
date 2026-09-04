using System.Diagnostics;
using System.Text;
using Automata.Core.Automation.Scheduling;

namespace Automata.Runner;

/// <summary>
/// Registers the heartbeat with Windows Task Scheduler by shelling out to <c>schtasks.exe</c>.
/// <para>
/// <b>One task, registered from XML, carrying two triggers</b> — a repeating time trigger and an
/// at-logon trigger, so a firing missed while the machine was off is picked up promptly instead of
/// waiting out the whole interval. The XML route is not decoration: <c>schtasks</c>' command-line
/// form cannot express a second trigger, cannot express the battery settings below, and refuses
/// <c>/SC ONLOGON /RU &lt;user&gt;</c> without elevation. Registering from XML needs no elevation.
/// </para>
/// <para>
/// <b>The principal uses an interactive token, never "run whether user is logged on or not".</b>
/// That flag runs the task in Windows session 0, where WebView2 cannot render — so the task would
/// start on time, open nothing, and fail every step. Registering it honestly as a logged-on task
/// is the difference between a schedule that works and one that looks scheduled.
/// </para>
/// <para>
/// <b>The battery settings are the other half of that honesty.</b> Task Scheduler defaults to
/// refusing to start on battery and to killing a running task the moment the machine unplugs, so
/// on a laptop the default heartbeat simply never fires and says nothing about it. Both are turned
/// off explicitly.
/// </para>
/// </summary>
public sealed class SchTasksRegistrar(string exePath) : IScheduledTaskRegistrar
{
    public const string TickTaskName = "MindAttic.Automata.Tick";

    /// <summary>
    /// A separate at-logon task that earlier versions registered alongside the tick. The logon
    /// trigger now lives on the tick task itself, so this one is removed wherever it is found —
    /// leaving it behind would run a second, redundant tick at every logon.
    /// </summary>
    public const string LegacyLogonTaskName = "MindAttic.Automata.Logon";

    public async Task<RegistrationResult> InstallAsync(int intervalMinutes, CancellationToken ct)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"automata-tick-{Guid.NewGuid():n}.xml");
        try
        {
            // schtasks reads /XML as Unicode and rejects UTF-8 as malformed, so this is written
            // UTF-16 with a BOM rather than through the default encoding.
            await File.WriteAllTextAsync(
                xmlPath, BuildTaskXml(intervalMinutes), new UnicodeEncoding(false, true), ct);

            var created = await RunAsync(["/Create", "/F", "/TN", TickTaskName, "/XML", xmlPath], ct);
            if (!created.Succeeded) return created;

            // Best-effort: a machine that never had the old pair has nothing to delete, and that
            // is not a failure worth failing the install over.
            await RunAsync(["/Delete", "/F", "/TN", LegacyLogonTaskName], ct);

            return RegistrationResult.Ok(
                $"{created.Report}{Environment.NewLine}" +
                $"Runs every {intervalMinutes} minute(s), and once at logon to catch up on anything " +
                "missed while the machine was off.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RegistrationResult.Failed($"could not write the task definition: {ex.Message}");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { /* a temp file is not worth a throw */ }
        }
    }

    public async Task<RegistrationResult> UninstallAsync(CancellationToken ct)
    {
        var removed = await RunAsync(["/Delete", "/F", "/TN", TickTaskName], ct);

        // The legacy task is gone on any machine installed since the triggers were merged, so its
        // absence is expected and only its presence-then-failure is worth reporting.
        var legacy = await RunAsync(["/Delete", "/F", "/TN", LegacyLogonTaskName], ct);

        return legacy.Succeeded
            ? RegistrationResult.Ok($"{removed.Report}{Environment.NewLine}{legacy.Report}")
            : removed;
    }

    /// <summary>
    /// The task definition. <c>StartWhenAvailable</c> runs a firing the machine slept through;
    /// <c>IgnoreNew</c> keeps ticks from stacking up behind a long run; and the finite execution
    /// limit means one wedged run cannot hold the only instance slot forever and silence the
    /// schedule for good.
    /// </summary>
    private string BuildTaskXml(int intervalMinutes)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Automata heartbeat: resumes parked runs and runs whatever is due. Runs only while you are logged on, because browser lanes cannot render in session 0.</Description>
            <URI>\{TickTaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>{DateTime.Now:yyyy-MM-dd\THH:mm:ss}</StartBoundary>
              <Enabled>true</Enabled>
              <Repetition>
                <Interval>PT{intervalMinutes}M</Interval>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
            </TimeTrigger>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exePath)}</Command>
              <Arguments>tick</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static async Task<RegistrationResult> RunAsync(string[] arguments, CancellationToken ct)
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
        return process.ExitCode == 0
            ? RegistrationResult.Ok(text)
            : RegistrationResult.Failed($"schtasks failed ({process.ExitCode}): {text}");
    }
}

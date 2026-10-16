using System.Globalization;

namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// Builds the Task Scheduler XML for the Automata heartbeat task.
/// <para>
/// Pulled out of <c>SchTasksRegistrar</c> (which is Windows-only, in <c>Automata.Runner</c>) so the
/// XML itself — the repeating time trigger, the logon trigger, the interactive-token principal, the
/// battery settings, and the escaping of untrusted path/user text — stays covered by plain NUnit
/// tests. A regression here does not throw; it silently stops the heartbeat on laptops or whenever
/// the task would otherwise run in session 0.
/// </para>
/// </summary>
public static class ScheduledTaskXmlBuilder
{
    public static string Build(string taskUri, string exePath, string user, int intervalMinutes, DateTime startBoundary)
    {
        var startText = startBoundary.ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture);
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Automata heartbeat: resumes parked runs and runs whatever is due. Runs only while you are logged on, because browser lanes cannot render in session 0.</Description>
            <URI>\{taskUri}</URI>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>{startText}</StartBoundary>
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
}

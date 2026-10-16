using System.Xml.Linq;
using Automata.Core.Automation.Scheduling;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers <see cref="ScheduledTaskXmlBuilder"/>, which is the part of
/// <c>Automata.Runner.SchTasksRegistrar</c> that can be tested without Windows: the task XML that
/// Task Scheduler actually registers. A regression here does not throw — it silently stops the
/// heartbeat on laptops (battery settings) or whenever the task runs in session 0 (missing
/// interactive-token principal, missing logon trigger).
/// </summary>
[TestFixture]
public class SchTasksRegistrarTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Build(string taskUri = "Task", string exePath = @"C:\app\automata-runner.exe",
        string user = @"DOMAIN\user", int intervalMinutes = 15) =>
        XDocument.Parse(ScheduledTaskXmlBuilder.Build(taskUri, exePath, user, intervalMinutes, new DateTime(2026, 1, 1)));

    [Test]
    public void ProducesXmlThatParses()
    {
        Assert.DoesNotThrow(() => Build());
    }

    [Test]
    public void IntervalBecomesIsoRepetitionInterval()
    {
        var doc = Build(intervalMinutes: 42);

        var interval = doc.Descendants(Ns + "Repetition").Single().Element(Ns + "Interval")!.Value;

        Assert.That(interval, Is.EqualTo("PT42M"));
    }

    [Test]
    public void HasBothTimeTriggerAndLogonTrigger()
    {
        var doc = Build();
        var triggers = doc.Descendants(Ns + "Triggers").Single();

        Assert.That(triggers.Element(Ns + "TimeTrigger"), Is.Not.Null);
        Assert.That(triggers.Element(Ns + "LogonTrigger"), Is.Not.Null);
    }

    [Test]
    public void PrincipalUsesInteractiveToken()
    {
        var doc = Build();

        var logonType = doc.Descendants(Ns + "Principal").Single().Element(Ns + "LogonType")!.Value;

        Assert.That(logonType, Is.EqualTo("InteractiveToken"));
    }

    [Test]
    public void BatterySettingsAreBothFalse()
    {
        var doc = Build();
        var settings = doc.Descendants(Ns + "Settings").Single();

        Assert.That(settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value, Is.EqualTo("false"));
        Assert.That(settings.Element(Ns + "StopIfGoingOnBatteries")!.Value, Is.EqualTo("false"));
    }

    [Test]
    public void IgnoresNewInstancesAndLimitsExecutionTo72Hours()
    {
        var doc = Build();
        var settings = doc.Descendants(Ns + "Settings").Single();

        Assert.That(settings.Element(Ns + "MultipleInstancesPolicy")!.Value, Is.EqualTo("IgnoreNew"));
        Assert.That(settings.Element(Ns + "ExecutionTimeLimit")!.Value, Is.EqualTo("PT72H"));
    }

    [Test]
    public void ExePathIsXmlEscaped()
    {
        const string path = """C:\apps\A & B\<runner> "x".exe""";

        var doc = Build(exePath: path);

        var command = doc.Descendants(Ns + "Exec").Single().Element(Ns + "Command")!.Value;
        Assert.That(command, Is.EqualTo(path));
    }

    [Test]
    public void UserIsXmlEscaped()
    {
        const string user = """DOMAIN\<user "A & B">""";

        var doc = Build(user: user);

        var principalUserId = doc.Descendants(Ns + "Principal").Single().Element(Ns + "UserId")!.Value;
        var logonTriggerUserId = doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value;

        Assert.That(principalUserId, Is.EqualTo(user));
        Assert.That(logonTriggerUserId, Is.EqualTo(user));
    }

    [Test]
    public void ActionRunsTheExeWithTheTickArgument()
    {
        var doc = Build();
        var exec = doc.Descendants(Ns + "Exec").Single();

        Assert.That(exec.Element(Ns + "Arguments")!.Value, Is.EqualTo("tick"));
    }
}

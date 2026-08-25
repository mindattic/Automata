using System.IO;
using System.Text.RegularExpressions;
using Automata.Core.Automation.Logging;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RunLogWriterTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() =>
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void CreatesTimestampedSluggedFile_AndAppendsStampedLines()
    {
        var writer = new RunLogWriter("Check Email from Dave!", root);

        writer.WriteLine("step 1 passed");
        writer.WriteLine("run finished");

        Assert.That(Path.GetFileName(writer.FilePath),
            Does.Match(@"^\d{8}-\d{6}-check-email-from-dave\.log$"));
        var content = File.ReadAllText(writer.FilePath);
        Assert.That(content, Does.Contain("step 1 passed"));
        Assert.That(content, Does.Contain("run finished"));
        Assert.That(Regex.Matches(content, @"^\[\d{2}:\d{2}:\d{2}\]", RegexOptions.Multiline).Count,
            Is.EqualTo(3)); // header + 2 lines
    }

    [Test]
    public void SecondWriterInSameSecond_GetsSuffixedFileName()
    {
        var first = new RunLogWriter("Task", root);
        var second = new RunLogWriter("Task", root);

        Assert.That(second.FilePath, Is.Not.EqualTo(first.FilePath));
        Assert.That(File.Exists(first.FilePath), Is.True);
        Assert.That(File.Exists(second.FilePath), Is.True);
    }
}

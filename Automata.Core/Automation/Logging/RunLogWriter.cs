using System.IO;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Logging;

/// <summary>
/// Per-run log file at
/// <c>%USERPROFILE%\Documents\Automata\Logs\&lt;yyyyMMdd-HHmmss&gt;-&lt;task-slug&gt;.log</c> —
/// beside the Collections folder, one fixed easy-to-find place, one file per run.
/// </summary>
public sealed class RunLogWriter
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Logs");

    public string FilePath { get; }

    public RunLogWriter(string taskName, string? rootPath = null)
    {
        var root = rootPath ?? DefaultRoot;
        Directory.CreateDirectory(root);

        var baseName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{StoreUtil.Slug(taskName)}";
        var path = Path.Combine(root, baseName + ".log");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(root, $"{baseName}-{n}.log");
        FilePath = path;

        File.WriteAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] Run log for '{taskName}'{Environment.NewLine}");
    }

    public void WriteLine(string line) =>
        File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
}

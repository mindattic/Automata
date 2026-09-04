using System.IO;
using Automata.Core.Automation.Data;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// The workspace's datasets: the CSV/JSON files a task fans out over and writes results into.
/// <para>
/// One folder, browsable in Explorer, exactly like Collections — a user can drop a spreadsheet
/// export in and a task can read it, with no import step. A dataset name is a file name, so
/// "skus.csv" means the file called skus.csv.
/// </para>
/// </summary>
public sealed class DatasetStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Datasets");

    public string RootPath { get; }

    public DatasetStore(string? rootPath = null) => RootPath = rootPath ?? DefaultRoot;

    /// <summary>Resolves a dataset name to a path inside the root. A name that tries to climb out
    /// is sanitised rather than honoured.</summary>
    public string PathFor(string datasetName) =>
        Path.Combine(RootPath, StoreUtil.SafeFileName(datasetName));

    public bool Exists(string datasetName) => File.Exists(PathFor(datasetName));

    public IReadOnlyList<Dictionary<string, string>> Read(string datasetName) =>
        DatasetIO.Read(PathFor(datasetName));

    public IReadOnlyList<string> Columns(string datasetName) =>
        DatasetIO.Columns(PathFor(datasetName));

    public void Append(string datasetName, IReadOnlyDictionary<string, string> row) =>
        DatasetIO.Write(PathFor(datasetName), [row], append: true);

    public void Write(string datasetName, IEnumerable<IReadOnlyDictionary<string, string>> rows, bool append) =>
        DatasetIO.Write(PathFor(datasetName), rows, append);

    /// <summary>Dataset file names, for the picker. Empty when nothing has been added yet — the
    /// folder is not created until something writes to it.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(RootPath)) return [];
        return Directory.EnumerateFiles(RootPath)
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }
}

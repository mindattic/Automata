using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Data;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// The workspace's datasets: the rows a task fans out over and writes results into, in the
/// <c>Datasets</c> and <c>DatasetRows</c> tables.
/// <para>
/// A dataset keeps its file-style name — "skus.csv", "roster.json" — because that is what tasks
/// refer to it by, and the extension still picks the shape: a <c>.json</c> dataset is a list of
/// objects whose nested values survive untouched, anything else is CSV with a header. A CSV or
/// JSON file comes in through <see cref="ImportFile"/> (the Data tab's Import) and goes back out
/// through <see cref="ExportFile"/>.
/// </para>
/// </summary>
public sealed class DatasetStore
{
    public DatasetStore(AutomataDatabase database) => Database = database;

    public AutomataDatabase Database { get; }

    /// <summary>The name a dataset is stored under. File-name rules still apply, so a name that
    /// tries to climb out ("..\x.csv") is sanitised the same way it always was.</summary>
    public static string Normalize(string datasetName) => StoreUtil.SafeFileName(datasetName);

    public static bool IsJson(string datasetName) =>
        Path.GetExtension(datasetName).Equals(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cross-process lock one dataset's writes serialise on. Per dataset, so a parallel run
    /// writing two datasets does not queue one behind the other.
    /// </summary>
    internal string LockKeyFor(string datasetName) =>
        Database.LockKey("dataset-" + Normalize(datasetName).ToLowerInvariant());

    public bool Exists(string datasetName)
    {
        var name = Normalize(datasetName);
        using var db = Database.CreateDbContext();
        return db.Datasets.Any(d => d.Name == name);
    }

    /// <summary>Rows as string columns; a JSON dataset's nested objects also publish their leaves
    /// (<c>Address.City</c>), exactly as reading the file did.</summary>
    public IReadOnlyList<Dictionary<string, string>> Read(string datasetName)
    {
        var (record, rows) = Load(datasetName);
        if (record == null) return [];
        return IsJson(record.Name)
            ? rows.Select(r => DatasetIO.JsonRow(r, flattenNested: true)).ToList()
            : rows.Select(r => CsvRow(record.Columns, r)).ToList();
    }

    /// <summary>The column names a dataset offers — what the binding picker lists.</summary>
    public IReadOnlyList<string> Columns(string datasetName)
    {
        var (record, rows) = Load(datasetName);
        if (record == null) return [];
        if (!IsJson(record.Name)) return record.Columns;

        // Flattened, because this is the list the binding picker offers: a nested field that a
        // loop can reach but the picker cannot name is a field nobody finds.
        var names = new List<string>();
        foreach (var row in rows)
            foreach (var key in DatasetIO.JsonRow(row, flattenNested: true).Keys)
                if (!names.Contains(key, StringComparer.Ordinal))
                    names.Add(key);
        return names;
    }

    /// <summary>How many rows a dataset has, without parsing them.</summary>
    public int Count(string datasetName)
    {
        var name = Normalize(datasetName);
        using var db = Database.CreateDbContext();
        var id = db.Datasets.Where(d => d.Name == name).Select(d => (int?)d.Id).FirstOrDefault();
        return id == null ? 0 : db.DatasetRows.Count(r => r.DatasetId == id);
    }

    public void Append(string datasetName, IReadOnlyDictionary<string, string> row) =>
        Write(datasetName, [row], append: true);

    /// <summary>
    /// Writes rows. <paramref name="claimFirstWrite"/> is evaluated inside the dataset's write
    /// lock and, when it returns true, makes this write replace rather than append — see
    /// <see cref="Model.DatasetWriteSpec.ResetOnFirstWrite"/>.
    /// <para>
    /// The lock spans the whole read-modify-write: an append works out the union of columns from
    /// what is already there, and the app and the headless runner can be writing the same dataset
    /// at the same moment.
    /// </para>
    /// </summary>
    public void Write(
        string datasetName,
        IEnumerable<IReadOnlyDictionary<string, string>> rows,
        bool append,
        Func<bool>? claimFirstWrite = null)
    {
        var incoming = rows.ToList();
        using var _ = ExclusiveFileLock.Acquire(LockKeyFor(datasetName));
        if (append && claimFirstWrite != null && claimFirstWrite()) append = false;
        WriteRows(datasetName, incoming.Select(r => JsonSerializer.Serialize(r)).ToList(),
            incoming.SelectMany(r => r.Keys), append);
    }

    /// <summary>Dataset names, for the picker.</summary>
    public IReadOnlyList<string> List()
    {
        using var db = Database.CreateDbContext();
        return db.Datasets.AsNoTracking().Select(d => d.Name).ToList()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- import / export ---------------------------------------------------------------------

    /// <summary>
    /// Replaces (or creates) a dataset from CSV or JSON text — the format is the name's. JSON must
    /// be an array; its objects are kept as they are, nested values and all.
    /// </summary>
    /// <returns>The name the dataset was stored under.</returns>
    public string ImportText(string datasetName, string text)
    {
        var name = Normalize(datasetName);
        using var _ = ExclusiveFileLock.Acquire(LockKeyFor(name));
        if (IsJson(name))
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"'{name}' is not a JSON array of objects.");
            }
            WriteRows(name, DatasetIO.JsonRowTexts(text), [], append: false);
        }
        else
        {
            var header = DatasetIO.CsvHeader(text);
            var rows = DatasetIO.ReadCsvText(text);
            WriteRows(name, rows.Select(r => JsonSerializer.Serialize(r)).ToList(), header, append: false);
        }
        return name;
    }

    /// <summary>Imports a .csv or .json file, named after the file unless told otherwise.</summary>
    public string ImportFile(string filePath, string? datasetName = null) =>
        ImportText(datasetName ?? Path.GetFileName(filePath), File.ReadAllText(filePath));

    /// <summary>The dataset as the file it would have been: CSV with a header, or a JSON array.</summary>
    public string ExportText(string datasetName)
    {
        var (record, rows) = Load(datasetName);
        if (record == null) throw new InvalidOperationException($"Dataset '{datasetName}' not found.");
        return IsJson(record.Name)
            ? DatasetIO.JsonArrayText(rows)
            : DatasetIO.CsvText(record.Columns, rows.Select(r => (IReadOnlyDictionary<string, string>)CsvRow(record.Columns, r)));
    }

    public void ExportFile(string datasetName, string filePath) =>
        ChosenFile.WriteText(filePath, ExportText(datasetName));

    // ---- internals ---------------------------------------------------------------------------

    /// <summary>The record and its row JSON, read in one transaction so a concurrent replace is
    /// seen entirely or not at all.</summary>
    private (DatasetRecord? Record, IReadOnlyList<string> Rows) Load(string datasetName)
    {
        var name = Normalize(datasetName);
        using var db = Database.CreateDbContext();
        using var tx = db.Database.BeginTransaction();
        var record = db.Datasets.AsNoTracking().FirstOrDefault(d => d.Name == name);
        if (record == null) return (null, []);
        var rows = db.DatasetRows.AsNoTracking()
            .Where(r => r.DatasetId == record.Id)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.Json)
            .ToList();
        return (record, rows);
    }

    /// <summary>Caller holds the dataset's lock.</summary>
    private void WriteRows(string datasetName, IReadOnlyList<string> rowJson, IEnumerable<string> newColumns, bool append)
    {
        var name = Normalize(datasetName);
        using var db = Database.CreateDbContext();
        using var tx = db.Database.BeginTransaction();

        var now = StoreUtil.UtcNow();
        var record = db.Datasets.FirstOrDefault(d => d.Name == name);
        if (record == null)
        {
            record = new DatasetRecord { Name = name, CreatedUtc = now };
            db.Datasets.Add(record);
        }

        var next = 0;
        if (!append)
        {
            if (record.Id != 0) db.DatasetRows.Where(r => r.DatasetId == record.Id).ExecuteDelete();
            record.Columns = [];
        }
        else if (record.Id != 0)
        {
            next = (db.DatasetRows.Where(r => r.DatasetId == record.Id).Max(r => (int?)r.Ordinal) ?? -1) + 1;
        }

        if (!IsJson(name))
        {
            // Appending a row with a column the header lacks widens the header: silently dropping
            // a column would lose data, and refusing would make an evolving result set unusable.
            var columns = new List<string>(record.Columns);
            foreach (var column in newColumns)
                if (!columns.Contains(column, StringComparer.Ordinal))
                    columns.Add(column);
            record.Columns = columns;
        }

        foreach (var json in rowJson)
            record.Rows.Add(new DatasetRow { Ordinal = next++, Json = json });
        record.ModifiedUtc = now;

        db.SaveChanges();
        tx.Commit();
    }

    private static Dictionary<string, string> CsvRow(IReadOnlyList<string> columns, string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
            row[column] = values.TryGetValue(column, out var v) ? v ?? "" : "";
        return row;
    }
}

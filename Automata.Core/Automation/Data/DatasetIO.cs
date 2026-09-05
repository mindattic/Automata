using System.IO;
using System.Text;
using System.Text.Json;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Data;

/// <summary>
/// Reads and writes the CSV/JSON files a task fans out over or writes its results into.
/// <para>
/// Hand-rolled rather than taking a dependency: Automata.Core has deliberately few, and the
/// subset that matters here — RFC 4180 quoting, an embedded comma/quote/newline, a header row —
/// is small and fully covered by tests.
/// </para>
/// <para>
/// Every value is a string. CSV has no other type, and forcing JSON into the same shape keeps one
/// binding model for both rather than two.
/// </para>
/// </summary>
public static class DatasetIO
{
    /// <summary>
    /// Reads a dataset, picking the format from the file extension.
    /// <para>
    /// Locked, so a reader never catches a rewrite half-done. Appending a row that introduces a new
    /// column rewrites the whole file, and a read landing in the middle of that would see a
    /// truncated one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Dictionary<string, string>> Read(string path)
    {
        using var _ = ExclusiveFileLock.Acquire(path);
        return IsJson(path) ? ReadJson(path, flattenNested: true) : ReadCsv(path);
    }

    /// <summary>
    /// Writes a dataset, picking the format from the file extension.
    /// <para>
    /// <b>The lock spans the whole read-modify-write, not just the write.</b> An append reads the
    /// existing rows, works out the union of columns and writes the result back; locking only the
    /// final write would still let two writers each read the same "before" and clobber each other's
    /// rows. This is the door every run's dataset writing goes through, and the app and the
    /// headless runner can be writing the same file at the same moment.
    /// </para>
    /// </summary>
    /// <param name="claimFirstWrite">
    /// Optional, and settled INSIDE the lock: when it returns true this write replaces the file
    /// instead of appending to it. That is what "start fresh each run" means, and it has to be
    /// decided here rather than by the caller — two rows finishing at once would otherwise both be
    /// told they are first, or the one that is would replace a file the other had just appended to.
    /// </param>
    public static void Write(
        string path,
        IEnumerable<IReadOnlyDictionary<string, string>> rows,
        bool append = false,
        Func<bool>? claimFirstWrite = null)
    {
        using var _ = ExclusiveFileLock.Acquire(path);
        if (append && claimFirstWrite != null && claimFirstWrite()) append = false;
        if (IsJson(path)) WriteJsonArray(path, rows, append);
        else WriteCsv(path, rows, append);
    }

    /// <summary>
    /// The column names a dataset offers, without loading every row — this is what the binding
    /// picker enumerates so a user chooses a column instead of typing one.
    /// </summary>
    public static IReadOnlyList<string> Columns(string path)
    {
        using var _ = ExclusiveFileLock.Acquire(path);
        return ColumnsUnlocked(path);
    }

    /// <summary>
    /// The same, for callers that already hold the lock. Re-taking it from inside
    /// <see cref="WriteCsv"/> would deadlock against itself.
    /// </summary>
    private static IReadOnlyList<string> ColumnsUnlocked(string path)
    {
        if (!File.Exists(path)) return [];
        if (IsJson(path))
        {
            // Flattened, because this is the list the binding picker offers: a nested field that
            // a loop can reach but the picker cannot name is a field nobody finds.
            var rows = ReadJson(path, flattenNested: true);
            var names = new List<string>();
            foreach (var row in rows)
                foreach (var key in row.Keys)
                    if (!names.Contains(key, StringComparer.Ordinal))
                        names.Add(key);
            return names;
        }
        var header = ParseCsv(File.ReadAllText(path)).FirstOrDefault();
        return header ?? [];
    }

    private static bool IsJson(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    // ---- CSV ---------------------------------------------------------------------------------
    // The format-specific methods below do NO locking: they are the internals that Read and Write
    // call while already holding it. Call them directly only where concurrency is not in play.

    public static IReadOnlyList<Dictionary<string, string>> ReadCsv(string path) =>
        File.Exists(path) ? ReadCsvText(File.ReadAllText(path)) : [];

    public static IReadOnlyList<Dictionary<string, string>> ReadCsvText(string text)
    {
        var records = ParseCsv(text);
        if (records.Count == 0) return [];

        var header = records[0];
        var rows = new List<Dictionary<string, string>>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            // A trailing newline yields one empty record; that is not a row.
            if (record.Count == 1 && record[0].Length == 0) continue;

            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
                row[header[c]] = c < record.Count ? record[c] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Appending to a file whose header does not cover every column in the new rows rewrites the
    /// whole file against the union of columns. Silently dropping a column would lose data, and
    /// refusing outright would make an evolving result set unusable.
    /// </summary>
    public static void WriteCsv(string path, IEnumerable<IReadOnlyDictionary<string, string>> rows, bool append = false)
    {
        var incoming = rows.ToList();
        var existing = append && File.Exists(path) ? ReadCsv(path).ToList() : [];
        var existingHeader = append && File.Exists(path) ? ColumnsUnlocked(path).ToList() : [];

        var columns = new List<string>(existingHeader);
        foreach (var row in incoming)
            foreach (var key in row.Keys)
                if (!columns.Contains(key, StringComparer.Ordinal))
                    columns.Add(key);

        var rewriteAll = !append || !File.Exists(path) || columns.Count != existingHeader.Count;

        var sb = new StringBuilder();
        if (rewriteAll)
        {
            sb.Append(string.Join(",", columns.Select(Escape))).Append('\n');
            foreach (var row in existing) AppendRow(sb, columns, row);
        }
        foreach (var row in incoming) AppendRow(sb, columns, row);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (rewriteAll)
        {
            File.WriteAllText(path, sb.ToString());
            return;
        }

        // A file THIS code wrote always ends in a newline. One a person made — exported from a
        // spreadsheet, saved from an editor — very often does not, and appending straight onto it
        // welds the first new row to the last existing one and loses them both.
        if (LacksTrailingNewline(path)) File.AppendAllText(path, "\n");
        File.AppendAllText(path, sb.ToString());
    }

    /// <summary>Whether the last byte of an existing, non-empty file is something other than a
    /// line feed. Reads one byte rather than the file, because this runs before every append.</summary>
    private static bool LacksTrailingNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private static void AppendRow(StringBuilder sb, List<string> columns, IReadOnlyDictionary<string, string> row)
    {
        sb.Append(string.Join(",", columns.Select(c => Escape(row.TryGetValue(c, out var v) ? v : ""))));
        sb.Append('\n');
    }

    private static string Escape(string? field)
    {
        field ??= "";
        var needsQuotes = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        return needsQuotes ? '"' + field.Replace("\"", "\"\"") + '"' : field;
    }

    /// <summary>RFC 4180 field splitting: quoted fields may hold commas, newlines and "" escapes.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch != '"') { field.Append(ch); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }
                inQuotes = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    // Swallow CR; the LF that follows ends the record.
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    // ---- JSON --------------------------------------------------------------------------------

    /// <summary>
    /// The file exactly as it stands: one column per top-level property, nested values kept as
    /// their raw JSON.
    /// <para>
    /// Faithful on purpose, because <see cref="WriteJsonArray"/>'s append reads through here
    /// before rewriting the file. Reading the convenience columns and writing them back would bake
    /// them into the file, and the next append would do it again.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Dictionary<string, string>> ReadJsonArray(string path) =>
        ReadJson(path, flattenNested: false);

    /// <summary>
    /// The rows a task actually binds against, which is the same thing plus reachable nested
    /// fields: an object-valued property ALSO publishes its leaves as <c>Address.City</c>.
    /// <para>
    /// The parent keeps its raw JSON as well, so nothing that already bound to <c>Address</c>
    /// changes meaning — flattening only adds names. A whole-row binding therefore carries both,
    /// which is what "all of it" means here: every column the loop publishes.
    /// </para>
    /// <para>
    /// Objects only. An array keeps its JSON, because its length varies from row to row and
    /// <c>Items.0</c> would be a column that exists on some rows and not others — a shape this
    /// product already has a better answer for, which is a loop.
    /// </para>
    /// <para>
    /// <b>A real property always wins.</b> Every top-level name is written first and a leaf never
    /// overwrites one, for the same reason a column called <c>#</c> beats a row's position: what
    /// the file says is data, and what this works out is convenience.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Dictionary<string, string>> ReadJson(string path, bool flattenNested)
    {
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<Dictionary<string, string>>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject()) row[prop.Name] = Stringify(prop.Value);
            if (flattenNested)
                foreach (var prop in element.EnumerateObject()) AddLeaves(row, prop.Name, prop.Value);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Adds <c>parent.child</c> for every leaf under an object-valued property, recursively.
    /// TryAdd rather than assignment: the first name to claim a key keeps it, which is what makes
    /// the real property above always win.
    /// </summary>
    private static void AddLeaves(Dictionary<string, string> row, string prefix, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in value.EnumerateObject())
        {
            var key = prefix + NestedSeparator + prop.Name;
            row.TryAdd(key, Stringify(prop.Value));
            AddLeaves(row, key, prop.Value);
        }
    }

    /// <summary>
    /// What joins a nested field to its parent. A dot, matching the <c>row.Address.City</c> a
    /// binding is written as — the same separator the row variable already uses, so there is one
    /// punctuation mark to learn rather than two.
    /// </summary>
    private const string NestedSeparator = ".";

    /// <summary>Scalars become their text; anything nested keeps its JSON so nothing is lost.</summary>
    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => value.GetRawText(),
    };

    /// <summary>
    /// One row as a single-line JSON object — the form a whole-row binding hands to a step.
    /// <para>
    /// Compact, unlike <see cref="WriteJsonArray"/>: this value goes into a CSV cell or a text
    /// field, and an indented object would put newlines inside one. Values stay strings, because
    /// that is what a row IS here — a column whose value is itself raw JSON (see
    /// <see cref="Stringify"/>) comes back as a JSON string containing that text rather than as a
    /// nested object, which is the same flatness a column binding already has.
    /// </para>
    /// </summary>
    public static string RowJson(IReadOnlyDictionary<string, string> row) => JsonSerializer.Serialize(row);

    public static void WriteJsonArray(string path, IEnumerable<IReadOnlyDictionary<string, string>> rows, bool append = false)
    {
        var all = new List<IReadOnlyDictionary<string, string>>();
        if (append && File.Exists(path)) all.AddRange(ReadJsonArray(path));
        all.AddRange(rows);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}

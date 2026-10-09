using System.IO;
using System.IO.Compression;
using MindAttic.Export.Artifacts;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Writes a user-facing file to a path the person chose (a Save dialog, or a harness path) through
/// MindAttic.Export's <see cref="ArtifactWriter"/>. The path is exact — no renaming — and an existing
/// file is replaced, since the dialog already confirmed the overwrite. Writes are atomic.
/// </summary>
public static class ChosenFile
{
    /// <summary>Exact name, replace in place.</summary>
    public static readonly ArtifactOptions Options = new()
    {
        Existing = ExistingArtifact.Overwrite,
        SanitizeName = false
    };

    /// <summary>UTF-8 (no BOM) text, parent folders created.</summary>
    public static Task WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        return ArtifactWriter.WriteTextAsync(Path.GetDirectoryName(full)!, Path.GetFileName(full), content, Options, ct);
    }

    /// <summary>Synchronous form for the existing synchronous export APIs. Runs off the caller's
    /// context so a call from the UI thread cannot deadlock.</summary>
    public static void WriteText(string path, string content) =>
        Task.Run(() => WriteTextAsync(path, content)).GetAwaiter().GetResult();

    /// <summary>A zip whose entries <paramref name="fill"/> adds, parent folders created.</summary>
    public static void WriteZip(string path, Action<ZipArchive> fill)
    {
        var full = Path.GetFullPath(path);
        Task.Run(() => ArtifactWriter.WriteZipAsync(Path.GetDirectoryName(full)!, Path.GetFileName(full),
            (zip, _) => { fill(zip); return Task.CompletedTask; }, Options)).GetAwaiter().GetResult();
    }
}

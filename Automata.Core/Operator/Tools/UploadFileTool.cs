using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// Attaches a local file to a page's file input via <see cref="IBrowserSurface.InjectFileAsync"/>
/// (CDP <c>DOM.setFileInputFiles</c>) — no native OS file dialog ever opens. Merges
/// Prose.KdpPublish's KDP-specific <c>UploadManuscriptTool</c> (defaulted to the first
/// <c>input[type=file]</c> in DOM order) and <c>UploadCoverTool</c> (a hardcoded element id) into
/// one generic tool that takes an explicit, caller-supplied selector.
/// </summary>
public class UploadFileTool : IBrowserTool
{
    public string Name => "upload_file";

    public string Description =>
        "Attach a local file to a file-upload input on the current page. No native file dialog " +
        "appears — the file is attached directly. `selector` is a CSS selector for the target " +
        "input (default: the first input[type=file] on the page) — pass a more specific " +
        "selector (e.g. \"#cover-upload\") when a page has more than one file input. After " +
        "calling, use get_page_status to confirm the page accepted the upload before continuing.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "file_path": { "type": "string", "description": "Absolute path to the local file to upload." },
        "selector": { "type": "string", "description": "CSS selector for the target file input. Defaults to input[type=file]." }
      },
      "required": ["file_path"]
    }
    """;

    public async Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        var selector = args.TryGetProperty("selector", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(selector)) selector = "input[type=file]";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" });

        try
        {
            await ctx.Browser.InjectFileAsync(filePath, selector, ct);
            return JsonSerializer.Serialize(new { ok = true, file = filePath, selector });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

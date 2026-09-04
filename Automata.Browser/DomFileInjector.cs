using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Automata.Browser;

/// <summary>
/// Attaches a file to a page's <c>&lt;input type="file"&gt;</c> element via Chrome DevTools
/// Protocol's <c>DOM.setFileInputFiles</c> — no native OS file-picker dialog ever opens. Ported
/// verbatim from Prose.KdpPublish.
/// </summary>
public static class DomFileInjector
{
    /// <summary>Finds the first (or a selector-matched) file input on the current page and sets
    /// its file to <paramref name="filePath"/>. Throws if no matching input is found after
    /// retrying.</summary>
    public static async Task InjectAsync(CoreWebView2 core, string filePath, string selector = "input[type=file]")
    {
        var evalParams = JsonSerializer.Serialize(new { expression = $"document.querySelector('{selector}')" });

        // A client-rendered SPA's file inputs may not be in the DOM immediately after
        // navigation — retry across a real render window before concluding the control
        // genuinely doesn't exist.
        JsonElement result = default;
        var found = false;
        for (var attempt = 0; attempt < 10 && !found; attempt++)
        {
            if (attempt > 0) await Task.Delay(1000);
            var evalResultJson = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", evalParams);
            using var doc = JsonDocument.Parse(evalResultJson);
            found = doc.RootElement.TryGetProperty("result", out result) &&
                    result.TryGetProperty("objectId", out _);
            if (found) result = result.Clone();
        }

        if (!found || !result.TryGetProperty("objectId", out var objectIdProp))
        {
            throw new InvalidOperationException(
                $"No element matching '{selector}' found on the current page after retrying for 10 seconds.");
        }

        var objectId = objectIdProp.GetString();
        var setFilesParams = JsonSerializer.Serialize(new { files = new[] { filePath }, objectId });
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", setFilesParams);
    }
}

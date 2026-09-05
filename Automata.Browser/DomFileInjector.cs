using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Automata.Browser;

/// <summary>
/// Attaches a file to a page's <c>&lt;input type="file"&gt;</c> element via Chrome DevTools
/// Protocol's <c>DOM.setFileInputFiles</c> — no native OS file-picker dialog ever opens. Ported
/// from Prose.KdpPublish.
/// <para>
/// It takes a JavaScript EXPRESSION rather than a selector, and that difference is the whole reason
/// an upload can now reach where every other action already could. <c>DOM.setFileInputFiles</c>
/// needs a RemoteObject, which means something has to be evaluated to produce the element — and a
/// <c>document.querySelector</c> stops at a shadow boundary, so the one action that did not go
/// through the resolver was the one action that stopped at the first component library it met.
/// Handed <c>window.__automataLastResolved</c> instead, it gets whatever the resolver found: inside
/// an open shadow root, inside a closed one, inside a same-origin frame.
/// </para>
/// <para>
/// A CROSS-ORIGIN frame is still out of reach here, and for a reason nothing in this file can fix:
/// the expression is evaluated in the top document's context, and the element is not in it. Reaching
/// that one means a per-frame execution context, which is a different mechanism again — the caller
/// checks for it and says so rather than attaching the wrong file to the wrong input.
/// </para>
/// </summary>
public static class DomFileInjector
{
    /// <summary>Evaluates <paramref name="expression"/> to an element and sets its file to
    /// <paramref name="filePath"/>. Throws if the expression yields no element after retrying.</summary>
    public static async Task InjectAsync(
        CoreWebView2 core, string filePath, string expression = "document.querySelector('input[type=file]')")
    {
        var evalParams = JsonSerializer.Serialize(new { expression });

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
                $"'{expression}' yielded no element on the current page after retrying for 10 seconds.");
        }

        var objectId = objectIdProp.GetString();
        var setFilesParams = JsonSerializer.Serialize(new { files = new[] { filePath }, objectId });
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", setFilesParams);
    }
}

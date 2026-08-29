using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Automata.Core.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace Automata.App;

/// <summary>
/// Both panes are plain WebView2 + vanilla JS — proven pattern from Prose.KdpPublish (see that
/// app's history for why BlazorWebView was abandoned in this exact hosting combination).
/// </summary>
public partial class MainWindow : Window
{
    private IBrowserSurface? targetBrowser;
    private CancellationTokenSource? runCts;
    private readonly AutomationController controller;

    public MainWindow()
    {
        InitializeComponent();

        controller = new AutomationController(
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.CollectionStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.ArchiveService>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Replay.ReplayEngine>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.AutomataSettingsStore>(),
            () => targetBrowser,
            () => TargetBrowser.CoreWebView2,
            script => ControlPanel.CoreWebView2 == null
                ? Task.CompletedTask
                : TryExecuteScriptAsync(ControlPanel.CoreWebView2, script),
            PostLogAsync);

#if DEBUG
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) ControlPanel.CoreWebView2?.OpenDevToolsWindow();
            if (e.Key == Key.F10) TargetBrowser.CoreWebView2?.OpenDevToolsWindow();
        };
#endif

        _ = InitializeControlPanelAsync();
        _ = InitializeTargetBrowserAsync();
    }

    /// <summary>Opt-in remote-debugging hook for tools/verify-ui.mjs. Null (today's exact
    /// behavior) unless the named env var holds a valid port number.</summary>
    private static CoreWebView2EnvironmentOptions? DebugOptions(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var port) || port <= 0)
            return null;
        return new CoreWebView2EnvironmentOptions(additionalBrowserArguments: $"--remote-debugging-port={port}");
    }

    /// <summary>Opt-in profile-directory override so the verification harness never touches the
    /// real profile under %LocalAppData%. Returns today's exact hardcoded path when unset.</summary>
    private static string ProfileDir(string envVar, string defaultLeaf)
    {
        var overridePath = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MindAttic", "Automata", defaultLeaf);
    }

    private async Task InitializeControlPanelAsync()
    {
        var userDataFolder = ProfileDir("AUTOMATA_PANEL_PROFILE_DIR", "ControlPanelWebView2");
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder, options: DebugOptions("AUTOMATA_PANEL_CDP_PORT"));
        await ControlPanel.EnsureCoreWebView2Async(env);

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        ControlPanel.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "automata.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        ControlPanel.CoreWebView2.WebMessageReceived += OnControlPanelMessage;
        ControlPanel.CoreWebView2.Navigate("https://automata.local/panel.html");
    }

    /// <summary>
    /// A dedicated user-data folder (separate from any installed Chrome/Edge profile) means a
    /// site login persists across app restarts without touching the user's regular browser
    /// profile at all.
    /// </summary>
    private async Task InitializeTargetBrowserAsync()
    {
        var userDataFolder = ProfileDir("AUTOMATA_TARGET_PROFILE_DIR", "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder, options: DebugOptions("AUTOMATA_TARGET_CDP_PORT"));
        await TargetBrowser.EnsureCoreWebView2Async(env);

        // Any "open in new window" request (target="_blank", window.open(), etc.) redirects
        // back into this same pane instead of spawning an untracked standalone popup window
        // this app has no visibility into.
        TargetBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            TargetBrowser.CoreWebView2.Navigate(args.Uri);
        };

        // Any native alert()/confirm()/beforeunload dialog the target page raises would
        // otherwise show as an unattended modal with nobody to click it — and every
        // ExecuteScriptAsync/CDP call the tools issue afterward hangs forever waiting for the
        // blocked renderer, with no timeout anywhere to break it. Auto-accepting keeps this
        // fully unattended instead of depending on a human happening to notice and click it.
        TargetBrowser.CoreWebView2.ScriptDialogOpening += (_, args) =>
        {
            _ = PostLogAsync($"⚠ page raised a {args.Kind} dialog: \"{args.Message}\" — auto-accepting.");
            args.Accept();
        };

        // The recorder rides along on every document, dormant until the Record button arms it.
        // fingerprint.js is Automata.Core's embedded resource; recorder.js ships in wwwroot.
        var recorderJs = Automata.Core.Automation.AutomationScripts.FingerprintJs + "\n" +
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "target", "recorder.js"));
        await TargetBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(recorderJs);
        TargetBrowser.CoreWebView2.WebMessageReceived += OnTargetMessage;
        TargetBrowser.CoreWebView2.NavigationCompleted += (_, _) =>
            _ = controller.OnTargetNavigationCompletedAsync(TargetBrowser.CoreWebView2.Source);

        TargetBrowser.CoreWebView2.Navigate("about:blank");

        targetBrowser = new WebView2BrowserSurface(TargetBrowser.CoreWebView2);
    }

    private void OnTargetMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(args.WebMessageAsJson); }
        catch { return; }
        // Only the injected recorder's envelope is trusted; anything else a page posts is noise.
        if (msg?["source"]?.GetValue<string>() != "automata-recorder") return;
        _ = controller.HandleRecorderMessageAsync(msg!);
    }

    private async void OnControlPanelMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(args.WebMessageAsJson); }
        catch { return; }
        var action = msg?["action"]?.GetValue<string>();

        try
        {
            switch (action)
            {
                case "navigate":
                    var url = msg!["url"]!.GetValue<string>();
                    TargetBrowser.CoreWebView2.Navigate(url);
                    await PostLogAsync($"Navigating to {url}");
                    break;
                case "run":
                    var task = msg!["task"]!.GetValue<string>();
                    _ = RunTaskAsync(task);
                    break;
                case "cancel":
                    // Cancels whichever run is live: the AI free-text loop (runCts here) and/or
                    // a task replay (the controller's own CTS) — the panel shares one Cancel.
                    runCts?.Cancel();
                    await controller.TryHandlePanelMessageAsync("cancelRun", msg!);
                    break;
                default:
                    if (action != null && msg != null)
                        await controller.TryHandlePanelMessageAsync(action, msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            await PostLogAsync($"⚠ Control panel message '{action}' failed: {ex.Message}");
        }
    }

    private async Task RunTaskAsync(string task)
    {
        if (targetBrowser == null)
        {
            await PostLogAsync("⚠ Target browser pane isn't ready yet — wait for it to finish loading and try again.");
            return;
        }
        if (string.IsNullOrWhiteSpace(task))
        {
            await PostLogAsync("⚠ Nothing to run — type an instruction first.");
            return;
        }

        runCts = new CancellationTokenSource();
        await SetRunningAsync(true);

        var operatorService = App.Services.GetRequiredService<BrowserOperatorService>();
        var ctx = new BrowserOperatorContext { Browser = targetBrowser };

        await PostLogAsync($"Starting: {task}");
        try
        {
            await foreach (var evt in operatorService.RunAsync(SystemPrompt, task, ctx, cancel: runCts.Token))
                await PostLogAsync(FormatEvent(evt));
        }
        catch (OperationCanceledException)
        {
            await PostLogAsync("Cancelled.");
        }
        catch (Exception ex)
        {
            await PostLogAsync($"Unexpected failure — {ex.Message}");
        }

        await PostLogAsync("Run finished.");
        await SetRunningAsync(false);
    }

    private const string SystemPrompt = """
        You are operating a live browser pane to accomplish a plain-English task. You have tools,
        not raw DOM access — use get_page_status first to see where you are, then act with
        click_button, set_field, type_into_field, select_form_option, check_checkbox, or
        upload_file as the task requires. Call get_page_status again after any action that might
        change or navigate the page, and wait for isProcessing to clear before trusting a form's
        state. Use log_note for anything worth telling the user that isn't captured by another
        tool's own result. When you're done, reply with a short final text summary of what
        happened (and any data you were asked to extract).
        """;

    /// <summary>
    /// A hung/orphaned WebView2 browser process can make ExecuteScriptAsync block forever with
    /// zero exception and zero log output, silently freezing an entire run behind a UI that never
    /// updates. Every UI-update call goes through this instead of a raw ExecuteScriptAsync so a
    /// stuck script call can never again block progress or hide what happened.
    /// </summary>
    private static async Task<bool> TryExecuteScriptAsync(CoreWebView2 webView, string script, int timeoutMs = 8000)
    {
        try
        {
            var scriptTask = webView.ExecuteScriptAsync(script);
            var winner = await Task.WhenAny(scriptTask, Task.Delay(timeoutMs));
            if (winner != scriptTask)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ WebView2 ExecuteScriptAsync timed out after {timeoutMs}ms — continuing without UI update.");
                return false;
            }
            await scriptTask; // observe/propagate a real script exception, if any
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ WebView2 ExecuteScriptAsync failed: {ex.Message}");
            return false;
        }
    }

    private async Task PostLogAsync(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Console.WriteLine(stamped);
        var jsArg = JsonSerializer.Serialize(stamped);
        await TryExecuteScriptAsync(ControlPanel.CoreWebView2, $"window.ssPanel.onLog({jsArg})");
    }

    private async Task SetRunningAsync(bool running)
        => await TryExecuteScriptAsync(ControlPanel.CoreWebView2, $"window.ssPanel.onRunState({(running ? "true" : "false")})");

    private static string FormatEvent(OperatorEvent evt) => evt switch
    {
        OperatorEvent.AssistantText t => t.Text,
        OperatorEvent.ToolStarted s => $"→ {s.Name}({Truncate(s.ArgsJson, 120)})",
        OperatorEvent.ToolCompleted c => $"{(c.IsError ? "✗" : "✓")} {c.Name} → {Truncate(c.ResultJson, 160)}",
        OperatorEvent.Error e => $"⚠ {e.Message}",
        OperatorEvent.Info i => i.Message,
        _ => evt.ToString() ?? "",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

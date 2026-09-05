using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Automata.Browser;
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
    private readonly Automata.Core.Automation.Storage.AutomataSettingsStore settingsStore;

    public MainWindow()
    {
        InitializeComponent();

        settingsStore = App.Services
            .GetRequiredService<Automata.Core.Automation.Storage.AutomataSettingsStore>();
        RestoreSidebarWidth();
        ApplyWindowTheme();
        // Before InitializeControlPanelAsync below: detaching a panel that has not built its
        // browser yet is a plain reparent of an empty control, which is the cheapest moment there
        // is to do it. Toggling later moves a live one, which works but has more to go wrong.
        if (settingsStore.Load().PanelDetached) DetachPanel();

        controller = new AutomationController(
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.CollectionStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.ArchiveService>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Execution.WorkflowEngine>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.AutomataSettingsStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.DatasetStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Storage.RunStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Scheduling.ScheduleStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Execution.ParkedRunStore>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Demos.DemoSeeder>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Scheduling.IClock>(),
            App.Services.GetRequiredService<Automata.Core.Automation.Flow.FlowAuthoringService>(),
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

    /// <summary>
    /// Restores the saved sidebar width, clamped to the column's own Min/Max so a hand-edited
    /// settings.json can never wedge the layout into an unusable shape.
    /// </summary>
    private void RestoreSidebarWidth()
    {
        var saved = settingsStore.Load().SidebarWidth;
        if (double.IsNaN(saved) || saved <= 0) return;
        var clamped = Math.Clamp(saved, SidebarColumn.MinWidth, SidebarColumn.MaxWidth);
        SidebarColumn.Width = new GridLength(clamped, GridUnitType.Pixel);
    }

    /// <summary>
    /// Paints the parts of the window that are NOT the panel — the splitter, and the ground the
    /// panes are mounted on.
    /// <para>
    /// The two WebView2 panes carry their own palette in CSS, but the splitter between them is
    /// WPF's, and a dark bar down the middle of a light window is the one seam a theme cannot
    /// hide. Read from the same setting the panel reads, so there is one answer to "which theme".
    /// </para>
    /// </summary>
    private void ApplyWindowTheme()
    {
        var light = Automata.Core.Automation.Storage.AutomataSettings.Themes.Coerce(
            settingsStore.Load().Theme)
            == Automata.Core.Automation.Storage.AutomataSettings.Themes.Light;

        // The same values as --color-border / --color-bg-2 in each theme's tokens.css.
        var edge = light ? "#D0D4D8" : "#333333";
        var ground = light ? "#ECEEF0" : "#1E1E1E";
        SidebarSplitter.Background = (Brush)new BrushConverter().ConvertFromString(edge)!;
        Background = (Brush)new BrushConverter().ConvertFromString(ground)!;
    }

    /// <summary>
    /// Persists the current sidebar width. Called both when the splitter is released and when the
    /// window closes — the second case is what catches a keyboard resize, which never raises a
    /// drag event.
    /// </summary>
    private void SaveSidebarWidth()
    {
        try
        {
            var width = SidebarColumn.ActualWidth;
            if (width <= 0) return;
            var settings = settingsStore.Load();
            if (Math.Abs(settings.SidebarWidth - width) < 0.5) return;
            settings.SidebarWidth = width;
            settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A width preference is never worth failing a close over.
        }
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => SaveSidebarWidth();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSidebarWidth();
        // Closed directly rather than docked: the app is going away, and docking would rewrite the
        // preference to "attached" for someone who deliberately left it detached.
        if (detachedPanel == null) return;
        SaveDetachedBounds();
        docking = true;
        detachedPanel.Release();
        detachedPanel.Close();
        detachedPanel = null;
    }

    // ---- the sidebar in its own window -----------------------------------------------------------

    /// <summary>Non-null while the sidebar is undocked. It holds the one and only panel WebView2.</summary>
    private DetachedPanelWindow? detachedPanel;

    /// <summary>Guards the close→dock→close path, since docking closes the window that asked.</summary>
    private bool docking;

    /// <summary>The width the docked column had before the panel left it, so docking puts it back
    /// where it was rather than at the default.</summary>
    private GridLength dockedWidth = new(420, GridUnitType.Pixel);

    public bool PanelIsDetached => detachedPanel != null;

    /// <summary>
    /// Detaches or docks, whichever the sidebar is not, then remembers it and TELLS THE PANEL.
    /// <para>
    /// The push belongs here rather than at the call site because there are two call sites and one
    /// of them is the detached window's close button. When only the button pushed, closing that
    /// window docked the sidebar and left the button still reading "Dock the sidebar" — a control
    /// describing a state the app was no longer in.
    /// </para>
    /// <para>
    /// If the move itself fails, the sidebar goes back to being docked before anything is saved:
    /// the panel is the only way to drive this app, and a half-moved one is a window with no UI in
    /// it and no way to ask for one back.
    /// </para>
    /// </summary>
    public void TogglePanelDetached()
    {
        var wantDetached = detachedPanel == null;
        try
        {
            if (wantDetached) DetachPanel();
            else DockPanel();
        }
        catch (Exception ex)
        {
            RecoverToDocked();
            _ = PostLogAsync($"⚠ The sidebar could not be moved — {ex.Message}");
        }

        PersistDetachedState();
    }

    /// <summary>
    /// Writes down where the sidebar now lives, and tells the panel so its own control agrees.
    /// <para>
    /// Both halves, from every path that moves the sidebar — including the detached window's close
    /// button, which is the one that used to skip the telling and leave a button reading "Dock the
    /// sidebar" on a sidebar that was already docked.
    /// </para>
    /// <para>
    /// The push happens whatever the save did. Remembering the preference is worth less than the
    /// panel agreeing with the window, and this is the shape the NaN bug argued for: one
    /// unserializable default threw in the save and took the announcement down with it.
    /// </para>
    /// </summary>
    private void PersistDetachedState()
    {
        try
        {
            var settings = settingsStore.Load();
            settings.PanelDetached = detachedPanel != null;
            settingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            _ = PostLogAsync($"⚠ The sidebar moved, but where it lives could not be saved — {ex.Message}");
        }
        _ = controller.PushSettingsAsync();
    }

    /// <summary>
    /// Puts the panel back in the grid from whatever half-state a failed move left it in.
    /// <para>
    /// Deliberately tolerant rather than precise: it does not know how far the move got, so it
    /// asserts the docked shape from first principles and accepts that some of that is a no-op.
    /// </para>
    /// </summary>
    private void RecoverToDocked()
    {
        var stray = detachedPanel;
        detachedPanel = null;
        if (stray != null)
        {
            docking = true;
            try { stray.Release(); stray.Close(); } catch { /* already gone */ }
            docking = false;
        }

        if (!RootGrid.Children.Contains(ControlPanel)) RootGrid.Children.Add(ControlPanel);
        SidebarColumn.MinWidth = 340;
        SidebarColumn.Width = dockedWidth.Value > 0 ? dockedWidth : new GridLength(420, GridUnitType.Pixel);
        SidebarSplitter.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Lifts the panel out of the grid and into its own window.
    /// <para>
    /// The column collapses to nothing and the splitter goes with it, so the browser gets the whole
    /// window rather than the whole window minus an empty 420px gutter. MinWidth has to be cleared
    /// too — a column with a MinWidth cannot be zero, and the gutter would stay.
    /// </para>
    /// </summary>
    private void DetachPanel()
    {
        if (detachedPanel != null) return;

        dockedWidth = SidebarColumn.Width;
        RootGrid.Children.Remove(ControlPanel);
        SidebarColumn.MinWidth = 0;
        SidebarColumn.Width = new GridLength(0);
        SidebarSplitter.Visibility = Visibility.Collapsed;

        var settings = settingsStore.Load();
        detachedPanel = new DetachedPanelWindow(ControlPanel, Background)
        {
            Owner = null,
            Width = settings.PanelWindowWidth > 0 ? settings.PanelWindowWidth : 460,
            Height = settings.PanelWindowHeight > 0 ? settings.PanelWindowHeight : 900,
        };
        PlaceDetached(detachedPanel, settings);

        detachedPanel.Closing += (_, _) =>
        {
            if (docking) return;
            // Someone closed this window themselves. The panel is the only way to drive this app,
            // so it goes back in the grid — but the close is ALLOWED to finish rather than being
            // cancelled and re-issued: a Window cannot be closed from inside its own Closing
            // handler, and doing that threw every time the title-bar X was used.
            DockFromClosing();
            PersistDetachedState();
        };

        detachedPanel.Show();
        detachedPanel.Activate();
    }

    /// <summary>
    /// Puts the saved position back, but only if it still lands on a screen that exists.
    /// <para>
    /// A window remembered onto a monitor that has since been unplugged opens somewhere nobody can
    /// see, and the only way back is to edit settings.json by hand.
    /// </para>
    /// </summary>
    private static void PlaceDetached(Window window, Automata.Core.Automation.Storage.AutomataSettings settings)
    {
        if (settings.PanelWindowLeft is not { } left || settings.PanelWindowTop is not { } top)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var onScreen =
            left + window.Width > SystemParameters.VirtualScreenLeft &&
            top + window.Height > SystemParameters.VirtualScreenTop &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!onScreen)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.Left = left;
        window.Top = top;
    }

    /// <summary>Puts the panel back in the grid and closes the window it was living in.</summary>
    private void DockPanel()
    {
        if (detachedPanel == null) return;

        SaveDetachedBounds();
        var window = detachedPanel;
        detachedPanel = null;
        var panel = window.Release();

        docking = true;
        window.Close();
        docking = false;

        RestoreDockedLayout(panel);
        Activate();
    }

    /// <summary>
    /// The same, for the case where the window is ALREADY closing — the title-bar X, Alt+F4, the
    /// taskbar. It takes the panel out and lets the close finish; there is nothing left to close.
    /// </summary>
    private void DockFromClosing()
    {
        if (detachedPanel == null) return;

        SaveDetachedBounds();
        var window = detachedPanel;
        detachedPanel = null;
        RestoreDockedLayout(window.Release());
        Activate();
    }

    /// <summary>The docked shape: the panel in its column, the column back to its width, and the
    /// splitter that resizes it back on screen.</summary>
    private void RestoreDockedLayout(UIElement? panel)
    {
        if (panel != null && !RootGrid.Children.Contains(panel)) RootGrid.Children.Add(panel);
        SidebarColumn.MinWidth = 340;
        SidebarColumn.Width = dockedWidth.Value > 0 ? dockedWidth : new GridLength(420, GridUnitType.Pixel);
        SidebarSplitter.Visibility = Visibility.Visible;
    }

    private void SaveDetachedBounds()
    {
        if (detachedPanel == null) return;
        try
        {
            var settings = settingsStore.Load();
            // RestoreBounds, not Left/Top: a maximised or minimised window reports its CURRENT
            // placement, and remembering -32000 puts it off every screen on the next launch.
            var bounds = detachedPanel.WindowState == WindowState.Normal
                ? new Rect(detachedPanel.Left, detachedPanel.Top, detachedPanel.Width, detachedPanel.Height)
                : detachedPanel.RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            settings.PanelWindowLeft = bounds.Left;
            settings.PanelWindowTop = bounds.Top;
            settings.PanelWindowWidth = bounds.Width;
            settings.PanelWindowHeight = bounds.Height;
            settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A window position is never worth failing a dock over.
        }
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

        // Stamped BEFORE the document exists, so a light-theme user never sees a frame of the dark
        // default. The panel applies the same value again when settings arrive; this is only about
        // the first paint, which is the one the settings round trip is too late for.
        var theme = Automata.Core.Automation.Storage.AutomataSettings.Themes.Coerce(
            settingsStore.Load().Theme);
        // Document-created runs after the global object exists but BEFORE <html> is parsed, so
        // documentElement is still null at that moment — hence the observer rather than a one-line
        // assignment. It disconnects as soon as the root appears, which is before anything paints.
        await ControlPanel.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            (function () {
              var set = function () {
                if (!document.documentElement) return false;
                document.documentElement.dataset.theme = '{{theme}}';
                return true;
              };
              if (!set()) {
                var watch = new MutationObserver(function () { if (set()) watch.disconnect(); });
                watch.observe(document, { childList: true, subtree: true });
              }
            })();
            """);

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
        // fingerprint.js and harvest.js are Automata.Core embedded resources; recorder.js ships in
        // wwwroot. harvest.js rides along because picking a harvest is a gesture in the TARGET
        // pane — the user clicks one row and the page itself works out what "all the rows like this
        // one" means, which is only answerable where the DOM is.
        var recorderJs = Automata.Core.Automation.AutomationScripts.StabilityJs + "\n" +
            Automata.Core.Automation.AutomationScripts.FingerprintJs + "\n" +
            Automata.Core.Automation.AutomationScripts.HarvestJs + "\n" +
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "target", "recorder.js"));
        await TargetBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(recorderJs);
        TargetBrowser.CoreWebView2.WebMessageReceived += OnTargetMessage;
        TargetBrowser.CoreWebView2.NavigationCompleted += (_, _) =>
            _ = controller.OnTargetNavigationCompletedAsync(TargetBrowser.CoreWebView2.Source);

        TargetBrowser.CoreWebView2.Navigate("about:blank");

        // The zoom lives on the WebView2 control, which is a WPF element — so unlike a headless
        // pane, setting it has to hop back to the UI thread from whatever thread the replay
        // engine is running a step on.
        targetBrowser = new WebView2BrowserSurface(
            TargetBrowser.CoreWebView2,
            factor => Dispatcher.Invoke(() => TargetBrowser.ZoomFactor = factor));
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
                case "togglePanelDetached":
                    TogglePanelDetached();
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

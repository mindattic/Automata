using System.IO;
using System.Runtime.InteropServices;
using Automata.Core.Operator;
using Microsoft.Web.WebView2.Core;

namespace Automata.Browser;

/// <summary>
/// Creates browser lanes hosted in off-screen windows, one per lane, each on its own STA thread
/// with its own message pump and its own user-data folder.
/// <para>
/// <b>Why a real window and not <c>HWND_MESSAGE</c>.</b> A message-only window has no client area,
/// so WebView2 never lays out — and Automata's Click, PressEnter and checkbox steps all dispatch
/// input at coordinates the resolver computed from a rendered box. With no layout there is nothing
/// to hit. So each lane gets an ordinary popup window, sized like a real browser, positioned far
/// off-screen and kept out of the taskbar and the alt-tab list.
/// </para>
/// <para>
/// <b>Why this still needs a logged-on session.</b> WebView2 cannot render in Windows session 0,
/// which is where a scheduled task set to "run whether user is logged on or not" executes. Lanes
/// therefore require an interactive session — see the runner's task registration, which uses an
/// interactive token deliberately.
/// </para>
/// </summary>
public sealed class OffscreenWebView2LaneFactory(string profileRoot) : IBrowserSurfaceFactory
{
    // Far enough off-screen that no plausible monitor arrangement shows it, while still being a
    // real window with real bounds.
    private const int OffScreenX = -32000;
    private const int OffScreenY = -32000;
    private const int LaneWidth = 1280;
    private const int LaneHeight = 900;

    private int created;

    public async Task<IBrowserLane> CreateLaneAsync(string profileKey, CancellationToken ct)
    {
        var laneId = $"lane-{Interlocked.Increment(ref created)}";
        var userDataFolder = Path.Combine(profileRoot, SafeFolder(profileKey));
        Directory.CreateDirectory(userDataFolder);

        var host = new LaneHost(laneId, profileKey, userDataFolder);
        await host.StartAsync(ct);
        return host;
    }

    private static string SafeFolder(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "default" : cleaned;
    }

    /// <summary>
    /// One lane: an STA thread owning a window, its message pump, and the CoreWebView2 built on it.
    /// Everything WebView2 touches happens on that thread; the surface's own calls are already
    /// async and marshalled by WebView2 itself.
    /// </summary>
    private sealed class LaneHost(string laneId, string profileKey, string userDataFolder) : IBrowserLane
    {
        private readonly TaskCompletionSource<IBrowserSurface> ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stopping = new();
        private Thread? thread;
        private IntPtr hwnd;
        private CoreWebView2Controller? controller;

        public string LaneId => laneId;
        public string ProfileKey => profileKey;
        public IBrowserSurface Surface { get; private set; } = null!;

        public async Task StartAsync(CancellationToken ct)
        {
            thread = new Thread(Pump) { IsBackground = true, Name = "automata-" + laneId };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            using var registration = ct.Register(() => ready.TrySetCanceled(ct));
            Surface = await ready.Task;
        }

        private void Pump()
        {
            try
            {
                hwnd = CreateOffscreenWindow();
                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("could not create the lane's host window");

                // The environment/controller creation is async, so it needs the pump running to
                // complete - hence starting it before awaiting anything.
                _ = InitialiseAsync();

                while (!stopping.IsCancellationRequested && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }

        private async Task InitialiseAsync()
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
                // A real rectangle, so the page lays out and coordinate-based input has something
                // to land on.
                controller.Bounds = new System.Drawing.Rectangle(0, 0, LaneWidth, LaneHeight);
                controller.IsVisible = true;
                // The controller is reached straight from here: this lane's WebView2 calls are
                // already made off the pump thread (the awaits above have no synchronisation
                // context to return to), so its zoom is no different.
                ready.TrySetResult(new WebView2BrowserSurface(
                    controller.CoreWebView2, factor => controller.ZoomFactor = factor));
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            stopping.Cancel();
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            stopping.Dispose();
            return ValueTask.CompletedTask;
        }

        // ---- Win32 ---------------------------------------------------------------------------

        private const int WM_CLOSE = 0x0010;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // keeps it out of the taskbar/alt-tab
        private const int WS_EX_NOACTIVATE = 0x08000000;   // never steals focus from the user

        private static IntPtr CreateOffscreenWindow() =>
            CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "STATIC", "Automata lane",
                WS_POPUP,
                OffScreenX, OffScreenY, LaneWidth, LaneHeight,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

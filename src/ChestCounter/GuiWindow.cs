using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChestCounter;

/// <summary>
/// The window of the graphical interface: a real Windows window that shows the page of <see cref="GuiServer"/> with
/// WebView2 (the Edge engine built into Windows 10 and 11). Unlike a browser window, the counter controls it:
/// the close button asks before stopping the counter, minimizing goes to the taskbar or, if chosen, to the notification
/// area, and starting ChestCounter.exe again brings it back.
/// Memory: minimized to the taskbar, the page is suspended; in the notification area (or opened with Fortnite and not
/// shown yet), the web engine is not running at all and is started again when the window is opened.
/// Runs on its own thread (Windows Forms needs one); the counter keeps its main loop.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GuiWindow
{
    private static readonly System.Drawing.Color Night = System.Drawing.Color.FromArgb(15, 27, 36);

    private static Form? _form;
    private static WebView2? _view;
    private static NotifyIcon? _tray;
    private static string _url = "", _cacheFolder = "";
    private static volatile bool _closing;
    private static bool _trayNoticeShown, _asleep;

    /// <summary>Minimizing sends the window to the notification area instead of the taskbar (setting).</summary>
    public static volatile bool MinimizeToTray;

    /// <summary>True when the WebView2 runtime is installed (else the page opens in a browser window).</summary>
    public static bool Available()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    /// <param name="url">The page (with its token).</param>
    /// <param name="cacheFolder">Where WebView2 keeps its data (in the counter's data folder).</param>
    /// <param name="startHidden">Opened with Fortnite: minimized (or in the tray), the game keeps the screen.</param>
    /// <param name="quit">The player confirmed closing: the counter stops.</param>
    public static void Start(string url, string cacheFolder, string title, bool startHidden, Action quit, Func<string> closeQuestion, Func<string> closeTitle, Func<string> openText, Func<string> quitText, Func<string> trayNotice)
    {
        _url = url;
        _cacheFolder = cacheFolder;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var icon = AppInfo.ExePath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
            var form = new ChromelessForm
            {
                Text = title,
                Width = 1200,
                Height = 900,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Night,
                Icon = icon,
                MinimumSize = new System.Drawing.Size(560, 480),
            };

            _tray = new NotifyIcon { Icon = icon, Text = title.Length > 63 ? title[..63] : title, Visible = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add(openText(), null, (_, _) => Restore());
            menu.Items.Add(quitText(), null, (_, _) => AskAndQuit());
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };

            void AskAndQuit()
            {
                Restore();
                if (MessageBox.Show(form, closeQuestion(), closeTitle(), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    _closing = true;
                    quit();
                }
            }

            // The close button: asks, and stops the counter only on "yes".
            form.FormClosing += (_, e) =>
            {
                if (_closing || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
                {
                    return;
                }
                e.Cancel = true;
                AskAndQuit();
            };
            // Minimized: the taskbar (default, page suspended) or the notification area (web engine stopped).
            form.Resize += (_, _) =>
            {
                if (form.WindowState == FormWindowState.Minimized)
                {
                    if (MinimizeToTray)
                    {
                        ToTray(title, trayNotice);
                    }
                    else
                    {
                        Sleep();
                    }
                }
                else if (form.Visible)
                {
                    Wake();
                    TellMaximized();
                }
            };
            // Closed anyway (Windows shutting down, another program closing it): the counter stops too, never left without window.
            form.FormClosed += (_, _) =>
            {
                _tray.Visible = false;
                _tray.Dispose();
                if (!_closing)
                {
                    _closing = true;
                    quit();
                }
            };
            form.Load += (_, _) =>
            {
                if (!startHidden)
                {
                    CreateView();
                }
            };

            _form = form;
            if (startHidden)
            {
                // Opened with Fortnite: no web engine until the player opens the window.
                form.WindowState = FormWindowState.Minimized;
                form.ShowInTaskbar = !MinimizeToTray;
                if (MinimizeToTray)
                {
                    _tray.Visible = true;
                    form.Shown += (_, _) => form.Hide();
                }
            }
            ready.Set();
            Application.Run(form);
        })
        {
            IsBackground = true,
            Name = "GuiWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Starts the web engine and shows the page (again, after the notification area).</summary>
    private static async void CreateView()
    {
        var form = _form;
        if (form == null || _view != null)
        {
            return;
        }
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Night };
        _view = view;
        form.Controls.Add(view);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _cacheFolder);
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            // The page draws its own title bar: its "app-region: drag" parts move the window like a title bar
            // (drag, double click to maximize, snap to the screen edges).
            core.Settings.IsNonClientRegionSupportEnabled = true;
            // The page's minimize / maximize / close buttons.
            core.WebMessageReceived += (_, e) =>
            {
                string? action = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("win", out var a))
                    {
                        action = a.GetString();
                    }
                }
                catch
                {
                    return;
                }
                switch (action)
                {
                    case "min": form.WindowState = FormWindowState.Minimized; break;
                    case "max": form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                    case "close": form.Close(); break; // asks before stopping the counter
                }
            };
            core.NavigationCompleted += (_, _) => TellMaximized();
            // Links that open a new window (GitHub, the OBS preview): in the player's browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true })?.Dispose(); } catch { }
            };
            view.Source = new Uri(_url);
        }
        catch
        {
            // WebView2 could not start: the page in a browser window, as without it
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_url) { UseShellExecute = true })?.Dispose(); } catch { }
        }
    }

    /// <summary>The page shows the maximize or the restore icon.</summary>
    private static void TellMaximized()
    {
        try
        {
            if (_view?.CoreWebView2 is { } core && _form != null)
            {
                core.PostWebMessageAsJson(_form.WindowState == FormWindowState.Maximized ? "{\"maximized\":true}" : "{\"maximized\":false}");
            }
        }
        catch
        {
            // the page is not there yet: it asks again when loaded
        }
    }

    /// <summary>Stops the web engine (its processes end): the window is in the notification area.</summary>
    private static void ReleaseView()
    {
        var view = _view;
        _view = null;
        _asleep = false;
        if (view == null)
        {
            return;
        }
        _form?.Controls.Remove(view);
        view.Dispose();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static void ToTray(string title, Func<string> notice)
    {
        var form = _form!;
        form.Hide();
        _tray!.Visible = true;
        if (!_trayNoticeShown)
        {
            // Once per launch: the counter has not gone, it waits next to the clock.
            _trayNoticeShown = true;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = notice();
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000);
        }
        ReleaseView();
    }

    /// <summary>Minimized to the taskbar: the page is suspended (its scripts stop, WebView2 gives memory back).</summary>
    private static async void Sleep()
    {
        var view = _view;
        if (_asleep || view?.CoreWebView2 == null)
        {
            return;
        }
        _asleep = true;
        try
        {
            view.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            view.Visible = false; // a page can only be suspended when it is not shown
            await view.CoreWebView2.TrySuspendAsync();
        }
        catch
        {
            // not suspended: it simply stays hidden
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static void Wake()
    {
        if (_view == null)
        {
            CreateView();
            return;
        }
        if (!_asleep)
        {
            return;
        }
        _asleep = false;
        try
        {
            _view.CoreWebView2?.Resume();
            if (_view.CoreWebView2 != null) _view.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch
        {
            // resumed by itself when shown
        }
        _view.Visible = true;
    }

    /// <summary>Shows the window again and puts it in front (from the taskbar or the notification area).</summary>
    public static void Restore()
    {
        var form = _form;
        if (form == null || form.IsDisposed)
        {
            return;
        }
        void Show()
        {
            if (_tray != null) _tray.Visible = false;
            form.ShowInTaskbar = true;
            form.Show();
            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }
            Wake();
            form.Activate();
            form.BringToFront();
        }
        if (form.InvokeRequired) form.BeginInvoke(Show); else Show();
    }

    /// <summary>The counter stops: the window closes without asking.</summary>
    public static void Close()
    {
        var form = _form;
        _closing = true;
        if (form == null || form.IsDisposed)
        {
            return;
        }
        try { form.Invoke(form.Close); } catch { }
    }
}

/// <summary>
/// A window without the Windows title bar (the page draws its own, with its buttons), that keeps everything else of a
/// normal window: resizing by the edges, the shadow, the rounded corners of Windows 11, minimize / maximize animations,
/// snapping. The title bar is removed by giving its height to the client area (WM_NCCALCSIZE).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ChromelessForm : Form
{
    private const int WM_NCCALCSIZE = 0x0083;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The frame is computed again without the title bar.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var before = Marshal.PtrToStructure<RECT>(m.LParam);
            base.WndProc(ref m);
            var after = Marshal.PtrToStructure<RECT>(m.LParam);
            // Left, right and bottom keep their resize border; the top loses the title bar. Maximized, Windows puts the
            // window's frame outside the screen: the top keeps that much, so that nothing is cut.
            after.Top = before.Top + (IsZoomed(Handle) ? FrameThickness() : 0);
            Marshal.StructureToPtr(after, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private int FrameThickness()
    {
        var dpi = GetDpiForWindow(Handle);
        return GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

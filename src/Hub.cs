// BWHub — a tray app and window for BWUI widgets: install, set up, and see them above other windows (BWUI DESIGN.md §6.11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bwui;
using WF = System.Windows.Forms;
using Draw = System.Drawing;

namespace BwHub
{
    static class Program
    {
        public const string AppId = "BwHub";

        /// <summary>
        ///   BwHub.exe          start in the tray and open the window (a second start opens the running one's window)
        ///   BwHub.exe --tray   start in the tray only (Start with Windows)
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            args = args ?? new string[0];
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            // --snapshot <png> <page: AppId|settings> [light|dark]: render the window offscreen and exit (docs, checks).
            if (args.Length >= 3 && args[0] == "--snapshot")
            {
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (args.Length >= 4) Theme.Mode = args[3] == "dark" ? ThemeMode.Dark : ThemeMode.Light;
                var hub = new Hub(true);
                new HubWindow(hub).Snapshot(args[1], args[2] == "settings" ? HubWindow.SettingsPage : args[2]);
                return;
            }
            Updater.AfterUpdate(args);
            bool created;
            using (var open = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + AppId + "_Open"))
            using (var mx = new Mutex(true, "Local\\" + AppId + "_Single", out created))
            {
                if (!created) { open.Set(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // A bug in one click shouldn't take the tray app down: log it to error.log and keep running.
                app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
                Theme.Watch();
                var hub = new Hub();
                app.Exit += (s, e) => hub.Dispose();

                // Another start asks this one to show its window.
                var t = new Thread(() =>
                {
                    while (open.WaitOne())
                        app.Dispatcher.BeginInvoke(new Action(hub.ShowWindow));
                }) { IsBackground = true };
                t.Start();

                if (Array.IndexOf(args, "--tray") < 0 && Array.IndexOf(args, "--after-update") < 0)
                    app.Dispatcher.BeginInvoke(new Action(hub.ShowWindow));
                app.Run();
            }
        }

        /// <summary>Appends to %LOCALAPPDATA%\BwHub\error.log, keeping the last 256 KB.</summary>
        public static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var path = System.IO.Path.Combine(App.DataDir(AppId), "error.log");
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + AppInfo.Version + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }

    sealed class Hub : IDisposable
    {
        const int HotkeyPeek = 1, HotkeyEscape = 2;

        /// <summary>Hotkeys to choose from. Only common, rarely taken combinations.</summary>
        public static readonly string[] Hotkeys = { "Ctrl+Alt+W", "Ctrl+Alt+Space", "Ctrl+Shift+Space" };

        readonly Settings settings = new Settings(Program.AppId);
        readonly HwndSource messages;
        readonly WF.NotifyIcon tray;
        readonly DispatcherTimer watch;
        public readonly Updater Updater;
        HubWindow window;

        public string Hotkey { get; private set; }
        public bool HotkeyOk { get; private set; }
        public bool Exiting { get; private set; }

        // While widgets are lifted: the window that had focus last, and the processes that may take it
        // without ending the peek (the widgets and BWHub itself, for menus and this window).
        bool peeking;
        IntPtr lastForeground;
        readonly HashSet<uint> peekProcesses = new HashSet<uint>();

        public Hub() : this(false) { }

        /// <summary>A snapshot hub has no tray, hotkey or updates; it only feeds the window its state.</summary>
        public Hub(bool snapshot)
        {
            var p = new HwndSourceParameters("BWHub") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = HWND_MESSAGE };
            messages = new HwndSource(p);
            Hotkey = settings.Get("hotkey", Hotkeys[0]);
            Updater = new Updater(Program.AppId, new UpdateSource("byeonggwan", "bwhub", "BwHub.exe"), settings, Dispatcher.CurrentDispatcher);
            if (snapshot) { HotkeyOk = true; return; }

            Theme.Mode = Theme.Parse(settings.Get("theme", "system"));
            messages.AddHook(WndProc);
            RegisterPeekHotkey();

            watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            watch.Tick += (s, e) => WatchForeground();

            // A hub that crashed mid-peek can leave widgets lifted; put them all back.
            foreach (var w in Widgets.FindWindows()) Widgets.PostMessage(w.Handle, Remote.PeekMessage, IntPtr.Zero, IntPtr.Zero);

            tray = new WF.NotifyIcon { Icon = MakeIcon(Math.Max(16, WF.SystemInformation.SmallIconSize.Width)), Text = "BWHub", Visible = true };
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == WF.MouseButtons.Left) ShowWindow();
                else if (e.Button == WF.MouseButtons.Right) ShowMenu();
            };

            if (Updater.IsInstalledCopy) Updater.Start();
            // Rewrite an existing Run value so it has this exe's path and --tray (0.1.0 builds wrote it without).
            if (StartsWithWindows) StartsWithWindows = true;
            Application.Current.SessionEnding += (s, e) => Exiting = true;
        }

        public void Dispose()
        {
            Exiting = true;
            if (peeking) EndPeek();
            UnregisterHotKey(messages.Handle, HotkeyPeek);
            Updater.Stop();
            tray.Visible = false;
            tray.Dispose();
            messages.Dispose();
        }

        public void ShowWindow()
        {
            if (window == null) window = new HubWindow(this);
            window.ShowAndActivate();
        }

        void Exit()
        {
            Exiting = true;
            Application.Current.Shutdown();
        }

        /// <summary>BWHub's own theme: system, light or dark (its window and tray menu).</summary>
        public ThemeMode ThemeMode
        {
            get { return Theme.Mode; }
            set { settings.Set("theme", Theme.ToKey(value)); Theme.Mode = value; }
        }

        public bool StartsWithWindows
        {
            get { return Widgets.StartsWithWindows(Program.AppId); }
            set
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                Widgets.SetStartsWithWindows(Program.AppId, "\"" + exe + "\" --tray", value);
            }
        }

        // ---------- peek ----------

        void TogglePeek()
        {
            if (peeking) EndPeek(); else StartPeek();
        }

        bool StartPeek()
        {
            var widgets = Widgets.FindWindows();
            if (widgets.Count == 0)
            {
                tray.ShowBalloonTip(4000, "BWHub", Text.T("실행 중인 위젯이 없어요.", "No widgets are running."), WF.ToolTipIcon.None);
                return false;
            }
            peeking = true;
            peekProcesses.Clear();
            peekProcesses.Add((uint)Process.GetCurrentProcess().Id);
            foreach (var w in widgets)
            {
                peekProcesses.Add(w.ProcessId);
                Widgets.PostMessage(w.Handle, Remote.PeekMessage, new IntPtr(1), IntPtr.Zero);
            }
            lastForeground = GetForegroundWindow();
            RegisterHotKey(messages.Handle, HotkeyEscape, MOD_NOREPEAT, VK_ESCAPE);
            watch.Start();
            return true;
        }

        void EndPeek()
        {
            peeking = false;
            watch.Stop();
            UnregisterHotKey(messages.Handle, HotkeyEscape);
            foreach (var w in Widgets.FindWindows()) Widgets.PostMessage(w.Handle, Remote.PeekMessage, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Ends the peek when focus moves to anything but a widget or BWHub: another app, the desktop, a link a widget opened.</summary>
        void WatchForeground()
        {
            var fg = GetForegroundWindow();
            if (fg == lastForeground || fg == IntPtr.Zero) return;
            uint pid;
            Widgets.GetWindowThreadProcessId(fg, out pid);
            if (peekProcesses.Contains(pid)) { lastForeground = fg; return; }
            EndPeek();
        }

        /// <summary>Lifts the widgets and opens one widget's own right-click menu (accounts and other widget items).</summary>
        public void ShowWidgetMenu(CatalogEntry e)
        {
            if (!peeking) StartPeek();
            foreach (var w in Widgets.WindowsOf(e))
            {
                AllowSetForegroundWindow(w.ProcessId);
                Widgets.PostMessage(w.Handle, Remote.CommandMessage, new IntPtr(Remote.ShowMenu), IntPtr.Zero);
                break;
            }
        }

        // ---------- hotkey ----------

        public void SetHotkey(string hotkey)
        {
            Hotkey = hotkey;
            settings.Set("hotkey", hotkey);
            RegisterPeekHotkey();
        }

        void RegisterPeekHotkey()
        {
            UnregisterHotKey(messages.Handle, HotkeyPeek);
            uint mods, vk;
            HotkeyOk = ParseHotkey(Hotkey, out mods, out vk) && RegisterHotKey(messages.Handle, HotkeyPeek, mods | MOD_NOREPEAT, vk);
        }

        /// <summary>"Ctrl+Alt+W" → modifiers and a virtual key.</summary>
        static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var part in text.Split('+'))
            {
                var t = part.Trim();
                if (t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= MOD_CONTROL;
                else if (t.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= MOD_ALT;
                else if (t.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= MOD_SHIFT;
                else if (t.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= MOD_WIN;
                else
                {
                    Key key;
                    if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
                    if (!Enum.TryParse(t, true, out key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                }
            }
            return vk != 0 && mods != 0;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyPeek) TogglePeek();
                else if (id == HotkeyEscape && peeking) EndPeek();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------- tray ----------

        void ShowMenu()
        {
            var m = new ContextMenu { Placement = PlacementMode.MousePoint };
            m.Resources.MergedDictionaries.Add(Theme.Resources);
            m.Items.Add(BwuiWindow.Item(Text.T("BWHub 열기", "Open BWHub"), ShowWindow));
            m.Items.Add(BwuiWindow.Item(peeking ? Text.T("위젯 내리기", "Hide widgets")
                                                : Text.T("위젯 보기", "Show widgets") + (HotkeyOk ? "  (" + Hotkey + ")" : ""), TogglePeek));
            m.Items.Add(new Separator());
            m.Items.Add(BwuiWindow.Item(Text.Exit, Exit));

            // A tray menu has to own the foreground, or a click elsewhere won't close it.
            m.Opened += (s, e) =>
            {
                var src = PresentationSource.FromVisual(m) as HwndSource;
                if (src != null) SetForegroundWindow(src.Handle);
            };
            m.IsOpen = true;
        }

        // ---------- icon: a rounded square with four tiles ----------

        public static ImageSource IconImage()
        {
            using (var icon = MakeIcon(32))
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
        }

        static Draw.Icon MakeIcon(int s)
        {
            using (var bmp = new Draw.Bitmap(s, s))
            {
                using (var g = Draw.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Draw.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Draw.Color.Transparent);
                    float u = s / 16f;
                    using (var bg = new Draw.SolidBrush(Draw.Color.FromArgb(0x25, 0x63, 0xEB)))
                        FillRound(g, bg, 0.5f * u, 0.5f * u, 15 * u, 15 * u, 4 * u);
                    using (var fg = new Draw.SolidBrush(Draw.Color.White))
                    {
                        FillRound(g, fg, 3.5f * u, 3.5f * u, 4 * u, 4 * u, 1.2f * u);
                        FillRound(g, fg, 8.5f * u, 3.5f * u, 4 * u, 4 * u, 1.2f * u);
                        FillRound(g, fg, 3.5f * u, 8.5f * u, 4 * u, 4 * u, 1.2f * u);
                        FillRound(g, fg, 8.5f * u, 8.5f * u, 4 * u, 4 * u, 1.2f * u);
                    }
                }
                return Draw.Icon.FromHandle(bmp.GetHicon());
            }
        }

        static void FillRound(Draw.Graphics g, Draw.Brush b, float x, float y, float w, float h, float r)
        {
            using (var path = new Draw.Drawing2D.GraphicsPath())
            {
                float d = r * 2;
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        // ---------- Win32 ----------

        const int WM_HOTKEY = 0x312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000, VK_ESCAPE = 0x1B;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    }
}

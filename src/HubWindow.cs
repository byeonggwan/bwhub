// BWHub — the main window: a sidebar of widgets, and the selected widget's preview, actions and settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bwui;

namespace BwHub
{
    /// <summary>The window's colors, from the BWUI palette.</summary>
    sealed class Look
    {
        public Color Side, Main, Stage, Selected, Hover, SegTrack, SegOn, SegOnBorder, SwitchOff, ButtonBorder, Thumb,
            Text, Text2, Text3, Divider, Success, Caution, Critical;
        public bool Dark, HighContrast;

        static Color H(string hex) { return Palette.Hex(hex); }

        public static Look Of(Palette p)
        {
            var l = new Look
            {
                Dark = p.IsDark, HighContrast = p.IsHighContrast, Stage = p.Desk,
                Text = p.TextPrimary, Text2 = p.TextSecondary, Text3 = p.TextTertiary, Divider = p.Divider,
                Success = p.Success, Caution = p.Caution, Critical = p.Critical
            };
            if (p.IsHighContrast)
            {
                l.Side = l.Main = l.SegTrack = l.SegOn = p.Surface;
                l.Selected = l.Hover = p.SurfaceHover;
                l.SegOnBorder = l.ButtonBorder = l.SwitchOff = l.Thumb = p.Border;
            }
            else if (p.IsDark)
            {
                l.Side = H("#232220"); l.Main = p.Surface; l.Selected = H("#3A3936"); l.Hover = H("#302F2C");
                l.SegTrack = H("#232220"); l.SegOn = H("#45433F"); l.SegOnBorder = H("#55534E");
                l.SwitchOff = H("#55534E"); l.ButtonBorder = H("#4A4844"); l.Thumb = H("#55534E");
            }
            else
            {
                l.Side = H("#F4F3EF"); l.Main = p.Surface; l.Selected = H("#E7E5DE"); l.Hover = H("#ECEAE4");
                l.SegTrack = H("#EEECE6"); l.SegOn = p.Surface; l.SegOnBorder = H("#D6D4CC");
                l.SwitchOff = H("#CFCDC5"); l.ButtonBorder = H("#D6D4CC"); l.Thumb = H("#CFCDC5");
            }
            return l;
        }
    }

    sealed class HubWindow : Window
    {
        public const string SettingsPage = "#settings";
        static readonly TimeSpan ReleasesFresh = TimeSpan.FromMinutes(10);

        readonly Hub hub;
        readonly Grid root = new Grid();
        readonly ScrollViewer detail = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        Look L;
        string selected;

        // Latest releases (by AppId), fetched when the window opens and at most every 10 minutes.
        readonly Dictionary<string, Release> releases = new Dictionary<string, Release>();
        readonly Dictionary<string, string> releaseErrors = new Dictionary<string, string>();
        DateTime releasesAt = DateTime.MinValue;
        bool fetching;

        // Per widget: what it's doing now ("Installing…") or why the last action failed.
        readonly Dictionary<string, string> busy = new Dictionary<string, string>();
        readonly Dictionary<string, string> errors = new Dictionary<string, string>();
        string lastState;

        public HubWindow(Hub hub)
        {
            this.hub = hub;
            Title = "BWHub";
            Width = 780;
            Height = 620;
            MinWidth = 640;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily(Tokens.FontFamily);
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(Theme.Resources);
            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ScrollXaml));
            Icon = Hub.IconImage();

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(detail, 1);
            Content = root;

            foreach (var e in Catalog.All) if (selected == null && e.Installed) selected = e.AppId;
            if (selected == null) selected = Catalog.All[0].AppId;

            EventHandler onTheme = (s, e) => Render();
            Theme.Changed += onTheme;
            hub.Updater.Changed += (s, e) => { if (IsVisible) Render(); };
            Closed += (s, e) => Theme.Changed -= onTheme;
            SourceInitialized += (s, e) => ApplyTitleBar();

            // Closing only hides; BWHub keeps running in the tray.
            Closing += (s, e) => { if (!hub.Exiting) { e.Cancel = true; Hide(); } };
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { Render(); FetchReleases(false); poll.Start(); }
                else poll.Stop();
            };
            // Picks up widgets started, stopped or changed elsewhere; re-renders only when something changed.
            poll.Tick += (s, e) => { if (State() != lastState) Render(); };
            Render();
        }

        /// <summary>Renders the window's content for one page to a PNG at 2x, without showing it (--snapshot).</summary>
        public void Snapshot(string file, string page)
        {
            selected = page;
            foreach (var e in Catalog.All)
            {
                try { releases[e.AppId] = GitHub.Latest(e.Repo); } catch { }
                Previews.Ensure(e, Theme.Current.IsDark);
            }
            releasesAt = DateTime.UtcNow;
            Render();
            var size = new Size(Width, Height - 32);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var host = new Border { Background = Br(L.Main), Child = null };
            var rtb = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Br(L.Main), null, new Rect(size));
            rtb.Render(bg);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // ---------- data ----------

        void FetchReleases(bool force)
        {
            if (fetching || (!force && DateTime.UtcNow - releasesAt < ReleasesFresh)) return;
            fetching = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var got = new Dictionary<string, Release>();
                var errs = new Dictionary<string, string>();
                foreach (var e in Catalog.All)
                {
                    try { got[e.AppId] = GitHub.Latest(e.Repo); }
                    catch (Exception ex) { errs[e.AppId] = ex.Message; }
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    releases.Clear(); releaseErrors.Clear();
                    foreach (var kv in got) releases[kv.Key] = kv.Value;
                    foreach (var kv in errs) releaseErrors[kv.Key] = kv.Value;
                    releasesAt = DateTime.UtcNow;
                    fetching = false;
                    Render();
                }));
            });
        }

        Release LatestOf(string appId)
        {
            Release r;
            return releases.TryGetValue(appId, out r) ? r : null;
        }

        /// <summary>A fingerprint of everything shown from outside: install, version, running, settings.</summary>
        string State()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in Catalog.All)
            {
                sb.Append(e.AppId).Append(e.Installed).Append(e.InstalledVersion).Append(Widgets.IsRunning(e))
                  .Append(Widgets.StartsWithWindows(e.AppId));
                try { sb.Append(File.GetLastWriteTimeUtc(new Settings(e.AppId).FilePath).Ticks); } catch { }
            }
            sb.Append(hub.Hotkey).Append(hub.HotkeyOk).Append(hub.StartsWithWindows).Append(hub.Updater.State);
            return sb.ToString();
        }

        void Run(CatalogEntry e, string doing, Action work)
        {
            if (busy.ContainsKey(e.AppId)) return;
            busy[e.AppId] = doing;
            errors.Remove(e.AppId);
            Render();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { work(); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy.Remove(e.AppId);
                    if (error != null) errors[e.AppId] = error;
                    Render();
                }));
            });
        }

        void SetWidgetSetting(CatalogEntry e, string key, string value)
        {
            new Settings(e.AppId).Set(key, value);
            if (Widgets.IsRunning(e))
            {
                if (Widgets.WindowsOf(e).Count > 0) Widgets.Post(e, Remote.CommandMessage, Remote.ReloadSettings);
                // A widget older than BWUI 0.3.0 can't reload; restart it so it reads the new settings.
                else Run(e, Text.T("다시 시작하는 중…", "Restarting…"), () => { Widgets.Stop(e); Widgets.Start(e); });
            }
            Render();
        }

        // ---------- layout ----------

        void Render()
        {
            L = Look.Of(Theme.Current);
            lastState = State();
            ApplyTitleBar();
            Background = Br(L.Main);
            Resources["Hub.Thumb"] = Br(L.Thumb);

            double offset = detail.VerticalOffset;
            root.Children.Clear();
            var side = Sidebar();
            root.Children.Add(side);
            detail.Content = selected == SettingsPage ? SettingsView() : WidgetView(Find(selected));
            root.Children.Add(detail);
            detail.ScrollToVerticalOffset(offset);
        }

        static CatalogEntry Find(string appId)
        {
            foreach (var e in Catalog.All) if (e.AppId == appId) return e;
            return Catalog.All[0];
        }

        void Select(string page)
        {
            if (page == selected) return;
            selected = page;
            detail.ScrollToTop();
            Render();
        }

        UIElement Sidebar()
        {
            var dock = new DockPanel { Background = Br(L.Side) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 18, 20, 14) };
            head.Children.Add(new Image { Source = Hub.IconImage(), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Label("BWHub", 14, L.Text, true, new Thickness(8, 0, 0, 0)));
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            var settings = SideItem(SettingsPage, GlyphOnly(""), Text.T("설정", "Settings"), null);
            settings.Margin = new Thickness(10, 4, 10, 12);
            DockPanel.SetDock(settings, Dock.Bottom);
            dock.Children.Add(settings);

            var list = new StackPanel();
            var installed = new List<CatalogEntry>();
            var available = new List<CatalogEntry>();
            foreach (var e in Catalog.All) (e.Installed ? installed : available).Add(e);
            if (installed.Count > 0)
            {
                list.Children.Add(SectionLabel(Text.T("설치됨", "Installed")));
                foreach (var e in installed)
                {
                    bool running = Widgets.IsRunning(e);
                    var dot = new Ellipse { Width = 7, Height = 7, Fill = Br(running ? L.Success : L.Text3), VerticalAlignment = VerticalAlignment.Center };
                    dot.ToolTip = running ? Text.T("실행 중", "Running") : Text.T("꺼짐", "Off");
                    list.Children.Add(SideItem(e.AppId, IconTile(e, 28), e.Name, dot));
                }
            }
            if (available.Count > 0)
            {
                list.Children.Add(SectionLabel(Text.T("더 받기", "Get more")));
                foreach (var e in available)
                    list.Children.Add(SideItem(e.AppId, IconTile(e, 28), e.Name, Glyph("", 12, L.Text3)));
            }
            dock.Children.Add(list);
            return dock;
        }

        FrameworkElement SectionLabel(string text)
        {
            return Label(text, 12, L.Text3, false, new Thickness(20, 12, 20, 6));
        }

        Border SideItem(string page, FrameworkElement icon, string text, FrameworkElement trailing)
        {
            bool on = page == selected;
            var d = new DockPanel { LastChildFill = true };
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            d.Children.Add(icon);
            if (trailing != null)
            {
                trailing.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(trailing, Dock.Right);
                d.Children.Add(trailing);
            }
            var t = Label(text, 13, L.Text, on, new Thickness(10, 0, 0, 0));
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            d.Children.Add(t);
            var b = new Border
            {
                Height = 40, Padding = new Thickness(8, 0, 10, 0), Margin = new Thickness(10, 1, 10, 1),
                CornerRadius = new CornerRadius(8), Background = on ? Br(L.Selected) : Brushes.Transparent,
                Cursor = Cursors.Hand, Child = d
            };
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            if (!on)
            {
                b.MouseEnter += (s, e) => b.Background = Br(L.Hover);
                b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            }
            b.MouseLeftButtonUp += (s, e) => Select(page);
            return b;
        }

        // ---------- widget page ----------

        UIElement WidgetView(CatalogEntry e)
        {
            var s = Page();
            bool isInstalled = e.Installed, isRunning = Widgets.IsRunning(e);
            var latest = LatestOf(e.AppId);
            var installedVersion = e.InstalledVersion;
            var iv = Updater.ParseVersion(installedVersion);
            bool hasUpdate = isInstalled && latest != null && latest.Version != null && iv != null && latest.Version > iv;
            // Builds before BWUI 0.3.0 don't carry a version; offer the latest release without calling it an update.
            bool unknownVersion = isInstalled && iv == null;

            s.Children.Add(Stage(e));

            // Name, description, status — and the actions on the right
            var head = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0) };
            string doing;
            if (busy.TryGetValue(e.AppId, out doing))
                actions.Children.Add(Label(doing, 13, L.Text2, false, new Thickness(0, 6, 0, 0)));
            else
            {
                var more = new List<KeyValuePair<string, Action>>();
                if (!isInstalled)
                    actions.Children.Add(Button(Text.T("설치", "Install"), true, latest != null, () => Run(e, Text.T("설치하는 중…", "Installing…"), () => Installer.Install(e, latest))));
                else
                {
                    if (hasUpdate)
                        actions.Children.Add(Button(Text.T("업데이트", "Update"), true, true, () => Run(e, Text.T("업데이트하는 중…", "Updating…"), () => Installer.Install(e, latest))));
                    if (isRunning)
                    {
                        bool remote = Widgets.WindowsOf(e).Count > 0;
                        var menu = Button(Text.T("위젯 메뉴", "Widget menu"), false, remote, () => hub.ShowWidgetMenu(e));
                        if (!remote)
                        {
                            menu.ToolTip = Text.T("이 버전의 위젯은 BWHub에서 메뉴를 열 수 없어요. 업데이트하면 돼요.", "This widget version can't open its menu from BWHub. Update it first.");
                            ToolTipService.SetShowOnDisabled(menu, true);
                        }
                        actions.Children.Add(menu);
                        more.Add(Pair(Text.T("다시 시작", "Restart"), () => Run(e, Text.T("다시 시작하는 중…", "Restarting…"), () => { Widgets.Stop(e); Widgets.Start(e); })));
                        more.Add(Pair(Text.T("종료", "Exit"), () => Run(e, Text.T("종료하는 중…", "Exiting…"), () => Widgets.Stop(e))));
                    }
                    else
                        actions.Children.Add(Button(Text.T("실행", "Start"), !hasUpdate, true, () => { Widgets.Start(e); Render(); }));
                    if (unknownVersion && latest != null)
                        more.Add(Pair(Text.T("최신 버전으로 다시 설치", "Reinstall the latest version"), () => Run(e, Text.T("설치하는 중…", "Installing…"), () => Installer.Install(e, latest))));
                }
                more.Add(Pair(Text.T("GitHub에서 보기", "View on GitHub"), () => Open(e.RepoUrl)));
                actions.Children.Add(MoreButton(more));
            }
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);

            var info = new StackPanel();
            info.Children.Add(Label(e.Name, 20, L.Text, true, new Thickness(0)));
            var desc = Label(e.Description, 13, L.Text2, false, new Thickness(0, 4, 0, 0));
            desc.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(desc);
            info.Children.Add(StatusLine(e, isInstalled, isRunning, installedVersion, latest, hasUpdate));
            head.Children.Add(info);
            s.Children.Add(head);

            string error;
            if (errors.TryGetValue(e.AppId, out error))
            {
                var t = Label(error, 12, L.Critical, false, new Thickness(0, 10, 0, 0));
                t.TextWrapping = TextWrapping.Wrap;
                s.Children.Add(t);
            }

            if (isInstalled)
            {
                var set = new Settings(e.AppId);
                s.Children.Add(Gap(16));
                var theme = Theme.Parse(set.Get("theme", "system"));
                s.Children.Add(Row(Text.T("테마", "Theme"), null, Segmented(ThemeNames(), ThemeIndex(theme),
                    i => SetWidgetSetting(e, "theme", Theme.ToKey(ThemeAt(i))))));
                if (e.Sizes.Length > 1)
                {
                    var names = new List<string>();
                    int current = Math.Max(0, Array.IndexOf(e.Sizes, WidgetSize.Regular));
                    var key = set.Get("size", "regular");
                    for (int i = 0; i < e.Sizes.Length; i++)
                    {
                        var z = e.Sizes[i];
                        names.Add(z == WidgetSize.Compact ? Text.T("작게", "Compact") : z == WidgetSize.Wide ? Text.T("넓게", "Wide") : Text.T("보통", "Regular"));
                        if (z.ToString().ToLowerInvariant() == key) current = i;
                    }
                    s.Children.Add(Row(Text.T("크기", "Size"), null, Segmented(names.ToArray(), current,
                        i => SetWidgetSetting(e, "size", e.Sizes[i].ToString().ToLowerInvariant()))));
                }
                s.Children.Add(Row(Text.StartWithWindows, null, Switch(Widgets.StartsWithWindows(e.AppId),
                    on => { Widgets.SetStartsWithWindows(e.AppId, "\"" + e.ExePath + "\"", on); Render(); })));
                s.Children.Add(Row(Text.AutoUpdate, Text.T("새 버전이 나오면 바로 설치해요", "Installs new versions as they come out"),
                    Switch(set.GetBool("update_auto", false), on => SetWidgetSetting(e, "update_auto", on ? "1" : "0"))));
            }
            else
            {
                var t = Label(Text.T("설치하면 테마, 크기, 시작 시 실행을 여기서 바꿀 수 있어요.", "Once installed, you can change its theme, size and startup here."),
                    13, L.Text3, false, new Thickness(0, 24, 0, 0));
                t.TextWrapping = TextWrapping.Wrap;
                s.Children.Add(t);
            }
            return s;
        }

        /// <summary>The widget's snapshot on a desktop-colored stage.</summary>
        UIElement Stage(CatalogEntry e)
        {
            var stage = new Border
            {
                Background = Br(L.Stage), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16), MinHeight = 180
            };
            var path = Previews.Get(e, L.Dark, Dispatcher, Render);
            ImageSource img = null;
            if (path != null)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    img = bi;
                }
                catch { }
            }
            if (img != null)
            {
                // Snapshots are rendered at 2x; show them at their real size, smaller if the window is narrow.
                var pic = new Image { Source = img, Stretch = Stretch.Uniform, MaxWidth = ((BitmapSource)img).PixelWidth / 2.0, MaxHeight = 300 };
                RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
                stage.Child = pic;
            }
            else
            {
                var tile = IconTile(e, 56);
                tile.HorizontalAlignment = HorizontalAlignment.Center;
                tile.VerticalAlignment = VerticalAlignment.Center;
                stage.Child = tile;
            }
            return stage;
        }

        FrameworkElement StatusLine(CatalogEntry e, bool installed, bool running, string version, Release latest, bool hasUpdate)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var parts = new List<KeyValuePair<string, Color>>();
            if (installed)
            {
                line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Br(running ? L.Success : L.Text3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
                parts.Add(new KeyValuePair<string, Color>(running ? Text.T("실행 중", "Running") : Text.T("꺼짐", "Off"), running ? L.Success : L.Text2));
                parts.Add(new KeyValuePair<string, Color>(version != null ? "v" + version : Text.T("버전 알 수 없음", "Unknown version"), L.Text3));
                if (hasUpdate) parts.Add(new KeyValuePair<string, Color>(latest.Tag + Text.T(" 업데이트 있음", " available"), L.Caution));
                else if (latest != null && version != null) parts.Add(new KeyValuePair<string, Color>(Text.T("최신", "Up to date"), L.Text3));
                else if (latest != null) parts.Add(new KeyValuePair<string, Color>(Text.T("최신 ", "Latest ") + latest.Tag, L.Text3));
            }
            else
            {
                parts.Add(new KeyValuePair<string, Color>(Text.T("설치 안 됨", "Not installed"), L.Text2));
                if (latest != null) parts.Add(new KeyValuePair<string, Color>(Text.T("최신 ", "Latest ") + latest.Tag, L.Text3));
            }
            if (latest == null && releaseErrors.ContainsKey(e.AppId)) parts.Add(new KeyValuePair<string, Color>(Text.T("최신 버전 확인 실패", "Couldn't check for updates"), L.Text3));
            for (int i = 0; i < parts.Count; i++)
                line.Children.Add(Label((i > 0 ? "  ·  " : "") + parts[i].Key, 12, i == 0 ? parts[i].Value : parts[i].Value, false, new Thickness(0)));
            return line;
        }

        // ---------- settings page ----------

        UIElement SettingsView()
        {
            var s = Page();
            s.Children.Add(Label(Text.T("설정", "Settings"), 20, L.Text, true, new Thickness(0, 0, 0, 4)));

            s.Children.Add(Group(Text.T("모양", "Appearance")));
            s.Children.Add(Row(Text.T("BWHub 테마", "BWHub theme"), null, Segmented(ThemeNames(), ThemeIndex(hub.ThemeMode),
                i => hub.ThemeMode = ThemeAt(i))));   // re-renders through Theme.Changed
            var installed = new List<CatalogEntry>();
            foreach (var e in Catalog.All) if (e.Installed) installed.Add(e);
            if (installed.Count > 0)
            {
                // Selected only when every installed widget has the same theme.
                int common = -1;
                foreach (var e in installed)
                {
                    int t = ThemeIndex(Theme.Parse(new Settings(e.AppId).Get("theme", "system")));
                    common = common == -1 ? t : common == t ? t : -2;
                }
                s.Children.Add(Row(Text.T("모든 위젯 테마", "Theme for all widgets"), Text.T("설치된 위젯 " + installed.Count + "개에 한 번에 적용해요", "Applies to all " + installed.Count + " installed widgets"),
                    Segmented(ThemeNames(), common, i => { foreach (var e in installed) SetWidgetSetting(e, "theme", Theme.ToKey(ThemeAt(i))); })));
            }

            s.Children.Add(Group(Text.T("위젯 보기", "Show widgets")));
            int hk = Array.IndexOf(Hub.Hotkeys, hub.Hotkey);
            s.Children.Add(Row(Text.T("단축키", "Hotkey"),
                hub.HotkeyOk ? Text.T("모든 위젯을 다른 창 위로 올려요. Esc나 다른 앱으로 넘어가면 돌아가요.", "Lifts every widget above other windows. Esc or switching apps puts them back.")
                             : Text.T("다른 앱이 이 단축키를 쓰고 있어요. 다른 걸 고르세요.", "Another app uses this hotkey. Pick another one."),
                Segmented(Hub.Hotkeys, hk, i => { hub.SetHotkey(Hub.Hotkeys[i]); Render(); }), !hub.HotkeyOk, true));

            s.Children.Add(Group(Text.T("일반", "General")));
            s.Children.Add(Row(Text.StartWithWindows, Text.T("로그인하면 트레이에서 조용히 시작해요", "Starts quietly in the tray when you sign in"),
                Switch(hub.StartsWithWindows, on => { hub.StartsWithWindows = on; Render(); })));
            if (hub.Updater.IsInstalledCopy)
            {
                var u = hub.Updater;
                s.Children.Add(Row(Text.AutoUpdate, null, Switch(u.Auto, on => { u.Auto = on; Render(); })));
                FrameworkElement action;
                if (u.State == UpdateState.Available) action = Button(Text.InstallUpdate(u.LatestTag), true, true, u.Install);
                else if (u.State == UpdateState.Checking || u.State == UpdateState.Installing) action = Label(u.State == UpdateState.Checking ? Text.Checking : Text.Updating, 13, L.Text2, false, new Thickness(0));
                else action = Button(u.State == UpdateState.UpToDate ? Text.UpToDate : Text.CheckForUpdates, false, true, () => { u.Check(true); Render(); });
                s.Children.Add(Row(Text.T("버전", "Version"), "BWHub v" + AppInfo.Version + (u.State == UpdateState.Failed && u.Error != null ? " · " + Text.UpdateFailed : ""), action));
            }
            else
                s.Children.Add(Row(Text.T("버전", "Version"), "BWHub v" + AppInfo.Version + Text.T(" · 개발 빌드", " · development build"), new Border()));
            return s;
        }

        // ---------- building blocks ----------

        StackPanel Page()
        {
            return new StackPanel { Margin = new Thickness(32, 24, 32, 32), MaxWidth = 620 };
        }

        static FrameworkElement Gap(double h) { return new Border { Height = h }; }

        FrameworkElement Group(string text)
        {
            return Label(text, 12, L.Text3, true, new Thickness(0, 24, 0, 2));
        }

        /// <summary>
        /// A settings row: label (and a hint below it) on the left, the control on the right, a hairline above.
        /// Stacked puts a wide control below the label instead.
        /// </summary>
        FrameworkElement Row(string label, string hint, FrameworkElement control, bool hintIsError = false, bool stacked = false)
        {
            var g = new Grid { MinHeight = 52 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
            if (stacked)
            {
                left.Margin = new Thickness(0, 12, 0, 14);
                Grid.SetColumnSpan(left, 2);
            }
            left.Children.Add(Label(label, 13, L.Text, false, new Thickness(0)));
            if (!string.IsNullOrEmpty(hint))
            {
                var h = Label(hint, 12, hintIsError ? L.Critical : L.Text3, false, new Thickness(0, 2, 0, 0));
                h.TextWrapping = TextWrapping.Wrap;
                left.Children.Add(h);
            }
            g.Children.Add(left);
            if (stacked)
            {
                control.HorizontalAlignment = HorizontalAlignment.Left;
                control.Margin = new Thickness(0, 10, 0, 0);
                left.Children.Add(control);
            }
            else
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                g.Children.Add(control);
            }
            return new Border { BorderBrush = Br(L.Divider), BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
        }

        TextBlock Label(string text, double size, Color color, bool bold, Thickness margin)
        {
            return new TextBlock
            {
                Text = text, FontSize = size, Foreground = Br(color), Margin = margin,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center
            };
        }

        TextBlock Glyph(string glyph, double size, Color color)
        {
            return new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Tokens.IconFont), FontSize = size, Foreground = Br(color),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        FrameworkElement GlyphOnly(string glyph)
        {
            return new Border { Width = 28, Height = 28, Child = Glyph(glyph, 15, L.Text2) };
        }

        FrameworkElement IconTile(CatalogEntry e, double size)
        {
            Color fill = L.HighContrast ? L.Main : Palette.Hex(L.Dark ? e.Tint[2] : e.Tint[0]);
            Color fg = L.HighContrast ? L.Text : Palette.Hex(L.Dark ? e.Tint[3] : e.Tint[1]);
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
                Background = Br(fill), Child = Glyph(e.Glyph, size * 0.5, fg),
                BorderBrush = L.HighContrast ? Br(L.Text) : null, BorderThickness = new Thickness(L.HighContrast ? 1 : 0)
            };
        }

        /// <summary>Primary (filled) or secondary (outlined) button, height 32.</summary>
        Border Button(string text, bool primary, bool enabled, Action onClick)
        {
            Brush normal = primary ? Br(L.Text) : Brushes.Transparent;
            Brush hover = primary ? Br(L.Text) : Br(L.Hover);
            var b = new Border
            {
                Height = 32, MinWidth = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
                CornerRadius = new CornerRadius(8), Background = normal,
                BorderBrush = primary ? null : Br(L.ButtonBorder), BorderThickness = new Thickness(primary ? 0 : 1),
                Child = Label(text, 13, primary ? L.Main : L.Text, primary, new Thickness(0)),
                Cursor = enabled ? Cursors.Hand : null, Opacity = enabled ? 1 : 0.45
            };
            ((TextBlock)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            if (enabled)
            {
                b.MouseEnter += (s, e) => { b.Background = hover; if (primary) b.Opacity = 0.86; };
                b.MouseLeave += (s, e) => { b.Background = normal; b.Opacity = 1; };
                b.MouseLeftButtonUp += (s, e) => onClick();
            }
            return b;
        }

        /// <summary>"⋯" button that opens a BWUI-styled menu below itself.</summary>
        static KeyValuePair<string, Action> Pair(string header, Action onClick) { return new KeyValuePair<string, Action>(header, onClick); }

        Border MoreButton(List<KeyValuePair<string, Action>> items)
        {
            Border b = null;
            b = Button("", false, true, () =>
            {
                var m = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
                m.Resources.MergedDictionaries.Add(Theme.Resources);
                foreach (var i in items) m.Items.Add(BwuiWindow.Item(i.Key, i.Value));   // new items each time: an item belongs to one menu
                m.IsOpen = true;
            });
            b.Padding = new Thickness(0);
            b.Width = 32;
            b.Child = Glyph(Glyphs.More, 14, L.Text);
            b.ToolTip = Text.T("더 보기", "More");
            System.Windows.Automation.AutomationProperties.SetName(b, Text.T("더 보기", "More"));
            return b;
        }

        /// <summary>One connected control with one selected segment (or none, with selected = -1).</summary>
        FrameworkElement Segmented(string[] options, int selected, Action<int> onSelect)
        {
            var track = new Border { Background = Br(L.SegTrack), CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++)
            {
                int k = i;
                bool on = i == selected;
                var seg = new Border
                {
                    Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(6),
                    Background = on ? Br(L.SegOn) : Brushes.Transparent,
                    BorderBrush = on ? Br(L.SegOnBorder) : Brushes.Transparent, BorderThickness = new Thickness(1),
                    Child = Label(options[i], 12.5, on ? L.Text : L.Text2, on, new Thickness(0)),
                    Cursor = on ? null : Cursors.Hand
                };
                System.Windows.Automation.AutomationProperties.SetName(seg, options[i]);
                if (!on)
                {
                    seg.MouseEnter += (s, e) => seg.Background = Br(L.Hover);
                    seg.MouseLeave += (s, e) => seg.Background = Brushes.Transparent;
                    seg.MouseLeftButtonUp += (s, e) => onSelect(k);
                }
                row.Children.Add(seg);
            }
            track.Child = row;
            return track;
        }

        /// <summary>Windows-style on/off switch.</summary>
        FrameworkElement Switch(bool on, Action<bool> onChange)
        {
            var knob = new Ellipse
            {
                Width = 14, Height = 14, Fill = Br(on ? L.Main : Colors.White),
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0)
            };
            var b = new Border
            {
                Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
                Background = Br(on ? L.Text : L.SwitchOff), Child = knob, Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(b, on ? Text.T("켜짐", "On") : Text.T("꺼짐", "Off"));
            b.MouseLeftButtonUp += (s, e) => onChange(!on);
            return b;
        }

        static string[] ThemeNames() { return new[] { Text.T("시스템", "System"), Text.T("라이트", "Light"), Text.T("다크", "Dark") }; }
        static int ThemeIndex(ThemeMode m) { return m == ThemeMode.Light ? 1 : m == ThemeMode.Dark ? 2 : 0; }
        static ThemeMode ThemeAt(int i) { return i == 1 ? ThemeMode.Light : i == 2 ? ThemeMode.Dark : ThemeMode.System; }

        static Brush Br(Color c) { return Ui.Br(c); }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ---------- title bar: dark mode and the sidebar color (Windows 11) ----------

        void ApplyTitleBar()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || L == null) return;
            int dark = L.Dark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, 4);            // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = L.Side.R | (L.Side.G << 8) | (L.Side.B << 16);
            DwmSetWindowAttribute(h, 35, ref caption, 4);         // DWMWA_CAPTION_COLOR
            int text = L.Text.R | (L.Text.G << 8) | (L.Text.B << 16);
            DwmSetWindowAttribute(h, 36, ref text, 4);            // DWMWA_TEXT_COLOR
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        // A thin scrollbar: a 6px rounded thumb on no track.
        const string ScrollXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='12'/>
    <Setter Property='MinWidth' Value='12'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='Vertical'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border CornerRadius='3' Width='6' Margin='3,2' Background='{DynamicResource Hub.Thumb}'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}

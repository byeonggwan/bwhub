// BWHub — the widget catalog, GitHub releases, install/update, and running widgets.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Bwui;
using Microsoft.Win32;

namespace BwHub
{
    sealed class CatalogEntry
    {
        public string AppId, Repo, Exe, NameKo, NameEn, DescKo, DescEn;
        public WidgetSize[] Sizes;
        /// <summary>Snapshot images in the repo's docs folder (BWUI DESIGN.md §6.9).</summary>
        public string PreviewLight, PreviewDark;
        /// <summary>Icon tile: a Fluent glyph on a tint. Light: 50 fill / 600 glyph; dark: 800 fill / 100 glyph.</summary>
        public string Glyph;
        public string[] Tint;   // light fill, light glyph, dark fill, dark glyph

        public string PreviewUrl(bool dark)
        {
            return "https://raw.githubusercontent.com/" + Catalog.Owner + "/" + Repo + "/main/docs/" + (dark ? PreviewDark : PreviewLight);
        }

        public string Name { get { return Text.T(NameKo, NameEn); } }
        public string Description { get { return Text.T(DescKo, DescEn); } }
        public string Dir { get { return App.DataDir(AppId); } }
        public string ExePath { get { return Path.Combine(Dir, Exe); } }
        public string RepoUrl { get { return "https://github.com/" + Catalog.Owner + "/" + Repo; } }
        public bool Installed { get { return File.Exists(ExePath); } }

        /// <summary>The installed exe's version (BWUI 0.3.0+ stamps it), or null.</summary>
        public string InstalledVersion
        {
            get
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(ExePath).ProductVersion;
                    return string.IsNullOrEmpty(v) || v == "0.0.0.0" ? null : v;
                }
                catch { return null; }
            }
        }
    }

    static class Catalog
    {
        public const string Owner = "byeonggwan";

        public static readonly CatalogEntry[] All =
        {
            new CatalogEntry
            {
                AppId = "AiUsageWidget", Repo = "windows-ai-usage-widget", Exe = "AiUsageWidget.exe",
                NameKo = "AI 사용량", NameEn = "AI usage",
                DescKo = "Claude · Codex · Antigravity 남은 한도와 초기화 시각", DescEn = "Claude, Codex and Antigravity limits left, and when they reset",
                Sizes = new[] { WidgetSize.Regular },
                PreviewLight = "rings.png", PreviewDark = "rings-dark.png",
                Glyph = "", Tint = new[] { "#FAECE7", "#993C1D", "#712B13", "#F5C4B3" }
            },
            new CatalogEntry
            {
                AppId = "TodayCalendar", Repo = "windows-today-calendar-widget", Exe = "TodayCalendar.exe",
                NameKo = "오늘 일정", NameEn = "Today",
                DescKo = "여러 구글 계정의 오늘 일정을 한 목록으로", DescEn = "Today's events from every Google account, in one list",
                Sizes = new[] { WidgetSize.Compact, WidgetSize.Regular, WidgetSize.Wide },
                PreviewLight = "today-light.png", PreviewDark = "today-dark.png",
                Glyph = "", Tint = new[] { "#E6F1FB", "#185FA5", "#0C447C", "#B5D4F4" }
            },
            new CatalogEntry
            {
                AppId = "PatchNoteMailbox", Repo = "windows-game-patchnote-widget", Exe = "PatchNoteMailbox.exe",
                NameKo = "게임 패치노트 우편함", NameEn = "Game patch notes",
                DescKo = "로스트아크 · 리그 오브 레전드 새 패치노트 알림", DescEn = "New patch notes for Lost Ark, League of Legends and more",
                Sizes = new[] { WidgetSize.Regular },
                PreviewLight = "expanded-light.png", PreviewDark = "expanded-dark.png",
                Glyph = "", Tint = new[] { "#EEEDFE", "#534AB7", "#3C3489", "#CECBF6" }
            },
        };
    }

    sealed class Release
    {
        public string Tag;
        public Dictionary<string, string> Assets = new Dictionary<string, string>();   // name → download URL
        public Version Version { get { return Updater.ParseVersion(Tag); } }
    }

    static class GitHub
    {
        public static Release Latest(string repo)
        {
            var json = GetString("https://api.github.com/repos/" + Catalog.Owner + "/" + repo + "/releases/latest");
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var r = new Release { Tag = (string)d["tag_name"] };
            foreach (Dictionary<string, object> a in (object[])d["assets"])
                r.Assets[(string)a["name"]] = (string)a["browser_download_url"];
            return r;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "BWHub";
            return wc;
        }

        public static string GetString(string url) { using (var wc = Client()) return wc.DownloadString(url); }
        public static byte[] GetBytes(string url) { using (var wc = Client()) return wc.DownloadData(url); }
    }

    /// <summary>Widget preview images from GitHub, cached in %LOCALAPPDATA%\BwHub\previews and refreshed daily.</summary>
    static class Previews
    {
        static readonly TimeSpan Fresh = TimeSpan.FromDays(1);
        static readonly HashSet<string> loading = new HashSet<string>();

        static string PathOf(CatalogEntry e, bool dark)
        {
            return Path.Combine(App.DataDir(Program.AppId), "previews", e.AppId + (dark ? "-dark" : "-light") + ".png");
        }

        /// <summary>Downloads the image now if it isn't cached (--snapshot).</summary>
        public static void Ensure(CatalogEntry e, bool dark)
        {
            var path = PathOf(e, dark);
            if (File.Exists(path)) return;
            try
            {
                var bytes = GitHub.GetBytes(e.PreviewUrl(dark));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch { }
        }

        /// <summary>The cached image path, or null. Starts a download when missing or stale; onLoaded runs on the UI thread.</summary>
        public static string Get(CatalogEntry e, bool dark, System.Windows.Threading.Dispatcher ui, Action onLoaded)
        {
            var path = PathOf(e, dark);
            bool exists = File.Exists(path);
            if ((!exists || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Fresh) && loading.Add(path))
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool changed = false;
                    try
                    {
                        var bytes = GitHub.GetBytes(e.PreviewUrl(dark));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, bytes);
                        changed = true;
                    }
                    catch { }
                    ui.BeginInvoke(new Action(() => { loading.Remove(path); if (changed) onLoaded(); }));
                });
            }
            return exists ? path : null;
        }
    }

    /// <summary>Installs a widget from its latest release, the same way BWUI's updater does (DESIGN.md §6.10).</summary>
    static class Installer
    {
        public static void Install(CatalogEntry e, Release r)
        {
            // Download and check every file first, so a failure leaves the installed copy untouched.
            var files = new List<KeyValuePair<string, byte[]>>();
            foreach (var a in r.Assets)
            {
                if (a.Key.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) continue;
                string sumUrl;
                if (!r.Assets.TryGetValue(a.Key + ".sha256", out sumUrl))
                    throw new Exception(Text.T(a.Key + "의 체크섬이 릴리스에 없어요", "The release has no checksum for " + a.Key));
                var bytes = GitHub.GetBytes(a.Value);
                var expected = GitHub.GetString(sumUrl).Trim().Split(' ')[0].ToLowerInvariant();
                if (Sha256(bytes) != expected)
                    throw new Exception(Text.T(a.Key + "의 체크섬이 맞지 않아요", "Checksum mismatch for " + a.Key));
                files.Add(new KeyValuePair<string, byte[]>(a.Key, bytes));
            }

            Widgets.Stop(e);
            Directory.CreateDirectory(e.Dir);
            foreach (var f in files)
            {
                var path = Path.Combine(e.Dir, f.Key);
                var tmp = path + ".new";
                var old = path + ".old";
                File.WriteAllBytes(tmp, f.Value);
                // A file in use (the status line helper, say) can still be renamed out of the way.
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                if (File.Exists(path)) File.Move(path, old);
                File.Move(tmp, path);
                try { File.Delete(old); } catch { }
            }
            Widgets.Start(e);
        }

        static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>A running BWUI widget window (BWUI 0.3.0+).</summary>
    sealed class WidgetWindow
    {
        public IntPtr Handle;
        public uint ProcessId;
        public string Title;
    }

    static class Widgets
    {
        public static List<WidgetWindow> FindWindows()
        {
            var list = new List<WidgetWindow>();
            EnumWindows((h, l) =>
            {
                if (GetProp(h, Remote.WidgetProp) == IntPtr.Zero || !IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                list.Add(new WidgetWindow { Handle = h, ProcessId = pid, Title = sb.ToString() });
                return true;
            }, IntPtr.Zero);
            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCulture));
            return list;
        }

        public static Process[] Processes(CatalogEntry e)
        {
            return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e.Exe));
        }

        public static bool IsRunning(CatalogEntry e) { return Processes(e).Length > 0; }

        /// <summary>The widget's windows, found by process.</summary>
        public static List<WidgetWindow> WindowsOf(CatalogEntry e)
        {
            var pids = new HashSet<uint>();
            foreach (var p in Processes(e)) pids.Add((uint)p.Id);
            return FindWindows().FindAll(w => pids.Contains(w.ProcessId));
        }

        public static void Post(CatalogEntry e, int message, int wParam)
        {
            foreach (var w in WindowsOf(e)) PostMessage(w.Handle, message, new IntPtr(wParam), IntPtr.Zero);
        }

        public static void Start(CatalogEntry e)
        {
            if (!e.Installed || IsRunning(e)) return;
            Process.Start(new ProcessStartInfo(e.ExePath) { WorkingDirectory = e.Dir, UseShellExecute = true });
        }

        /// <summary>Closes the widget like its Exit item; kills it if it doesn't go within a few seconds.</summary>
        public static void Stop(CatalogEntry e)
        {
            foreach (var p in Processes(e))
            {
                uint pid = (uint)p.Id;
                EnumWindows((h, l) =>
                {
                    uint wp;
                    GetWindowThreadProcessId(h, out wp);
                    if (wp == pid && IsWindowVisible(h)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                try { if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(2000); } } catch { }
            }
        }

        // ---------- Start with Windows: the same HKCU Run value BWUI writes (named after the AppId) ----------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartsWithWindows(string appId) { return Bwui.Startup.IsEnabled(appId); }

        public static void SetStartsWithWindows(string appId, string command, bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(appId, command);
                    else k.DeleteValue(appId, false);
                }
            }
            catch { }
        }

        // ---------- Win32 ----------

        const int WM_CLOSE = 0x10;
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}

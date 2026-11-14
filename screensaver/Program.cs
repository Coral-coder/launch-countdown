// Launch Countdown: a native Windows screensaver. It fetches its own data and draws everything
// itself (GDI+ for the panels, OpenGL for the 3D corridor); it is not a web page in a window.
//
// Windows starts a .scr with:  /s  run   /c  settings   /p <hwnd>  preview in the control panel.
// This one adds  /agent : a tray helper started at sign-in that wakes the PC and the display
// ten minutes before a launch and brings the countdown up.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LaunchCountdownSaver
{
    internal static class Settings
    {
        private const string Key = @"Software\LaunchCountdownSaver";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "LaunchCountdownAgent";
        public const string DefaultUrl = "https://coral-coder.github.io/launch-countdown/";
        public const string DefaultStream = "https://video.ibm.com/embed/23615375?autoplay=true&volume=0";

        public static string Url = DefaultUrl, SiteIds = "12,27", Theme = "Mission Control", Slides = "", View = "Orbit", CloudTime = "t0", AisKey = "", Stream = DefaultStream;
        public static int SecondsPerTab = 20, DisplayOffMinutes = 30, VerticalScale = 1;
        public static bool WakeForLaunch = true, TerminalLayout = true, Video = true, Clouds = true, RadarVolume = true, Aircraft = true, Vessels = true,
            Hazards = true, Wind = false, MapKey = true, Telemetry = true;

        private static readonly string[] Bools = { "WakeForLaunch", "TerminalLayout", "Video", "Clouds", "RadarVolume", "Aircraft", "Vessels", "Hazards", "Wind", "MapKey", "Telemetry" };
        private static readonly string[] Strings = { "Url", "SiteIds", "Theme", "Slides", "View", "CloudTime", "AisKey", "Stream" };
        private static readonly string[] Ints = { "SecondsPerTab", "DisplayOffMinutes", "VerticalScale" };

        public static void Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return;
                    var t = typeof(Settings);
                    foreach (var n in Strings) { var v = k.GetValue(n) as string; if (v != null) t.GetField(n).SetValue(null, v); }
                    foreach (var n in Ints) { var v = k.GetValue(n); if (v != null) t.GetField(n).SetValue(null, Convert.ToInt32(v)); }
                    foreach (var n in Bools) { var v = k.GetValue(n); if (v != null) t.GetField(n).SetValue(null, Convert.ToInt32(v) != 0); }
                }
            }
            catch { }
            SecondsPerTab = Math.Max(3, Math.Min(600, SecondsPerTab)); DisplayOffMinutes = Math.Max(0, Math.Min(1440, DisplayOffMinutes)); VerticalScale = Math.Max(1, Math.Min(10, VerticalScale));
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    var t = typeof(Settings);
                    foreach (var n in Strings) k.SetValue(n, (string)t.GetField(n).GetValue(null) ?? "");
                    foreach (var n in Ints) k.SetValue(n, (int)t.GetField(n).GetValue(null), RegistryValueKind.DWord);
                    foreach (var n in Bools) k.SetValue(n, (bool)t.GetField(n).GetValue(null) ? 1 : 0, RegistryValueKind.DWord);
                }
                // The wake-up needs something running when the screensaver is not: start the helper at sign-in.
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (WakeForLaunch) run.SetValue(RunName, "\"" + Application.ExecutablePath + "\" /agent");
                    else run.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        public static string SiteBase()
        {
            var u = string.IsNullOrWhiteSpace(Url) ? DefaultUrl : Url.Trim();
            return u.EndsWith("/") ? u : u + "/";
        }

        /// <summary>The slides in the slideshow, in order. An empty setting means all of them.</summary>
        public static List<Slide> SlideList(bool withVideo)
        {
            var all = Enum.GetValues(typeof(Slide)).Cast<Slide>().Where(s => s != Slide.Terminal).ToList();
            var chosen = Slides.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var list = chosen.Count == 0 ? all : all.Where(s => chosen.Contains(s.ToString())).ToList();
            if (!withVideo || !Video) list.Remove(Slide.Live);
            if (list.Count == 0) list.Add(Slide.Count);
            return list;
        }

        /// <summary>The count as you have adjusted it (your own T-0, a hold, a scrub), kept across runs.</summary>
        public static string CountState
        {
            get { try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? "" : k.GetValue("Count") as string ?? ""; } catch { return ""; } }
            set { try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Count", value); } catch { } }
        }
    }

    /// <summary>
    /// Hold, scrub and your own T-0, applied on top of the published launch time. Whatever you set is
    /// dropped as soon as the source publishes a different T-0 for that launch.
    /// </summary>
    internal static class Count
    {
        private static string id; private static long source, mine, holdStart; private static bool scrub, loaded;

        private static void Load()
        {
            if (loaded) return; loaded = true;
            var p = Settings.CountState.Split('|');
            if (p.Length != 5) return;
            id = p[0]; long.TryParse(p[1], out source); long.TryParse(p[2], out mine); long.TryParse(p[3], out holdStart); scrub = p[4] == "1";
        }
        private static void Keep() { Settings.CountState = string.Join("|", id ?? "", source, mine, holdStart, scrub ? "1" : "0"); }
        private static void Clear() { id = null; mine = holdStart = 0; scrub = false; Keep(); }

        private static bool For(Launch l)
        {
            Load();
            if (l == null || id != l.Id || !l.SourceNet.HasValue) return false;
            if (l.SourceNet.Value.Ticks == source) return true;
            Clear(); // the source moved T-0: theirs wins
            return false;
        }

        /// <summary>T-0 as counted. While holding or scrubbed it slides with the clock, so the count stands still.</summary>
        public static DateTime? Net(Launch l)
        {
            if (!For(l)) return l.SourceNet;
            var t = mine != 0 ? new DateTime(mine, DateTimeKind.Utc) : l.SourceNet.Value;
            return holdStart != 0 ? t + (DateTime.UtcNow - new DateTime(holdStart, DateTimeKind.Utc)) : t;
        }

        public static bool Holding(Launch l) { return For(l) && holdStart != 0 && !scrub; }
        public static bool Scrubbed(Launch l) { return For(l) && scrub; }
        public static bool Mine(Launch l) { return For(l) && mine != 0; }

        private static bool Begin(Launch l)
        {
            if (l == null || !l.SourceNet.HasValue) return false;
            if (!For(l)) { id = l.Id; source = l.SourceNet.Value.Ticks; mine = holdStart = 0; scrub = false; }
            return true;
        }

        /// <summary>Hold stops the clock; holding again resumes it, with T-0 moved later by the length of the hold.</summary>
        public static void ToggleHold(Launch l)
        {
            if (!Begin(l) || scrub) return;
            if (holdStart == 0) holdStart = DateTime.UtcNow.Ticks;
            else { mine = Net(l).Value.Ticks; holdStart = 0; }
            Keep();
        }

        /// <summary>Scrub stops the clock for good; only a new T-0 or a reset starts it again.</summary>
        public static void Scrub(Launch l)
        {
            if (!Begin(l) || scrub) return;
            if (holdStart == 0) holdStart = DateTime.UtcNow.Ticks;
            scrub = true; Keep();
        }

        public static void SetT0(Launch l, DateTime utc) { if (Begin(l)) { mine = utc.Ticks; holdStart = 0; scrub = false; Keep(); } }
        public static void Reset(Launch l) { if (For(l)) Clear(); }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimer(IntPtr attributes, bool manualReset, string name);
        [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] private static extern bool CancelWaitableTimer(IntPtr timer);

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        public const int WM_SYSCOMMAND = 0x0112;
        public static readonly IntPtr SC_MONITORPOWER = (IntPtr)0xF170, MonitorOff = (IntPtr)2, MonitorOn = (IntPtr)(-1);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        private static IntPtr wakeTimer = IntPtr.Zero;

        /// <summary>Hold (or stop holding) the system and display awake for this process.</summary>
        public static void KeepAwake(bool on)
        {
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS); } catch { }
        }

        /// <summary>Turn a sleeping display back on.</summary>
        public static void WakeDisplay(IntPtr window)
        {
            SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            if (window != IntPtr.Zero) SendMessage(window, WM_SYSCOMMAND, SC_MONITORPOWER, MonitorOn);
            // Displays reliably wake on input; nudge the pointer one pixel and back.
            mouse_event(0x0001, 0, 1, 0, UIntPtr.Zero);
            mouse_event(0x0001, 0, -1, 0, UIntPtr.Zero);
        }

        /// <summary>Ask Windows to resume from sleep at the given time (needs wake timers allowed in the power plan).</summary>
        public static void ScheduleWake(DateTime utc)
        {
            if (wakeTimer == IntPtr.Zero) wakeTimer = CreateWaitableTimer(IntPtr.Zero, true, null);
            if (wakeTimer == IntPtr.Zero) return;
            CancelWaitableTimer(wakeTimer);
            if (utc <= DateTime.UtcNow) return;
            long due = utc.ToFileTimeUtc();
            SetWaitableTimer(wakeTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, true);
        }

        public static double IdleSeconds()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref i) ? unchecked((uint)Environment.TickCount - i.dwTime) / 1000.0 : 0;
        }
    }

    internal static class Program
    {
        public const string SaverMutex = "LaunchCountdownSaverRunning", AgentMutex = "LaunchCountdownAgentRunning";

        [STAThread]
        private static void Main(string[] args)
        {
#if TEST
            if (args.Length > 0 && args[0] == "/shot") { Settings.Load(); Shots.Run(args); return; }
#endif
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            // Arguments arrive as "/s", "/c", "/c:1234" or "/p 1234"; no argument means "configure".
            var arg = args.Length > 0 ? args[0].ToLowerInvariant() : "/c";
            if (arg.StartsWith("/agent"))
            {
                bool first;
                using (new System.Threading.Mutex(true, AgentMutex, out first))
                    if (first) Application.Run(new AgentContext());
            }
            else if (arg.StartsWith("/s"))
            {
                bool first;
                using (new System.Threading.Mutex(true, SaverMutex, out first))
                    if (first) Application.Run(new SaverContext());
            }
            else if (arg.StartsWith("/p"))
            {
                long h;
                var text = args.Length > 1 ? args[1] : arg.Substring(2).TrimStart(':');
                if (long.TryParse(text, out h) && h != 0) { Store.Start(true); Application.Run(new SaverForm(new IntPtr(h))); }
            }
            else Application.Run(new ConfigForm());
        }

        public static bool IsRunning(string mutex)
        {
            System.Threading.Mutex m;
            if (!System.Threading.Mutex.TryOpenExisting(mutex, out m)) return false;
            m.Dispose();
            return true;
        }

        public static void Start(string argument)
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, argument) { UseShellExecute = false }); } catch { }
        }
    }

    /// <summary>Tray helper: wakes the display for a launch when no screensaver is up to do it itself.</summary>
    internal sealed class AgentContext : ApplicationContext
    {
        public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10), Tail = TimeSpan.FromMinutes(15);
        private readonly NotifyIcon tray;
        private readonly Timer timer = new Timer { Interval = 20000 };
        private DateTime? shownFor, armedFor;

        public AgentContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show launch display now", null, (o, e) => Program.Start("/s"));
            menu.Items.Add("Settings…", null, (o, e) => Program.Start("/c"));
            menu.Items.Add("Exit", null, (o, e) => { tray.Visible = false; ExitThread(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Launch Countdown", ContextMenuStrip = menu, Visible = true };
            Store.Start(true);
            timer.Tick += (o, e) => OnTick();
            timer.Start();
        }

        private void OnTick()
        {
            Settings.Load();
            if (!Settings.WakeForLaunch) { tray.Visible = false; ExitThread(); return; }
            var l = Store.Current; var net = l == null ? null : l.Net;
            tray.Text = net.HasValue ? "Launch Countdown · next T-0 " + net.Value.ToLocalTime().ToString("ddd HH:mm") : "Launch Countdown";
            if (!net.HasValue) return;
            if (armedFor != net) { armedFor = net; Native.ScheduleWake(net.Value - Lead - TimeSpan.FromSeconds(30)); }
            var now = DateTime.UtcNow;
            if (now < net.Value - Lead || now > net.Value + Tail || shownFor == net) return;
            if (Program.IsRunning(Program.SaverMutex)) { shownFor = net; return; } // it wakes itself
            if (Native.IdleSeconds() < 60) return; // someone at the keyboard does not need the screen taken over
            shownFor = net;
            Native.WakeDisplay(IntPtr.Zero);
            Program.Start("/s");
        }
    }

    /// <summary>One window per monitor, each running the display.</summary>
    internal sealed class SaverContext : ApplicationContext
    {
        private readonly List<SaverForm> forms = new List<SaverForm>();
        private readonly Timer offTimer = new Timer(), watch = new Timer { Interval = 5000 };
        private bool holding;

        public SaverContext()
        {
            Store.Start(false);
            foreach (var s in Screen.AllScreens)
            {
                var f = new SaverForm(s.Bounds, s.Primary);
                f.FormClosed += (o, e) => { Native.KeepAwake(false); ExitThread(); };
                forms.Add(f);
                f.Show();
            }
            try { Cursor.Hide(); } catch { }

            offTimer.Tick += (o, e) =>
            {
                offTimer.Stop();
                if (holding) return;
                foreach (var f in forms) f.GoDark();
                if (forms.Count > 0) Native.SendMessage(forms[0].Handle, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, Native.MonitorOff);
            };
            ArmOffTimer();
            watch.Tick += (o, e) => OnTick();
            watch.Start();
        }

        private void ArmOffTimer()
        {
            offTimer.Stop();
            if (Settings.DisplayOffMinutes <= 0) return;
            offTimer.Interval = Settings.DisplayOffMinutes * 60 * 1000;
            offTimer.Start();
        }

        /// <summary>Inside the launch window the display is woken and held on; afterwards it may sleep again.</summary>
        private void OnTick()
        {
            var l = Store.Current; var net = l == null ? null : l.Net; var now = DateTime.UtcNow;
            bool want = Settings.WakeForLaunch && net.HasValue && now >= net.Value - AgentContext.Lead && now <= net.Value + AgentContext.Tail;
            if (net.HasValue && Settings.WakeForLaunch) Native.ScheduleWake(net.Value - AgentContext.Lead - TimeSpan.FromSeconds(30));
            if (want && !holding)
            {
                holding = true;
                offTimer.Stop();
                Native.KeepAwake(true);
                Native.WakeDisplay(forms.Count > 0 ? forms[0].Handle : IntPtr.Zero);
                foreach (var f in forms) f.Wake();
            }
            else if (!want && holding)
            {
                holding = false;
                Native.KeepAwake(false);
                ArmOffTimer();
            }
        }
    }
}

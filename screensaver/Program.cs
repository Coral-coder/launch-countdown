// Windows screensaver that shows the launch countdown site full-screen in WebView2.
//
// Windows starts a .scr with:  /s  run   /c  settings   /p <hwnd>  preview in the control panel.
// This one adds  /agent : a tray helper started at sign-in that wakes the PC and the display
// ten minutes before a launch and brings the countdown up.
//
// Outside the launch window the saver never asks Windows to keep the display on: the page is
// loaded without video and an optional timer switches the monitor off after a set time.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LaunchCountdownSaver
{
    internal static class Settings
    {
        private const string Key = @"Software\LaunchCountdownSaver";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "LaunchCountdownAgent";
        public const string DefaultUrl = "https://coral-coder.github.io/launch-countdown/";

        public static string Url = DefaultUrl;
        public static int SecondsPerTab = 20;
        public static int DisplayOffMinutes = 30; // 0 = leave it to the Windows power settings
        public static bool WakeForLaunch = true;

        public static void Load()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return;
                Url = k.GetValue("Url", DefaultUrl) as string ?? DefaultUrl;
                SecondsPerTab = Clamp(k.GetValue("SecondsPerTab", 20), 3, 600, 20);
                DisplayOffMinutes = Clamp(k.GetValue("DisplayOffMinutes", 30), 0, 1440, 30);
                WakeForLaunch = Clamp(k.GetValue("WakeForLaunch", 1), 0, 1, 1) == 1;
            }
        }

        public static void Save()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("Url", Url);
                k.SetValue("SecondsPerTab", SecondsPerTab, RegistryValueKind.DWord);
                k.SetValue("DisplayOffMinutes", DisplayOffMinutes, RegistryValueKind.DWord);
                k.SetValue("WakeForLaunch", WakeForLaunch ? 1 : 0, RegistryValueKind.DWord);
            }
            // The wake-up needs something running when the screensaver is not: start the helper at sign-in.
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (WakeForLaunch) run.SetValue(RunName, "\"" + Application.ExecutablePath + "\" /agent");
                else run.DeleteValue(RunName, false);
            }
        }

        private static int Clamp(object v, int lo, int hi, int fallback)
        {
            try { return Math.Max(lo, Math.Min(hi, Convert.ToInt32(v))); } catch { return fallback; }
        }

        private static string Base()
        {
            return string.IsNullOrWhiteSpace(Url) ? DefaultUrl : Url.Trim();
        }

        /// <summary>The site address with the screensaver switches added.</summary>
        public static string SaverUrl()
        {
            var u = Base();
            return u + (u.Contains("?") ? "&" : "?") + "screensaver=1&secs=" + SecondsPerTab;
        }

        public static string NextLaunchUrl()
        {
            return new Uri(new Uri(Base()), "data/next.json").ToString();
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimer(IntPtr attributes, bool manualReset, string name);
        [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] private static extern bool CancelWaitableTimer(IntPtr timer);

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        public const int WM_SYSCOMMAND = 0x0112;
        public static readonly IntPtr SC_MONITORPOWER = (IntPtr)0xF170;
        public static readonly IntPtr MonitorOff = (IntPtr)2;
        public static readonly IntPtr MonitorOn = (IntPtr)(-1);

        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        private static IntPtr wakeTimer = IntPtr.Zero;

        /// <summary>Hold (or stop holding) the system and display awake for this process.</summary>
        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
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

    /// <summary>
    /// Knows when the next launch is (from the site's data/next.json) and whether we are inside
    /// the window where the display should be up: ten minutes before T-0 to fifteen after.
    /// </summary>
    internal sealed class LaunchWatcher
    {
        public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10), Tail = TimeSpan.FromMinutes(15);
        private readonly Timer timer = new Timer { Interval = 20000 };
        private DateTime lastFetch = DateTime.MinValue;
        private bool fetching;

        public DateTime? Net { get; private set; }
        public event Action Tick;

        public LaunchWatcher()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            timer.Tick += (o, e) => Poll();
        }

        public void Start() { timer.Start(); Poll(); }

        public bool InWindow
        {
            get { var now = DateTime.UtcNow; return Net.HasValue && now >= Net.Value - Lead && now <= Net.Value + Tail; }
        }

        private void Poll()
        {
            if (!fetching && (DateTime.UtcNow - lastFetch).TotalMinutes >= 5) Fetch();
            var t = Tick; if (t != null) t();
        }

        private void Fetch()
        {
            fetching = true;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            client.DownloadStringCompleted += (o, e) =>
            {
                fetching = false;
                lastFetch = DateTime.UtcNow;
                if (e.Error == null && !e.Cancelled)
                {
                    var m = Regex.Match(e.Result, "\"net\"\\s*:\\s*\"([^\"]+)\"");
                    DateTime net;
                    if (m.Success && DateTime.TryParse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out net))
                    {
                        Net = net;
                        if (Settings.WakeForLaunch) Native.ScheduleWake(net - Lead - TimeSpan.FromSeconds(30));
                    }
                    else Net = null;
                }
                client.Dispose();
            };
            try { client.DownloadStringAsync(new Uri(Settings.NextLaunchUrl() + "?t=" + DateTime.UtcNow.Ticks)); }
            catch { fetching = false; lastFetch = DateTime.UtcNow; }
        }
    }

    internal static class Program
    {
        public const string SaverMutex = "LaunchCountdownSaverRunning", AgentMutex = "LaunchCountdownAgentRunning";

        [STAThread]
        private static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
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
            else if (arg.StartsWith("/p")) return; // the tiny control-panel preview is left blank
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
        private readonly NotifyIcon tray;
        private readonly LaunchWatcher watcher = new LaunchWatcher();
        private DateTime? shownFor;

        public AgentContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show launch display now", null, (o, e) => Program.Start("/s"));
            menu.Items.Add("Settings…", null, (o, e) => Program.Start("/c"));
            menu.Items.Add("Exit", null, (o, e) => { tray.Visible = false; ExitThread(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Launch Countdown", ContextMenuStrip = menu, Visible = true };

            watcher.Tick += OnTick;
            watcher.Start();
        }

        private void OnTick()
        {
            Settings.Load();
            if (!Settings.WakeForLaunch) { tray.Visible = false; ExitThread(); return; }
            tray.Text = watcher.Net.HasValue ? "Launch Countdown · next T-0 " + watcher.Net.Value.ToLocalTime().ToString("ddd HH:mm") : "Launch Countdown";

            if (!watcher.InWindow || shownFor == watcher.Net) return;
            if (Program.IsRunning(Program.SaverMutex)) { shownFor = watcher.Net; return; } // it wakes itself
            // Someone at the keyboard does not need the screen taken over.
            if (Native.IdleSeconds() < 60) return;
            shownFor = watcher.Net;
            Native.WakeDisplay(IntPtr.Zero);
            Program.Start("/s");
        }
    }

    /// <summary>One window per monitor; the primary one hosts the page, the rest are black.</summary>
    internal sealed class SaverContext : ApplicationContext
    {
        private readonly List<SaverForm> forms = new List<SaverForm>();
        private readonly Timer offTimer = new Timer();
        private readonly LaunchWatcher watcher = new LaunchWatcher();
        private bool holding;

        public SaverContext()
        {
            foreach (var s in Screen.AllScreens)
            {
                var f = new SaverForm(s.Bounds, s.Primary);
                f.FormClosed += (o, e) => { Native.KeepAwake(false); ExitThread(); };
                forms.Add(f);
                f.Show();
            }
            Cursor.Hide();

            offTimer.Tick += (o, e) =>
            {
                offTimer.Stop();
                if (holding) return;
                foreach (var f in forms) f.GoDark();
                if (forms.Count > 0) Native.SendMessage(forms[0].Handle, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, Native.MonitorOff);
            };
            ArmOffTimer();

            watcher.Tick += OnTick;
            watcher.Start();
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
            var want = Settings.WakeForLaunch && watcher.InWindow;
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

    internal sealed class SaverForm : Form
    {
        // Runs in every page: any real input ends the screensaver. The first moments are ignored
        // because Windows delivers a stray mouse move when the window appears.
        private const string ExitScript = @"(function () {
  var x = null, y = null, born = Date.now();
  function bye() { try { window.chrome.webview.postMessage('exit'); } catch (e) {} }
  addEventListener('mousemove', function (e) {
    if (x === null || Date.now() - born < 1500) { x = e.screenX; y = e.screenY; return; }
    if (Math.abs(e.screenX - x) + Math.abs(e.screenY - y) > 12) bye();
  }, true);
  ['mousedown', 'keydown', 'wheel', 'touchstart'].forEach(function (n) { addEventListener(n, bye, true); });
})();";

        private readonly WebView2 web;
        private DateTime quietUntil = DateTime.UtcNow.AddMilliseconds(1500);
        private Point? mouseStart;
        private bool dark;

        public SaverForm(Rectangle bounds, bool primary)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;

            KeyDown += (o, e) => Close();
            MouseDown += (o, e) => Close();
            MouseMove += OnMouseMoved;

            if (!primary) return;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
            Controls.Add(web);
            Load += async (o, e) =>
            {
                try
                {
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchCountdownSaver");
                    var env = await CoreWebView2Environment.CreateAsync(null, data);
                    await web.EnsureCoreWebView2Async(env);
                    var s = web.CoreWebView2.Settings;
                    s.AreDefaultContextMenusEnabled = false;
                    s.AreDevToolsEnabled = false;
                    s.IsStatusBarEnabled = false;
                    s.AreBrowserAcceleratorKeysEnabled = false;
                    await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ExitScript);
                    web.CoreWebView2.WebMessageReceived += (s2, m) => { if (DateTime.UtcNow > quietUntil) Close(); };
                    web.CoreWebView2.NewWindowRequested += (s2, n) => n.Handled = true;
                    web.CoreWebView2.Navigate(Settings.SaverUrl());
                }
                catch (Exception ex)
                {
                    // Usually a missing WebView2 runtime; say so instead of showing a blank screen.
                    Controls.Remove(web);
                    Controls.Add(new Label
                    {
                        Dock = DockStyle.Fill, ForeColor = Color.Gray, BackColor = Color.Black, TextAlign = ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 14f),
                        Text = "Launch Countdown screensaver could not start the Microsoft Edge WebView2 runtime.\n" + ex.Message,
                    });
                }
            };
        }

        /// <summary>Stop the page and leave a black window, so nothing keeps drawing once the display is off.</summary>
        public void GoDark()
        {
            dark = true;
            mouseStart = null;
            quietUntil = DateTime.UtcNow.AddSeconds(3); // the monitor switching off can produce a stray mouse event
            if (web == null) return;
            try { if (web.CoreWebView2 != null) web.CoreWebView2.Navigate("about:blank"); } catch { }
            web.Visible = false;
        }

        /// <summary>Bring the page back after GoDark, for the launch window.</summary>
        public void Wake()
        {
            mouseStart = null;
            quietUntil = DateTime.UtcNow.AddSeconds(3); // ignore the pointer nudge that woke the display
            if (!dark) return;
            dark = false;
            if (web == null) return;
            web.Visible = true;
            try { if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(Settings.SaverUrl()); } catch { }
        }

        private void OnMouseMoved(object sender, MouseEventArgs e)
        {
            if (DateTime.UtcNow < quietUntil || mouseStart == null) { mouseStart = e.Location; return; }
            if (Math.Abs(e.X - mouseStart.Value.X) + Math.Abs(e.Y - mouseStart.Value.Y) > 12) Close();
        }
    }

    internal sealed class ConfigForm : Form
    {
        public ConfigForm()
        {
            Text = "Launch Countdown screensaver";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(540, 330);

            var url = new TextBox { Left = 16, Top = 36, Width = 508, Text = Settings.Url };
            var secs = new NumericUpDown { Left = 16, Top = 96, Width = 90, Minimum = 3, Maximum = 600, Value = Settings.SecondsPerTab };
            var off = new NumericUpDown { Left = 280, Top = 96, Width = 90, Minimum = 0, Maximum = 1440, Value = Settings.DisplayOffMinutes };
            var wake = new CheckBox { Left = 16, Top = 180, Width = 508, Text = "Wake the display 10 minutes before launch and show the countdown", Checked = Settings.WakeForLaunch };
            var ok = new Button { Text = "OK", Left = 348, Top = 286, Width = 84, Height = 28, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 440, Top = 286, Width = 84, Height = 28, DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[]
            {
                new Label { Left = 16, Top = 14, AutoSize = true, Text = "Site address" }, url,
                new Label { Left = 16, Top = 74, AutoSize = true, Text = "Seconds on each tab" }, secs,
                new Label { Left = 280, Top = 74, AutoSize = true, Text = "Turn the display off after (minutes)" }, off,
                new Label { Left = 16, Top = 128, Width = 508, Height = 40, ForeColor = SystemColors.GrayText,
                    Text = "0 minutes leaves the display to the Windows power settings. Outside the launch window the screensaver plays no video and makes no power requests." },
                wake,
                new Label { Left = 34, Top = 206, Width = 490, Height = 70, ForeColor = SystemColors.GrayText,
                    Text = "Adds a small helper to the notification area that starts when you sign in. It wakes the PC and the display at T-10 minutes, keeps them on until T+15 minutes, then lets them sleep again. Waking from sleep needs \"Allow wake timers\" enabled in the Windows power plan, and a locked PC will show the lock screen." },
                ok, cancel,
            });
            AcceptButton = ok;
            CancelButton = cancel;

            ok.Click += (o, e) =>
            {
                Settings.Url = string.IsNullOrWhiteSpace(url.Text) ? Settings.DefaultUrl : url.Text.Trim();
                Settings.SecondsPerTab = (int)secs.Value;
                Settings.DisplayOffMinutes = (int)off.Value;
                Settings.WakeForLaunch = wake.Checked;
                Settings.Save();
                if (Settings.WakeForLaunch && !Program.IsRunning(Program.AgentMutex)) Program.Start("/agent");
                Close();
            };
            cancel.Click += (o, e) => Close();
        }
    }
}

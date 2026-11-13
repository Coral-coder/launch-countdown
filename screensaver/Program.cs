// Windows screensaver that shows the launch countdown site full-screen in WebView2.
//
// Windows starts a .scr with:  /s  run   /c  settings   /p <hwnd>  preview in the control panel.
// The saver never asks Windows to keep the display on, the site is loaded in a mode with no
// video, and an optional timer switches the monitor off itself after a set number of minutes.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LaunchCountdownSaver
{
    internal static class Settings
    {
        private const string Key = @"Software\LaunchCountdownSaver";
        public const string DefaultUrl = "https://coral-coder.github.io/launch-countdown/";

        public static string Url = DefaultUrl;
        public static int SecondsPerTab = 20;
        public static int DisplayOffMinutes = 30; // 0 = leave it to the Windows power settings

        public static void Load()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return;
                Url = k.GetValue("Url", DefaultUrl) as string ?? DefaultUrl;
                SecondsPerTab = Clamp(k.GetValue("SecondsPerTab", 20), 3, 600, 20);
                DisplayOffMinutes = Clamp(k.GetValue("DisplayOffMinutes", 30), 0, 1440, 30);
            }
        }

        public static void Save()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("Url", Url);
                k.SetValue("SecondsPerTab", SecondsPerTab, RegistryValueKind.DWord);
                k.SetValue("DisplayOffMinutes", DisplayOffMinutes, RegistryValueKind.DWord);
            }
        }

        private static int Clamp(object v, int lo, int hi, int fallback)
        {
            try { return Math.Max(lo, Math.Min(hi, Convert.ToInt32(v))); } catch { return fallback; }
        }

        /// <summary>The site address with the screensaver switches added.</summary>
        public static string SaverUrl()
        {
            var u = string.IsNullOrWhiteSpace(Url) ? DefaultUrl : Url.Trim();
            return u + (u.Contains("?") ? "&" : "?") + "screensaver=1&secs=" + SecondsPerTab;
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        public const int WM_SYSCOMMAND = 0x0112;
        public static readonly IntPtr SC_MONITORPOWER = (IntPtr)0xF170;
        public static readonly IntPtr MonitorOff = (IntPtr)2;
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            // Arguments arrive as "/s", "/c", "/c:1234" or "/p 1234"; no argument means "configure".
            var mode = args.Length > 0 && args[0].Length >= 2 ? args[0].Substring(0, 2).ToLowerInvariant() : "/c";
            if (mode == "/s") Application.Run(new SaverContext());
            else if (mode == "/p") return; // the tiny control-panel preview is left blank
            else Application.Run(new ConfigForm());
        }
    }

    /// <summary>One window per monitor; the primary one hosts the page, the rest are black.</summary>
    internal sealed class SaverContext : ApplicationContext
    {
        private readonly List<SaverForm> forms = new List<SaverForm>();
        private readonly Timer offTimer = new Timer();

        public SaverContext()
        {
            foreach (var s in Screen.AllScreens)
            {
                var f = new SaverForm(s.Bounds, s.Primary);
                f.FormClosed += (o, e) => ExitThread();
                forms.Add(f);
                f.Show();
            }
            Cursor.Hide();

            if (Settings.DisplayOffMinutes > 0)
            {
                offTimer.Interval = Settings.DisplayOffMinutes * 60 * 1000;
                offTimer.Tick += (o, e) =>
                {
                    offTimer.Stop();
                    foreach (var f in forms) f.GoDark();
                    if (forms.Count > 0) Native.SendMessage(forms[0].Handle, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, Native.MonitorOff);
                };
                offTimer.Start();
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
        private readonly DateTime shown = DateTime.UtcNow;
        private Point? mouseStart;

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
                    web.CoreWebView2.WebMessageReceived += (s2, m) => Close();
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
            mouseStart = null;
            if (web == null) return;
            try { web.CoreWebView2?.Navigate("about:blank"); } catch { }
            web.Visible = false;
        }

        private void OnMouseMoved(object sender, MouseEventArgs e)
        {
            if ((DateTime.UtcNow - shown).TotalMilliseconds < 1500 || mouseStart == null) { mouseStart = e.Location; return; }
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
            ClientSize = new Size(520, 230);

            var url = new TextBox { Left = 16, Top = 36, Width = 488, Text = Settings.Url };
            var secs = new NumericUpDown { Left = 16, Top = 96, Width = 90, Minimum = 3, Maximum = 600, Value = Settings.SecondsPerTab };
            var off = new NumericUpDown { Left = 270, Top = 96, Width = 90, Minimum = 0, Maximum = 1440, Value = Settings.DisplayOffMinutes };
            var ok = new Button { Text = "OK", Left = 328, Top = 186, Width = 84, Height = 28, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 420, Top = 186, Width = 84, Height = 28, DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[]
            {
                new Label { Left = 16, Top = 14, AutoSize = true, Text = "Site address" }, url,
                new Label { Left = 16, Top = 74, AutoSize = true, Text = "Seconds on each tab" }, secs,
                new Label { Left = 270, Top = 74, AutoSize = true, Text = "Turn the display off after (minutes)" }, off,
                new Label { Left = 16, Top = 132, Width = 488, Height = 44, ForeColor = SystemColors.GrayText,
                    Text = "0 minutes leaves the display to the Windows power settings. The screensaver never keeps the display awake: it plays no video and makes no power requests." },
                ok, cancel,
            });
            AcceptButton = ok;
            CancelButton = cancel;

            ok.Click += (o, e) =>
            {
                Settings.Url = string.IsNullOrWhiteSpace(url.Text) ? Settings.DefaultUrl : url.Text.Trim();
                Settings.SecondsPerTab = (int)secs.Value;
                Settings.DisplayOffMinutes = (int)off.Value;
                Settings.Save();
                Close();
            };
            cancel.Click += (o, e) => Close();
        }
    }
}

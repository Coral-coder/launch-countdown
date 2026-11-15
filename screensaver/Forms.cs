// The windows: the full-screen display (one per monitor, and the small preview inside Screen Saver
// Settings), the dialog for entering a new T-0, and the settings dialog.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LaunchCountdownSaver
{
    /// <summary>The live video panel. It is the one part that needs a browser engine, because the streams are web players.</summary>
    internal abstract class VideoHost
    {
        public bool Failed;
        public abstract void Show(string url, Rectangle bounds);
        public abstract void Hide();

        public static VideoHost Create(Control parent)
        {
#if NOVIDEO
            return null;
#else
            try { return Make(parent); } catch { return null; } // no WebView2 runtime: the display carries on without video
#endif
        }
#if !NOVIDEO
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static VideoHost Make(Control parent) { return new WebVideo(parent); }
#endif
    }

    /// <summary>
    /// The display window. It runs three ways: full-screen as the screensaver (one per monitor), as an
    /// ordinary resizable window (the app), and as the little preview inside Screen Saver Settings.
    /// </summary>
    internal sealed class SaverForm : Form
    {
        private readonly bool primary, preview, app; private readonly IntPtr previewParent;
        private readonly Timer frame = new Timer { Interval = 15 };
        private readonly GLView gl; private readonly VideoHost video;
        private Bitmap back; private Layout lay = new Layout();
        private int index, lastVersion = -1; private long lastSecond; private DateTime slideStart = DateTime.UtcNow, lastBuild, toastUntil, lastMouse = DateTime.MinValue;
        private Slide current = Slide.Count; private Slide? pinned; private bool paused, dark, help, force = true, barShown, cursorShown = true, dragging, full; private string toast;
        private Point mouseAt, dragAt; private Rectangle windowed; private DateTime scrubArmed = DateTime.MinValue;

        /// <summary>The screensaver, covering one monitor.</summary>
        public SaverForm(Rectangle bounds, bool primary)
        {
            this.primary = primary;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = bounds;
            ShowInTaskbar = false; TopMost = true;
            Init();
            if (primary) { gl = new GLView { Visible = false, Enabled = false }; Controls.Add(gl); video = VideoHost.Create(this); }
            else index = 3; // other monitors run the same show, a few slides ahead
            if (primary) Shown += (o, e) => { Activate(); Focus(); };
        }

        /// <summary>The app: an ordinary window that stays on the tab you pick.</summary>
        public SaverForm()
        {
            app = primary = true; paused = true; pinned = Slide.Count;
            Text = "Launch Countdown"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1440, 810); MinimumSize = new Size(900, 560);
            try { Icon = SystemIcons.Application; } catch { }
            Init();
            gl = new GLView { Visible = false, Enabled = false }; Controls.Add(gl); video = VideoHost.Create(this);
        }

        /// <summary>The preview: a child of the little monitor in Screen Saver Settings.</summary>
        public SaverForm(IntPtr parent)
        {
            preview = true; previewParent = parent;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Init();
            Load += (o, e) =>
            {
                Native.SetParent(Handle, previewParent);
                Native.SetWindowLong(Handle, -16, Native.GetWindowLong(Handle, -16) | 0x40000000); // WS_CHILD
                Native.RECT r; Native.GetClientRect(previewParent, out r);
                Bounds = new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
            };
        }

        private void Init()
        {
            BackColor = Color.Black; KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            // one bad frame (a source returning something odd) must not take the display down
            frame.Tick += (o, e) => { try { Tick(); } catch { force = true; } };
            frame.Start();
            if (preview) return;
            KeyDown += OnKey;
            Resize += (o, e) => force = true;
            MouseMove += OnMouseMove; MouseDown += OnMouseDown; MouseUp += (o, e) => dragging = false; MouseWheel += OnWheel;
            MouseDoubleClick += (o, e) => { if (gl != null && lay.View3D.Contains(e.Location)) gl.Scene.ResetView(); };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            frame.Stop();
            if (video != null) video.Hide();
            base.OnFormClosed(e);
        }

        // ---------------------------------------------------------------- frame

        private bool Has3D { get { return gl != null && !gl.Failed; } }
        private bool HasVideo { get { return video != null && !video.Failed && Settings.Video; } }
        private List<Slide> Slides() { return Settings.SlideList(HasVideo); }

        private void Tick()
        {
            if (preview && !Native.IsWindow(previewParent)) { Close(); return; }
            if (dark || ClientSize.Width < 8 || ClientSize.Height < 8) return;
            if (primary && Updater.Note.Length > 0) { toast = Updater.Note; toastUntil = DateTime.UtcNow.AddSeconds(12); Updater.Note = ""; force = true; }
            var now = DateTime.UtcNow; var l = Store.Current;
            double t = l != null && l.Net.HasValue ? (now - l.Net.Value).TotalSeconds : double.NaN;
            bool terminal = !preview && Settings.TerminalLayout && l != null && l.Precise && t >= -3600 && t <= 900;
            var slides = Slides();
            if (!paused && !terminal && (now - slideStart).TotalSeconds >= Settings.SecondsPerTab) { index++; slideStart = now; }
            index = ((index % slides.Count) + slides.Count) % slides.Count;
            // a tab you picked stays until the slideshow is switched back on
            var slide = preview ? Slide.Count : pinned.HasValue ? pinned.Value : terminal ? Slide.Terminal : slides[index];

            // the bar and the pointer: always there in the app, in the screensaver only while the mouse is in use
            bool bar = !preview && (app || (now - lastMouse).TotalSeconds < 6);
            if (!app && !preview && cursorShown != bar) { try { if (bar) Cursor.Show(); else Cursor.Hide(); } catch { } cursorShown = bar; }

            bool flying = t >= 0 && t <= Flight.End;
            if (force || slide != current || bar != barShown || now.Ticks / TimeSpan.TicksPerSecond != lastSecond || Store.Version != lastVersion || (flying && (now - lastBuild).TotalMilliseconds > 200))
            {
                current = slide; barShown = bar; lastSecond = now.Ticks / TimeSpan.TicksPerSecond; lastVersion = Store.Version; lastBuild = now; force = false;
                Build(slide, slides, l);
                Invalidate();
            }
            else if (lay.Clock.Width > 1) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(lay.Clock, 4, 4)));
            if (gl != null && gl.Visible) gl.Render();
        }

        private void Build(Slide slide, List<Slide> slides, Launch l)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (back == null || back.Width != w || back.Height != h) { if (back != null) back.Dispose(); back = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            var tabs = new List<Slide>(slides); tabs.Add(Slide.Terminal);
            using (var g = Graphics.FromImage(back))
            using (var p = new Painter(g, w, h) { Scene = gl == null ? null : gl.Scene, View3DAvailable = Has3D, VideoAvailable = HasVideo, Bar = barShown, App = app, Running = !paused, Full = full, Tabs = tabs })
            {
                p.Draw(slide, Math.Max(0, slides.IndexOf(slide)), slides.Count);
                p.Banner(l, paused && !app && !pinned.HasValue, DateTime.UtcNow < toastUntil ? toast : null, help);
                lay = p.Out;
            }
            if (gl != null)
            {
                bool show = lay.View3D.Width > 4 && Has3D;
                if (show)
                {
                    var r = Rectangle.Round(lay.View3D);
                    if (gl.Bounds != r) gl.Bounds = r;
                    Scene3D.Mode m;
                    gl.Scene.View = slide == Slide.Terminal ? Scene3D.Mode.Chase : Enum.TryParse(Settings.View, true, out m) ? m : Scene3D.Mode.Orbit;
                }
                if (gl.Visible != show) gl.Visible = show;
            }
            if (video != null)
            {
                if (lay.Video.Width > 4 && HasVideo)
                {
                    // the launch webcast around T-0 when one is listed, otherwise the pad and weather stream
                    double t = l != null && l.Net.HasValue ? (DateTime.UtcNow - l.Net.Value).TotalMinutes : double.NaN;
                    string url = l != null && l.Video != null && t > -120 && t < 60 ? l.Video : Settings.Stream;
                    video.Show(url, Rectangle.Round(lay.Video));
                }
                else video.Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (dark || back == null) { e.Graphics.Clear(Color.Black); return; }
            try
            {
                e.Graphics.DrawImageUnscaled(back, 0, 0);
                Painter.DrawClock(e.Graphics, lay, ClientSize.Height / 1080f, DateTime.UtcNow);
            }
            catch { }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        /// <summary>Stop drawing and leave a black window, so nothing keeps working once the display is off.</summary>
        public void GoDark()
        {
            dark = true;
            if (gl != null) gl.Visible = false;
            if (video != null) video.Hide();
            Invalidate();
        }

        /// <summary>Bring the display back after GoDark, for the launch window.</summary>
        public void Wake() { dark = false; force = true; }

        // ---------------------------------------------------------------- mouse
        // Moving the mouse never closes the screensaver: it brings up the bar so tabs and controls can be clicked.

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (Math.Abs(e.X - mouseAt.X) + Math.Abs(e.Y - mouseAt.Y) < 3) return;
            mouseAt = e.Location; lastMouse = DateTime.UtcNow;
            if (dragging && gl != null) { gl.Scene.Drag(e.X - dragAt.X, e.Y - dragAt.Y); dragAt = e.Location; }
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            lastMouse = DateTime.UtcNow;
            for (int i = lay.Hits.Count - 1; i >= 0; i--)
                if (lay.Hits[i].Key.Contains(e.Location)) { Do(lay.Hits[i].Value, false); return; }
            if (gl != null && lay.View3D.Contains(e.Location)) { dragging = true; dragAt = e.Location; } // drag turns the 3D view, the wheel zooms, a double click lets it go
        }

        private void OnWheel(object sender, MouseEventArgs e)
        {
            lastMouse = DateTime.UtcNow;
            if (gl != null && lay.View3D.Contains(e.Location)) gl.Scene.Wheel(e.Delta);
        }

        // ---------------------------------------------------------------- actions, from a key or a click

        private void Say(string text) { toast = text; toastUntil = DateTime.UtcNow.AddSeconds(4); force = true; }
        private static string OnOff(bool v) { return v ? "on" : "off"; }

        private void Go(int to)
        {
            var slides = Slides();
            index = ((to % slides.Count) + slides.Count) % slides.Count; slideStart = DateTime.UtcNow;
            if (paused || pinned.HasValue) pinned = slides[index];
        }

        private void SetFull(bool on)
        {
            if (!app || on == full) return;
            full = on;
            if (on) { windowed = Bounds; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds; }
            else { FormBorderStyle = FormBorderStyle.Sizable; Bounds = windowed; }
        }

        private void Do(string action, bool shift)
        {
            var l = Store.Current; var slides = Slides();
            if (action.StartsWith("tab:"))
            {
                // stay on this tab until the slideshow is switched on again
                var s = (Slide)Enum.Parse(typeof(Slide), action.Substring(4));
                pinned = s; paused = true; if (slides.Contains(s)) index = slides.IndexOf(s);
                force = true; return;
            }
            switch (action)
            {
                case "exit": Close(); return;
                case "help": help = !help; break;
                case "hold": Count.ToggleHold(l); Say(Count.Holding(l) ? "HOLD — press H to resume, T to enter a new T-0" : "Count resumed"); break;
                case "scrub":
                    // a scrub needs a deliberate second step: Shift+S, or a second click within a few seconds
                    if (shift || (DateTime.UtcNow - scrubArmed).TotalSeconds < 5) { Count.Scrub(l); Say("SCRUBBED — T or Set T-0 enters a new T-0, R or Reset clears it"); }
                    else { scrubArmed = DateTime.UtcNow; Say("Press Shift+S, or click Scrub again, to scrub"); }
                    break;
                case "reset": Count.Reset(l); Say("Count reset to the published T-0"); break;
                case "t0": AskT0(l); break;
                case "next": Go(index + 1); break;
                case "prev": Go(index - 1); break;
                case "slideshow":
                    paused = !paused; slideStart = DateTime.UtcNow;
                    if (paused) pinned = current; else pinned = null;
                    Say(paused ? "Slideshow off — staying on this tab" : "Slideshow on, " + Settings.SecondsPerTab + " seconds per slide"); break;
                case "faster": Settings.SecondsPerTab = Math.Min(600, Settings.SecondsPerTab + 5); Say(Settings.SecondsPerTab + " seconds per slide"); break;
                case "slower": Settings.SecondsPerTab = Math.Max(5, Settings.SecondsPerTab - 5); Say(Settings.SecondsPerTab + " seconds per slide"); break;
                case "launch":
                {
                    var all = Store.Launches.Where(x => x.Net.HasValue && x.Net.Value > DateTime.UtcNow.AddMinutes(-20)).ToList();
                    if (all.Count == 0) break;
                    int i = (all.FindIndex(x => l != null && x.Id == l.Id) + 1) % all.Count;
                    Store.SelectedId = i == 0 ? null : all[i].Id; Store.Version++;
                    Say("Following " + all[i].Name + (i == 0 ? " (next launch)" : ""));
                    break;
                }
                case "theme":
                {
                    int i = (Array.FindIndex(Theme.All, x => x == Theme.Cur) + 1) % Theme.All.Length;
                    Settings.Theme = Theme.All[i].Name; Say("Theme: " + Settings.Theme); break;
                }
                case "settings":
                    if (!app) { try { Cursor.Show(); } catch { } TopMost = false; }
                    using (var d = new ConfigForm()) d.ShowDialog(this);
                    if (!app) TopMost = true;
                    Settings.Load(); Store.Refresh(); break;
                case "full": SetFull(!full); break;
                case "view":
                {
                    var names = Enum.GetNames(typeof(Scene3D.Mode)); int i = (Array.IndexOf(names, Settings.View) + 1) % names.Length;
                    Settings.View = names[i]; Say("3D view: " + names[i]); break;
                }
                case "scale": Settings.VerticalScale = Settings.VerticalScale >= 10 ? 1 : Settings.VerticalScale == 1 ? 2 : Settings.VerticalScale == 2 ? 5 : 10; Say("Vertical scale " + Settings.VerticalScale + "×"); break;
                case "wind": Settings.Wind = !Settings.Wind; Say("Wind arrows and labels " + OnOff(Settings.Wind)); break;
                case "aircraft": Settings.Aircraft = !Settings.Aircraft; Say("Aircraft " + OnOff(Settings.Aircraft)); break;
                case "vessels": Settings.Vessels = !Settings.Vessels; Say("Vessels " + OnOff(Settings.Vessels)); break;
                case "hazards": Settings.Hazards = !Settings.Hazards; Say("NOTAM and NOTMAR areas " + OnOff(Settings.Hazards)); break;
                case "clouds": Settings.Clouds = !Settings.Clouds; Say("Cloud volume " + OnOff(Settings.Clouds)); break;
                case "radar": Settings.RadarVolume = !Settings.RadarVolume; Store.Refresh(); Say("Radar volume " + OnOff(Settings.RadarVolume)); break;
                case "telemetry": Settings.Telemetry = !Settings.Telemetry; Say("Telemetry " + OnOff(Settings.Telemetry)); break;
                case "key": Settings.MapKey = !Settings.MapKey; Say("Key " + OnOff(Settings.MapKey)); break;
                case "fields": Settings.CloudTime = Settings.CloudTime == "t0" ? "now" : "t0"; Store.Refresh(); Say("Cloud and wind fields for " + (Settings.CloudTime == "t0" ? "launch time" : "now")); break;
                case "winds": Settings.UpperSource = Winds.NextSource(Settings.UpperSource); Say("Upper winds: " + Winds.SourceName(Settings.UpperSource)); break;
                case "video": Settings.Video = !Settings.Video; Say("Live video " + OnOff(Settings.Video)); break;
                case "terminal": Settings.TerminalLayout = !Settings.TerminalLayout; Say("Terminal-count layout " + OnOff(Settings.TerminalLayout)); break;
                default: return;
            }
            Settings.Save();
            force = true;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            e.Handled = e.SuppressKeyPress = true;
            string a = null;
            switch (e.KeyCode)
            {
                // only Escape or Space ends the screensaver; in the app Escape leaves full screen and Space toggles the slideshow
                case Keys.Escape: if (app) { SetFull(false); return; } a = "exit"; break;
                case Keys.Space: a = app ? "slideshow" : "exit"; break;
                case Keys.F11: a = "full"; break;
                case Keys.F1: case Keys.K: a = "help"; break;
                case Keys.H: a = "hold"; break; case Keys.S: a = "scrub"; break; case Keys.R: a = "reset"; break; case Keys.T: a = "t0"; break;
                case Keys.Right: case Keys.PageDown: a = "next"; break; case Keys.Left: case Keys.PageUp: a = "prev"; break;
                case Keys.P: a = "slideshow"; break; case Keys.Add: case Keys.Oemplus: a = "faster"; break; case Keys.Subtract: case Keys.OemMinus: a = "slower"; break;
                case Keys.L: a = "launch"; break; case Keys.C: a = "theme"; break; case Keys.V: a = "view"; break; case Keys.X: a = "scale"; break;
                case Keys.W: a = "wind"; break; case Keys.A: a = "aircraft"; break; case Keys.B: a = "vessels"; break; case Keys.N: a = "hazards"; break;
                case Keys.O: a = "clouds"; break; case Keys.D: a = "radar"; break; case Keys.G: a = "telemetry"; break; case Keys.Y: a = "key"; break;
                case Keys.M: a = "fields"; break; case Keys.U: a = "winds"; break; case Keys.I: a = "video"; break; case Keys.E: a = "terminal"; break;
                default:
                    if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 && e.KeyCode - Keys.D1 < Slides().Count) { pinned = paused ? (Slide?)Slides()[e.KeyCode - Keys.D1] : null; index = e.KeyCode - Keys.D1; slideStart = DateTime.UtcNow; force = true; return; }
                    e.Handled = e.SuppressKeyPress = false; return;
            }
            Do(a, e.Shift);
        }

        private void AskT0(Launch l)
        {
            if (l == null || !l.Net.HasValue) return;
            if (!app) { try { Cursor.Show(); } catch { } }
            using (var d = new T0Dialog(l))
                if (d.ShowDialog(this) == DialogResult.OK) { Count.SetT0(l, d.Value); Say("T-0 set to " + d.Value.ToString("HH:mm:ss") + " UTC — kept until the source publishes a new time"); }
            cursorShown = true; lastMouse = DateTime.UtcNow;
        }
    }

    /// <summary>Enter a new T-0: when coming out of a hold, or whenever the time slips.</summary>
    internal sealed class T0Dialog : Form
    {
        public DateTime Value;

        public T0Dialog(Launch l)
        {
            Text = "Set T-0"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; TopMost = true; Font = new Font("Segoe UI", 10f); ClientSize = new Size(470, 200);
            var box = new TextBox { Left = 16, Top = 44, Width = 438, Text = l.Net.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") };
            var note = new Label { Left = 16, Top = 80, Width = 438, Height = 66, ForeColor = SystemColors.GrayText,
                Text = "This PC's local time, or end with Z for UTC. A time alone (14:32:10) means today. +10 slips the current T-0 by ten minutes; -2:30 brings it forward." };
            var ok = new Button { Text = "Set", Left = 278, Top = 156, Width = 84, Height = 30 };
            var cancel = new Button { Text = "Cancel", Left = 370, Top = 156, Width = 84, Height = 30, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { new Label { Left = 16, Top = 16, AutoSize = true, Text = "New T-0 for " + l.Name }, box, note, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (o, e) =>
            {
                DateTime v;
                if (Parse(box.Text, l.Net.Value, out v)) { Value = v; DialogResult = DialogResult.OK; Close(); }
                else { note.ForeColor = Color.Firebrick; note.Text = "That is not a time I can read. Examples: 2026-11-14 12:32:30, 12:32:30, 17:32:30Z, +10, -2:30"; }
            };
            Shown += (o, e) => { box.SelectAll(); box.Focus(); };
        }

        public static bool Parse(string text, DateTime current, out DateTime utc)
        {
            utc = current; text = (text ?? "").Trim();
            var slip = Regex.Match(text, @"^([+-])\s*(\d+)(?::(\d{1,2}))?$");
            if (slip.Success)
            {
                double s = int.Parse(slip.Groups[2].Value) * 60 + (slip.Groups[3].Success ? int.Parse(slip.Groups[3].Value) : 0);
                utc = current.AddSeconds(slip.Groups[1].Value == "-" ? -s : s); return true;
            }
            bool z = Regex.IsMatch(text, @"(Z|UTC)$", RegexOptions.IgnoreCase);
            if (z) text = Regex.Replace(text, @"\s*(Z|UTC)$", "", RegexOptions.IgnoreCase);
            DateTime v;
            if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out v) && !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out v)) return false;
            if (z)
            {
                if (!Regex.IsMatch(text, @"\d{4}|/")) { var n = DateTime.UtcNow; v = new DateTime(n.Year, n.Month, n.Day, v.Hour, v.Minute, v.Second); }
                utc = DateTime.SpecifyKind(v, DateTimeKind.Utc);
            }
            else utc = DateTime.SpecifyKind(v, DateTimeKind.Local).ToUniversalTime();
            return true;
        }
    }

    internal sealed class ConfigForm : Form
    {
        public ConfigForm()
        {
            Text = "Launch Countdown screensaver"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9f); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(600, 520);
            var tabs = new TabControl { Left = 10, Top = 10, Width = 580, Height = 456 };
            Func<string, TabPage> page = t => { var p = new TabPage(t) { Padding = new Padding(10) }; tabs.TabPages.Add(p); return p; };
            Func<Control, string, int, int, Label> label = (p, t, x, y) => { var lb = new Label { Left = x, Top = y, AutoSize = true, Text = t }; p.Controls.Add(lb); return lb; };
            Func<Control, string, int, int, bool, CheckBox> check = (p, t, x, y, on0) => { var c = new CheckBox { Left = x, Top = y, Width = 520, Text = t, Checked = on0 }; p.Controls.Add(c); return c; };
            Func<Control, string, int, int, int, GrayNote> note = (p, t, x, y, h) => { var n = new GrayNote { Left = x, Top = y, Width = 530, Height = h, Text = t }; p.Controls.Add(n); return n; };

            // ---- display
            var d = page("Display");
            label(d, "Theme", 14, 16);
            var theme = new ComboBox { Left = 14, Top = 36, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            theme.Items.AddRange(Theme.All.Select(t => (object)t.Name).ToArray()); theme.SelectedItem = Theme.Cur.Name; d.Controls.Add(theme);
            label(d, "Seconds on each slide", 264, 16);
            var secs = new NumericUpDown { Left = 264, Top = 36, Width = 90, Minimum = 3, Maximum = 600, Value = Settings.SecondsPerTab }; d.Controls.Add(secs);
            label(d, "Turn the display off after (minutes, 0 = leave it to Windows)", 14, 76);
            var off = new NumericUpDown { Left = 14, Top = 96, Width = 90, Minimum = 0, Maximum = 1440, Value = Settings.DisplayOffMinutes }; d.Controls.Add(off);
            var terminal = check(d, "Terminal-count layout from T-1 hour: count on top, corridor left, live video and timeline right", 14, 136, Settings.TerminalLayout);
            var videoOn = check(d, "Show live video (pad and weather stream, launch webcast near T-0)", 14, 162, Settings.Video);
            var wake = check(d, "Wake the display 10 minutes before launch and show the countdown", 14, 198, Settings.WakeForLaunch);
            note(d, "Adds a small helper to the notification area that starts when you sign in. It wakes the PC and the display at T-10 minutes, keeps them on until T+15 minutes, then lets them sleep again. Waking from sleep needs \"Allow wake timers\" in the Windows power plan, and a locked PC shows the lock screen.", 32, 222, 64);
            var update = check(d, "Update automatically when a newer version is published", 14, 356, Settings.AutoUpdate);
            note(d, "While it runs: Escape or Space exits and the mouse is ignored. H hold/resume · Shift+S scrub · T set T-0 · R reset · ←/→ slides · P pause · L next launch · C theme · F1 all keys.", 14, 300, 50);

            // ---- sites
            var s = page("Launch sites");
            label(s, "Count launches from these sites", 14, 16);
            var sites = new CheckedListBox { Left = 14, Top = 38, Width = 540, Height = 300, CheckOnClick = true };
            var ids = Settings.SiteIds.Split(',').Select(x => x.Trim()).ToList();
            foreach (var site in LaunchCountdownSaver.Site.All) sites.Items.Add(site.Name, site.Ids.All(i => ids.Contains(i.ToString())));
            s.Controls.Add(sites);
            var everywhere = check(s, "Every launch site in the world", 14, 350, Settings.SiteIds.Trim().Length == 0);
            everywhere.CheckedChanged += (o, e) => sites.Enabled = !everywhere.Checked; sites.Enabled = !everywhere.Checked;

            // ---- slides
            var sl = page("Slides");
            label(sl, "Slides in the slideshow (the terminal count takes over from T-1 hour)", 14, 16);
            var slides = new CheckedListBox { Left = 14, Top = 38, Width = 540, Height = 340, CheckOnClick = true };
            var chosen = Settings.Slides.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            var names = Enum.GetValues(typeof(Slide)).Cast<Slide>().Where(x => x != Slide.Terminal).ToList();
            foreach (var n in names) slides.Items.Add(Painter.Title(n), chosen.Count == 0 || chosen.Contains(n.ToString()));
            sl.Controls.Add(slides);

            // ---- 3D
            var v = page("3D corridor");
            var clouds = check(v, "Cloud volume", 14, 14, Settings.Clouds);
            var radar = check(v, "Radar volume (NEXRAD Level II, US sites)", 14, 40, Settings.RadarVolume);
            var air = check(v, "Aircraft (ADS-B), with projected track", 14, 66, Settings.Aircraft);
            var sea = check(v, "Vessels (AIS), with projected track — needs the key on the Sources tab", 14, 92, Settings.Vessels);
            var haz = check(v, "NOTAM and NOTMAR areas", 14, 118, Settings.Hazards);
            var wind = check(v, "Wind arrows and speed labels", 14, 144, Settings.Wind);
            var key = check(v, "Key", 14, 170, Settings.MapKey);
            var tele = check(v, "Calculated telemetry", 14, 196, Settings.Telemetry);
            label(v, "View", 14, 232);
            var view = new ComboBox { Left = 14, Top = 252, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            view.Items.AddRange(Enum.GetNames(typeof(Scene3D.Mode))); view.SelectedItem = Enum.GetNames(typeof(Scene3D.Mode)).Contains(Settings.View) ? Settings.View : "Orbit"; v.Controls.Add(view);
            label(v, "Vertical scale", 200, 232);
            var scale = new ComboBox { Left = 200, Top = 252, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            scale.Items.AddRange(new object[] { "1×", "2×", "5×", "10×" }); scale.SelectedItem = Settings.VerticalScale + "×"; if (scale.SelectedIndex < 0) scale.SelectedIndex = 0; v.Controls.Add(scale);
            label(v, "Cloud and wind fields for", 326, 232);
            var when = new ComboBox { Left = 326, Top = 252, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            when.Items.AddRange(new object[] { "Launch time", "Now" }); when.SelectedIndex = Settings.CloudTime == "now" ? 1 : 0; v.Controls.Add(when);
            label(v, "Show aircraft within this many miles of the launch NOTAM (0 = everything in range)", 14, 340);
            var miles = new NumericUpDown { Left = 14, Top = 360, Width = 90, Minimum = 0, Maximum = 150, Value = Math.Max(0, Math.Min(150, Settings.AircraftMiles)) }; v.Controls.Add(miles);
            note(v, "The terminal count always uses the chase view, which follows the vehicle and pulls back as it speeds up.", 14, 292, 40);

            // ---- sources
            var so = page("Sources");
            label(so, "Pad and weather stream", 14, 16);
            var stream = new TextBox { Left = 14, Top = 36, Width = 540, Text = Settings.Stream }; so.Controls.Add(stream);
            label(so, "aisstream.io key (free), for vessels", 14, 76);
            var ais = new TextBox { Left = 14, Top = 96, Width = 540, Text = Settings.AisKey }; so.Controls.Add(ais);
            label(so, "Countdown site (published launch azimuths and hazard areas come from here)", 14, 136);
            var url = new TextBox { Left = 14, Top = 156, Width = 540, Text = Settings.Url }; so.Controls.Add(url);
            label(so, "Upper-level winds, for the displays and the calculations", 14, 270);
            var upper = new ComboBox { Left = 14, Top = 290, Width = 540, DropDownStyle = ComboBoxStyle.DropDownList };
            var upperIds = new[] { "auto", "sonde", "profiler", "balloon", "model" };
            upper.Items.AddRange(new object[] { "Automatic: newest measurements when T-0 is within 6 hours, forecast otherwise", "Live balloon (SondeHub, as it climbs)", "Radar wind profile (NEXRAD VAD, about every 5 minutes, low levels)", "Published sounding (radiosonde, newest launch)", "Forecast model" });
            upper.SelectedIndex = Math.Max(0, Array.IndexOf(upperIds, Settings.UpperSource)); so.Controls.Add(upper);
            note(so, "The KSC TDRWP profiler has no public live feed, so the radar wind profile from the nearest NEXRAD stands in for it.", 14, 320, 40);
            note(so, "Everything else is fetched directly by the screensaver: launches from Launch Library 2, weather from Open-Meteo and the National Weather Service, radar from NOAA, aircraft from adsb.lol, tides from NOAA CO-OPS, space weather from NOAA SWPC.", 14, 196, 60);

            var ok = new Button { Text = "OK", Left = 414, Top = 478, Width = 84, Height = 28 };
            var cancel = new Button { Text = "Cancel", Left = 506, Top = 478, Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { tabs, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;

            ok.Click += (o, e) =>
            {
                Settings.Theme = (string)theme.SelectedItem; Settings.SecondsPerTab = (int)secs.Value; Settings.DisplayOffMinutes = (int)off.Value;
                Settings.TerminalLayout = terminal.Checked; Settings.Video = videoOn.Checked; Settings.WakeForLaunch = wake.Checked; Settings.AutoUpdate = update.Checked;
                var picked = new List<string>();
                for (int i = 0; i < LaunchCountdownSaver.Site.All.Length; i++) if (sites.GetItemChecked(i)) picked.AddRange(LaunchCountdownSaver.Site.All[i].Ids.Select(x => x.ToString()));
                Settings.SiteIds = everywhere.Checked ? "" : picked.Count > 0 ? string.Join(",", picked) : "12,27";
                var on = new List<string>();
                for (int i = 0; i < names.Count; i++) if (slides.GetItemChecked(i)) on.Add(names[i].ToString());
                Settings.Slides = on.Count == names.Count ? "" : string.Join(",", on);
                Settings.Clouds = clouds.Checked; Settings.RadarVolume = radar.Checked; Settings.Aircraft = air.Checked; Settings.Vessels = sea.Checked; Settings.Hazards = haz.Checked;
                Settings.Wind = wind.Checked; Settings.MapKey = key.Checked; Settings.Telemetry = tele.Checked; Settings.View = (string)view.SelectedItem; Settings.AircraftMiles = (int)miles.Value;
                Settings.VerticalScale = int.Parse(((string)scale.SelectedItem).TrimEnd('×')); Settings.CloudTime = when.SelectedIndex == 1 ? "now" : "t0";
                Settings.Stream = string.IsNullOrWhiteSpace(stream.Text) ? Settings.DefaultStream : stream.Text.Trim(); Settings.AisKey = ais.Text.Trim();
                Settings.Url = string.IsNullOrWhiteSpace(url.Text) ? Settings.DefaultUrl : url.Text.Trim(); Settings.UpperSource = upperIds[upper.SelectedIndex];
                Settings.Save();
                if (Settings.WakeForLaunch && !Program.IsRunning(Program.AgentMutex)) Program.Start("/agent");
                Close();
            };
            cancel.Click += (o, e) => Close();
        }

        private sealed class GrayNote : Label { public GrayNote() { ForeColor = SystemColors.GrayText; } }
    }
}

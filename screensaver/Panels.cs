// Everything drawn in 2D. Layout is in units of 1/1080 of the window height, so the same code
// fills a 4K monitor and the little preview in Screen Saver Settings.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace LaunchCountdownSaver
{
    internal enum Slide { Count, Live, Map, Corridor, Criteria, Forecast, Hourly, Alerts, Upper, Sea, Mission, Vehicle, Updates, Schedule, Raw, Terminal }

    /// <summary>Where the live parts go: the running clock, the 3D view and the video.</summary>
    internal sealed class Layout
    {
        public RectangleF Clock, View3D, Video; public float ClockSize; public bool ClockCentered = true;
        /// <summary>What the mouse can click: a rectangle and the action it stands for.</summary>
        public readonly List<KeyValuePair<RectangleF, string>> Hits = new List<KeyValuePair<RectangleF, string>>();
    }

    internal sealed partial class Painter : IDisposable
    {
        private readonly Graphics g; private readonly Theme th; private readonly float u, W, H;
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        private readonly DateTime now = DateTime.UtcNow;
        public readonly Layout Out = new Layout();
        /// <summary>Numbers from the 3D view for the telemetry column.</summary>
        public Scene3D Scene; public bool VideoAvailable, View3DAvailable = true;
        /// <summary>The tab bar: shown all the time in the app, and in the screensaver while the mouse is moving.</summary>
        public bool Bar, App, Running, Full; public List<Slide> Tabs;

        public Painter(Graphics graphics, int width, int height)
        {
            g = graphics; th = Theme.Cur; W = width; H = height; u = height / 1080f;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        }
        public void Dispose() { foreach (var f in fonts.Values) f.Dispose(); }

        // ---------------------------------------------------------------- primitives

        private Font F(float size, bool bold = false, bool mono = false)
        {
            float px = Math.Max(4f, size * u); string key = px + "|" + bold + "|" + mono; Font f;
            if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(mono ? th.Mono : th.Font, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
        private string Up(string s) { return th.Caps && s != null ? s.ToUpperInvariant() : s; }

        /// <summary>Draw text; align -1 left, 0 centre, 1 right of x. Returns the width used.</summary>
        private float Text(string s, float x, float y, float size, Color c, bool bold = false, bool mono = false, int align = -1, float maxWidth = 0)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var f = F(size, bold, mono);
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces, Trimming = StringTrimming.EllipsisCharacter })
            using (var b = new SolidBrush(c))
            {
                float w = g.MeasureString(s, f, 100000, fmt).Width;
                if (maxWidth > 0 && w > maxWidth)
                {
                    var r = new RectangleF(align < 0 ? x : align == 0 ? x - maxWidth / 2 : x - maxWidth, y, maxWidth, f.GetHeight(g) * 1.3f);
                    g.DrawString(s, f, b, r, fmt); return maxWidth;
                }
                g.DrawString(s, f, b, align < 0 ? x : align == 0 ? x - w / 2 : x - w, y, fmt);
                return w;
            }
        }

        private float Wrapped(string s, RectangleF r, float size, Color c)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var f = F(size);
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord })
            using (var b = new SolidBrush(c))
            {
                var need = g.MeasureString(s, f, new SizeF(r.Width, r.Height), fmt);
                g.DrawString(s, f, b, r, fmt);
                return Math.Min(r.Height, need.Height);
            }
        }

        private GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(Math.Min(r.Width, r.Height), rad * 2 * u);
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>A titled card. Returns the area inside it.</summary>
        private RectangleF Card(RectangleF r, string title)
        {
            using (var p = Round(r, th.Radius)) using (var b = new SolidBrush(th.Panel)) using (var pen = new Pen(th.Line, Math.Max(1, u)))
            {
                g.FillPath(b, p); g.DrawPath(pen, p);
            }
            if (th.Rule > 0) using (var b = new SolidBrush(th.Accent)) g.FillRectangle(b, r.X, r.Y, r.Width, th.Rule * u);
            float pad = 18 * u, top = pad;
            if (title != null) { Text(Up(title), r.X + pad, r.Y + pad * 0.8f, 13, th.Muted, true); top = 44 * u; }
            return new RectangleF(r.X + pad, r.Y + top, r.Width - 2 * pad, r.Height - top - pad);
        }

        private void Pill(string s, float x, float y, float size, Color c, int align = -1)
        {
            var f = F(size, true);
            float w = g.MeasureString(s, f).Width + 14 * u, h = size * u * 1.9f, x0 = align < 0 ? x : align == 0 ? x - w / 2 : x - w;
            using (var p = Round(new RectangleF(x0, y, w, h), Math.Min(th.Radius, 6))) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            Text(s, x0 + w / 2, y + h * 0.2f, size, c.GetBrightness() > 0.6 ? Color.Black : Color.White, true, false, 0);
        }

        private float Fact(float x, float y, float w, string label, string value, string sub = null, Color? color = null)
        {
            Text(Up(label), x, y, 12, th.Muted, true, false, -1, w);
            Text(value ?? "—", x, y + 20 * u, 24, color ?? th.Ink, true, false, -1, w);
            if (sub != null) Text(sub, x, y + 54 * u, 13, th.Ink2, false, false, -1, w);
            return 82 * u;
        }

        /// <summary>Balloons report hundreds of levels: keep about n of them, evenly spread in height, for a table.</summary>
        private static List<Level> Thin(List<Level> lv, int n)
        {
            if (lv.Count <= n) return lv;
            var o = new List<Level>(); double z0 = lv[0].Z, z1 = Math.Min(lv[lv.Count - 1].Z, 20000), next = z0;
            foreach (var q in lv) if (q.Z >= next && q.Z <= z1 + 1) { o.Add(q); next = q.Z + (z1 - z0) / n; }
            return o;
        }

        private void Line(Color c, float width, float x1, float y1, float x2, float y2) { using (var p = new Pen(c, Math.Max(1, width * u))) g.DrawLine(p, x1, y1, x2, y2); }

        // ---------------------------------------------------------------- shared words

        public static string Clock(Launch l, DateTime at, bool ms = true)
        {
            if (l == null || !l.Net.HasValue) return "T- --:--:--";
            var d = l.Net.Value - at; bool minus = d.Ticks > 0; d = d.Duration();
            string s = (minus ? "T-" : "T+") + (d.Days > 0 ? d.Days + "d " : "") + d.Hours.ToString("00") + ":" + d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00");
            return ms && l.Precise ? s + "." + d.Milliseconds.ToString("000") : s;
        }

        private Color StatusColor(Launch l)
        {
            switch ((l.Status ?? "").ToLowerInvariant())
            {
                case "go": case "success": return th.Good;
                case "hold": case "tbc": return th.Warn;
                case "failure": case "partial failure": return th.Bad;
                case "in flight": return th.Info;
                default: return th.Muted;
            }
        }

        private static readonly Dictionary<string, string> Zones = new Dictionary<string, string>
        {
            { "America/New_York", "Eastern Standard Time" }, { "America/Chicago", "Central Standard Time" }, { "America/Denver", "Mountain Standard Time" },
            { "America/Los_Angeles", "Pacific Standard Time" }, { "America/Anchorage", "Alaskan Standard Time" }, { "Pacific/Auckland", "New Zealand Standard Time" },
            { "America/Cayenne", "SA Eastern Standard Time" }, { "Asia/Tokyo", "Tokyo Standard Time" }, { "Asia/Kolkata", "India Standard Time" }, { "Asia/Shanghai", "China Standard Time" },
        };
        /// <summary>A time at the pad, when Windows knows the pad's time zone; otherwise this PC's local time.</summary>
        public static string PadTime(Launch l, DateTime utc, string format)
        {
            foreach (var id in new[] { l.Tz != null && Zones.ContainsKey(l.Tz) ? Zones[l.Tz] : null, l.Tz })
            {
                if (id == null) continue;
                try
                {
                    var z = TimeZoneInfo.FindSystemTimeZoneById(id); var t = TimeZoneInfo.ConvertTimeFromUtc(utc, z); var off = z.GetUtcOffset(utc);
                    return t.ToString(format) + " UTC" + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString(off.Minutes != 0 ? @"h\:mm" : "%h") + " (pad)";
                }
                catch { }
            }
            return utc.ToLocalTime().ToString(format) + " (this PC)";
        }

        private static string Rel(double seconds)
        {
            var d = TimeSpan.FromSeconds(Math.Abs(seconds));
            return (seconds < 0 ? "T-" : "T+") + (d.TotalHours >= 1 ? (int)d.TotalHours + ":" + d.Minutes.ToString("00") : d.Minutes.ToString()) + ":" + d.Seconds.ToString("00");
        }
        private static string Compass(double deg) { return new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" }[(int)Math.Round(((deg % 360) + 360) % 360 / 22.5) % 16]; }
        private static string Num(double v, string fmt = "0") { return double.IsNaN(v) ? "—" : v.ToString(fmt); }
        private static double CtoF(double c) { return c * 9 / 5 + 32; }

        // ---------------------------------------------------------------- frame

        public void Draw(Slide slide, int slideIndex, int slideCount)
        {
            g.Clear(th.Bg);
            var l = Store.Current;
            float top = 0, bottom = H;
            if (th.Marking != null)
            {
                float mh = 24 * u;
                using (var b = new SolidBrush(th.MarkBg)) { g.FillRectangle(b, 0, 0, W, mh); g.FillRectangle(b, 0, H - mh, W, mh); }
                Text(th.Marking, W / 2, 4 * u, 13, Color.White, true, false, 0); Text(th.Marking, W / 2, H - mh + 4 * u, 13, Color.White, true, false, 0);
                top = mh; bottom = H - mh;
            }
            float barTop = top;
            if (Bar) top += 80 * u;
            if (l == null)
            {
                if (Bar) TabBar(null, slide, barTop);
                Text(Up("Launch Countdown"), W / 2, H * 0.42f, 44, th.Ink, true, false, 0);
                Text(Store.LaunchNote.Length > 0 ? Store.LaunchNote : "no upcoming launches at the selected sites", W / 2, H * 0.5f, 20, th.Muted, false, false, 0);
                return;
            }
            float m = 28 * u, head = slide == Slide.Terminal ? 0 : 74 * u;
            if (slide != Slide.Terminal) Header(l, slide, top, head);
            var r = new RectangleF(m, top + head + (slide == Slide.Terminal ? m * 0.6f : 0), W - 2 * m, bottom - top - head - 40 * u - (slide == Slide.Terminal ? m * 0.6f : 0));
            switch (slide)
            {
                case Slide.Count: CountSlide(l, r); break;
                case Slide.Corridor: Corridor(l, r); break;
                case Slide.Map: MapSlide(l, r); break;
                case Slide.Forecast: Weather(l, r); break;
                case Slide.Mission: Mission(l, r); break;
                case Slide.Vehicle: Vehicle(l, r); break;
                case Slide.Updates: Updates(l, r); break;
                case Slide.Live: Live(l, r); break;
                case Slide.Criteria: Criteria(l, r); break;
                case Slide.Hourly: Hourly(l, r); break;
                case Slide.Alerts: Alerts(l, r); break;
                case Slide.Upper: Upper(l, r); break;
                case Slide.Sea: SeaSlide(l, r); break;
                case Slide.Raw: Raw(l, r); break;
                case Slide.Schedule: Schedule(r); break;
                case Slide.Terminal: Terminal(l, r); break;
            }
            Footer(bottom, slideIndex, slideCount);
            if (Bar) TabBar(l, slide, barTop);
        }

        private void Header(Launch l, Slide slide, float top, float h)
        {
            float m = 28 * u, y = top + 20 * u;
            float x = m + Text(Up("Launch Countdown"), m, y, 20, th.Ink, true) + 18 * u;
            Text(Up(Title(slide)), x, y + 4 * u, 15, th.Accent, true);
            if (slide != Slide.Count)
            {
                // the running clock, small, on every slide that is not the big count
                Out.Clock = new RectangleF(W / 2 - 330 * u, y - 8 * u, 660 * u, 46 * u); Out.ClockSize = 32;
                Text(l.Name, W / 2 + 350 * u, y + 4 * u, 15, th.Ink2, false, false, -1, W / 2 - 350 * u - 330 * u);
            }
            Text(now.ToString("HH:mm:ss") + " UTC", W - m, y, 20, th.Ink2, false, true, 1);
            Pill(Up(l.StatusName.Length > 0 ? l.StatusName : l.Status), W - m - 190 * u, y - 2 * u, 13, StatusColor(l), 1);
            Line(th.Line, 1, m, top + h - 10 * u, W - m, top + h - 10 * u);
        }

        public static string Title(Slide s)
        {
            switch (s)
            {
                case Slide.Live: return "Live video"; case Slide.Map: return "Range map and radar"; case Slide.Corridor: return "Ascent corridor 3D"; case Slide.Criteria: return "Launch commit criteria";
                case Slide.Forecast: return "Forecast"; case Slide.Hourly: return "Hourly"; case Slide.Alerts: return "National Weather Service"; case Slide.Upper: return "Upper air";
                case Slide.Sea: return "Sea state and space weather"; case Slide.Mission: return "Mission"; case Slide.Vehicle: return "Vehicle"; case Slide.Updates: return "Updates";
                case Slide.Schedule: return "Schedule"; case Slide.Raw: return "All data"; case Slide.Terminal: return "Terminal count"; default: return "Countdown";
            }
        }

        public static string Short(Slide s)
        {
            switch (s)
            {
                case Slide.Count: return "Count"; case Slide.Corridor: return "3D"; case Slide.Alerts: return "NWS"; case Slide.Sea: return "Sea + space"; case Slide.Raw: return "Data";
                default: return s.ToString();
            }
        }

        /// <summary>The controls and the tabs, as two rows across the top. Every button is also a key.</summary>
        private void TabBar(Launch l, Slide active, float y0)
        {
            float m = 28 * u, h = 32 * u, x = m, y = y0 + 6 * u, gap = 6 * u;
            using (var b = new SolidBrush(th.Bg)) g.FillRectangle(b, 0, y0, W, 80 * u);
            Func<string, string, bool, Color?, float> button = (label, action, on, tint) =>
            {
                float w = g.MeasureString(Up(label), F(13, true)).Width + 18 * u; var r = new RectangleF(x, y, w, h);
                using (var p = Round(r, Math.Min(th.Radius, 6))) using (var b = new SolidBrush(on ? (tint ?? th.Accent) : th.Panel)) using (var pen = new Pen(tint ?? th.Line, Math.Max(1, u))) { g.FillPath(b, p); g.DrawPath(pen, p); }
                var fill = on ? (tint ?? th.Accent) : th.Panel;
                Text(Up(label), x + w / 2, y + 7 * u, 13, on ? (fill.GetBrightness() > 0.6 ? Color.Black : Color.White) : th.Ink, true, false, 0);
                Out.Hits.Add(new KeyValuePair<RectangleF, string>(r, action)); x += w + gap; return w;
            };
            bool holding = l != null && Count.Holding(l), scrubbed = l != null && Count.Scrubbed(l);
            button(holding ? "Resume" : "Hold", "hold", holding, th.Warn); button("Scrub", "scrub", scrubbed, th.Bad); button("Set T-0", "t0", false, null); button("Reset", "reset", false, null);
            x += 14 * u;
            button(Running ? "Slideshow on" : "Slideshow off", "slideshow", Running, null); button("−", "slower", false, null);
            x += Text(Settings.SecondsPerTab + " s", x, y + 7 * u, 13, th.Ink2, false, true) + gap; button("+", "faster", false, null);
            x += 14 * u;
            button("Next launch", "launch", false, null); button("Theme", "theme", false, null); button("Settings", "settings", false, null);
            button(Full ? "Window" : "Full screen", "full", false, null);
            if (!App) button("Exit", "exit", false, null);
            x = m; y = y0 + 44 * u;
            foreach (var s in Tabs ?? new List<Slide>()) button(Short(s), "tab:" + s, s == active, null);
        }

        /// <summary>Things laid over any slide: the hold or scrub state, a message after a key press, the key list.</summary>
        public void Banner(Launch l, bool paused, string toast, bool help)
        {
            float y = H - 110 * u;
            if (l != null && (Count.Holding(l) || Count.Scrubbed(l)))
            {
                bool scrub = Count.Scrubbed(l); var c = scrub ? th.Bad : th.Warn;
                using (var b = new SolidBrush(c)) g.FillRectangle(b, 0, y, W, 44 * u);
                Text(scrub ? "SCRUBBED  ·  T sets a new T-0  ·  R resets" : "HOLD  ·  H resumes  ·  T sets a new T-0", W / 2, y + 9 * u, 20, c.GetBrightness() > 0.6 ? Color.Black : Color.White, true, false, 0);
                y -= 50 * u;
            }
            else if (l != null && Count.Mine(l)) Text("T-0 SET BY YOU  ·  kept until the source publishes a new time  ·  R resets", W / 2, H - 58 * u, 13, th.Warn, true, false, 0);
            if (toast != null)
            {
                float w = Math.Min(W * 0.8f, g.MeasureString(toast, F(20, true)).Width + 60 * u);
                using (var p = Round(new RectangleF(W / 2 - w / 2, y, w, 44 * u), 8)) using (var b = new SolidBrush(Color.FromArgb(235, th.Panel))) using (var pen = new Pen(th.Accent, 2 * u)) { g.FillPath(b, p); g.DrawPath(pen, p); }
                Text(toast, W / 2, y + 9 * u, 20, th.Ink, true, false, 0, w - 30 * u);
            }
            if (paused) Text("SLIDESHOW PAUSED", W - 28 * u, H - 58 * u, 13, th.Warn, true, false, 1);
            if (!help) return;
            var r = new RectangleF(W / 2 - 450 * u, H / 2 - 380 * u, 900 * u, 760 * u);
            using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, 0, 0, W, H);
            var c2 = Card(r, "Keys · moving the mouse brings up the tabs and buttons; it never exits");
            string[][] keys =
            {
                new[] { "Esc  Space", App ? "leave full screen / slideshow on or off" : "exit" }, new[] { "Mouse", "click tabs and buttons · drag and wheel in the 3D view" }, new[] { "H", "hold / resume (T-0 moves by the length of the hold)" }, new[] { "Shift+S", "scrub" }, new[] { "T", "enter a new T-0" },
                new[] { "R", "reset to the published T-0" }, new[] { "←  →", "previous / next slide" }, new[] { "1 – 9", "jump to a slide" }, new[] { "P", "pause the slideshow" },
                new[] { "+  −", "seconds per slide" }, new[] { "L", "follow the next launch in the list" }, new[] { "C", "theme" }, new[] { "E", "terminal-count layout on / off" },
                new[] { "I", "live video on / off" }, new[] { "V", "3D view" }, new[] { "X", "3D vertical scale" }, new[] { "O  D", "cloud volume / radar volume" },
                new[] { "A  B  N", "aircraft / vessels / NOTAM and NOTMAR areas" }, new[] { "W", "wind arrows and labels" }, new[] { "G  Y", "telemetry / key" },
                new[] { "M", "cloud and wind fields for launch time or now" }, new[] { "U", "upper-wind source: automatic, profiler, balloon, model" }, new[] { "F1  K", "this list" }, new[] { "F11", "full screen (app)" },
            };
            float ky = c2.Y, lh = (c2.Height) / keys.Length;
            foreach (var k in keys) { Text(k[0], c2.X, ky, 17, th.Accent, true, true); Text(k[1], c2.X + 190 * u, ky, 17, th.Ink); ky += lh; }
        }

        private void Footer(float bottom, int index, int count)
        {
            float y = bottom - 30 * u, m = 28 * u;
            string age = Store.LaunchesAt == DateTime.MinValue ? "" : "launch data " + Math.Max(0, (int)(now - Store.LaunchesAt).TotalMinutes) + " min old";
            Text((Store.LaunchNote.Length > 0 ? Store.LaunchNote + " · " : "") + age + " · Launch Library 2, Open-Meteo, NOAA, adsb.lol, Esri", m, y, 12, th.Muted);
            for (int i = 0; i < count; i++)
                using (var b = new SolidBrush(i == index ? th.Accent : th.Line)) g.FillEllipse(b, W - m - (count - i) * 16 * u, y + 4 * u, 8 * u, 8 * u);
        }

        // ---------------------------------------------------------------- countdown

        private void CountSlide(Launch l, RectangleF r)
        {
            float cx = r.X + r.Width / 2, y = r.Y + 6 * u;
            Text(l.Name, cx, y, 46, th.Ink, true, false, 0, r.Width); y += 66 * u;
            Text(string.Join("  ·  ", new[] { l.Provider, l.PadName, l.Location }.Where(s => !string.IsNullOrEmpty(s))), cx, y, 20, th.Ink2, false, false, 0, r.Width); y += 44 * u;

            float ch = 250 * u;
            var cr = new RectangleF(r.X, y, r.Width, ch); Card(cr, null);
            Out.ClockSize = l.Net.HasValue && (l.Net.Value - now).Duration().Days > 0 ? 122 : 150;
            Out.Clock = new RectangleF(cr.X + 10 * u, cr.Y + 26 * u, cr.Width - 20 * u, 170 * u);
            if (l.Net.HasValue)
            {
                string when = PadTime(l, l.Net.Value, "ddd d MMM yyyy  HH:mm:ss") + "   ·   " + l.Net.Value.ToString("HH:mm:ss") + " UTC";
                if (l.WinClose.HasValue && l.WinOpen.HasValue && (l.WinClose.Value - l.WinOpen.Value).TotalSeconds > 1) when += "   ·   window to " + l.WinClose.Value.ToString("HH:mm") + " UTC";
                if (!l.Precise) when += "   ·   time not confirmed";
                Text(when, cx, cr.Bottom - 46 * u, 18, th.Ink2, false, false, 0, cr.Width - 40 * u);
            }
            y = cr.Bottom + 22 * u;

            // facts
            var w = Store.Weather; int hr = w == null || !l.Net.HasValue ? -1 : w.Hour(l.Net.Value); bool atT0 = hr >= 0;
            if (w != null && hr < 0) hr = w.Hour(now);
            float fw = (r.Width - 5 * 16 * u) / 6, fh = 118 * u; int i = 0;
            Func<string, RectangleF> tile = title => { var q = Card(new RectangleF(r.X + i * (fw + 16 * u), y, fw, fh), null); i++; return q; };
            var t1 = tile(""); Fact(t1.X, t1.Y, t1.Width, "Vehicle", l.Rocket, l.RocketCount > 0 ? l.RocketOk + " of " + l.RocketCount + " flights successful" : "first flight of this configuration");
            var t2 = tile(""); Fact(t2.X, t2.Y, t2.Width, "Orbit", l.Orbit ?? "—", l.OrbitName);
            var t3 = tile(""); Fact(t3.X, t3.Y, t3.Width, "Launch azimuth", l.Heading.ToString("0") + "°" + (l.HeadingEstimated ? " est." : ""), "≈ " + Flight.Inclination(l.Lat, l.Heading).ToString("0.0") + "° inclination");
            var t4 = tile(""); Fact(t4.X, t4.Y, t4.Width, "Weather", l.Prob.HasValue ? l.Prob.Value.ToString("0") + "% GO" : "no forecast yet", l.Concerns ?? (l.Prob.HasValue ? "range forecast" : "published closer to launch"), l.Prob.HasValue ? (l.Prob >= 70 ? th.Good : l.Prob >= 40 ? th.Warn : th.Bad) : th.Ink);
            string wxWhen = atT0 ? "at T-0" : "now";
            var t5 = tile(""); Fact(t5.X, t5.Y, t5.Width, "Surface wind " + wxWhen, hr < 0 ? null : Num(w.Wind[hr]) + " kt " + Compass(w.Dir[hr]), hr < 0 ? null : "gusting " + Num(w.Gust[hr]) + " kt");
            var t6 = tile(""); Fact(t6.X, t6.Y, t6.Width, "Cloud " + wxWhen, hr < 0 ? null : Num(Math.Max(w.Low[hr], Math.Max(w.Mid[hr], w.High[hr]))) + "%", hr < 0 ? null : "low " + Num(w.Low[hr]) + " · mid " + Num(w.Mid[hr]) + " · high " + Num(w.High[hr]));
            y += fh + 22 * u;

            Timeline(l, new RectangleF(r.X, y, r.Width, r.Bottom - y), true);
        }

        /// <summary>The countdown events. Horizontal strip, or a list when there is height for it.</summary>
        private void Timeline(Launch l, RectangleF r, bool strip)
        {
            var c = Card(r, "Timeline" + (l.Timeline.Count > 0 && l.Timeline[0].Nominal ? " · nominal profile, not published by the provider" : ""));
            double t = l.Net.HasValue ? (now - l.Net.Value).TotalSeconds : double.NaN;
            var ev = l.Timeline; if (ev.Count == 0) return;
            if (!strip)
            {
                // a list, scrolled so the next event stays in view
                float rowH = 34 * u; int rows = Math.Max(1, (int)(c.Height / rowH));
                int next = ev.FindIndex(e => e.T > t); if (next < 0) next = ev.Count;
                int first = Math.Max(0, Math.Min(ev.Count - rows, next - 2));
                for (int i = first; i < Math.Min(ev.Count, first + rows); i++)
                {
                    float y = c.Y + (i - first) * rowH; bool done = !double.IsNaN(t) && ev[i].T <= t, isNext = i == next;
                    if (isNext) using (var b = new SolidBrush(Color.FromArgb(40, th.Accent))) g.FillRectangle(b, c.X - 6 * u, y - 3 * u, c.Width + 12 * u, rowH - 4 * u);
                    using (var b = new SolidBrush(done ? th.Good : isNext ? th.Accent : th.Line)) g.FillEllipse(b, c.X, y + 7 * u, 10 * u, 10 * u);
                    Text(Rel(ev[i].T), c.X + 24 * u, y, 17, done ? th.Muted : th.Ink, false, true);
                    Text(ev[i].Name, c.X + 130 * u, y, 17, done ? th.Muted : th.Ink, isNext, false, -1, c.Width - 280 * u);
                    if (!double.IsNaN(t) && !done) Text("in " + Rel(ev[i].T - t).Substring(2), c.Right, y, 15, isNext ? th.Accent : th.Muted, false, true, 1);
                }
                return;
            }
            double lo = ev[0].T, hi = ev[ev.Count - 1].T; if (hi - lo < 1) hi = lo + 1;
            float x0 = c.X + 30 * u, x1 = c.Right - 30 * u, yl = c.Y + c.Height * 0.42f;
            Func<double, float> px = s => x0 + (float)((s - lo) / (hi - lo)) * (x1 - x0);
            Line(th.Line, 3, x0, yl, x1, yl);
            if (!double.IsNaN(t) && t > lo) Line(th.Good, 3, x0, yl, px(Math.Min(hi, t)), yl);
            float lastLabel = -1e9f; int row = 0;
            foreach (var e in ev)
            {
                float x = px(e.T); bool done = !double.IsNaN(t) && e.T <= t;
                using (var b = new SolidBrush(done ? th.Good : th.Ink2)) g.FillEllipse(b, x - 5 * u, yl - 5 * u, 10 * u, 10 * u);
                row = x - lastLabel < 150 * u ? (row + 1) % 2 : 0; lastLabel = x;
                float ty = row == 0 ? yl + 14 * u : yl - 50 * u;
                Text(e.Name, x, ty, 13, done ? th.Muted : th.Ink, false, false, 0, 190 * u); Text(Rel(e.T), x, ty + 17 * u, 12, th.Muted, false, true, 0);
            }
            if (!double.IsNaN(t) && t >= lo && t <= hi) using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, px(t) - 8 * u, yl - 8 * u, 16 * u, 16 * u);
        }

        // ---------------------------------------------------------------- corridor and telemetry

        private void Corridor(Launch l, RectangleF r)
        {
            // the telemetry and the key share a column; with both off the view takes the whole slide
            float side = Settings.Telemetry || Settings.MapKey ? 430 * u : 0, gap = side > 0 ? 16 * u : 0;
            var view = new RectangleF(r.X, r.Y, r.Width - side - gap, r.Height);
            if (View3DAvailable) Out.View3D = view; else MapPanel(l, view, 260);
            if (side <= 0) return;
            float keyH = Settings.MapKey ? (Settings.Telemetry ? 268 * u : r.Height) : 0;
            if (Settings.Telemetry) Readout(l, new RectangleF(view.Right + gap, r.Y, side, r.Height - keyH - (keyH > 0 ? gap : 0)));
            if (Settings.MapKey) Key(new RectangleF(view.Right + gap, r.Bottom - keyH, side, keyH));
        }

        private void Key(RectangleF r)
        {
            var c = Card(r, "Key"); float y = c.Y, lh = Math.Min(27 * u, c.Height / 8);
            Action<Color, string> row = (col, s) => { using (var b = new SolidBrush(col)) g.FillRectangle(b, c.X, y + 7 * u, 20 * u, 4 * u); Text(s, c.X + 32 * u, y, 14, th.Ink2, false, false, -1, c.Width - 32 * u); y += lh; };
            row(th.Track, "Nominal ascent and its corridor");
            row(Color.FromArgb(180, 184, 188), "Aircraft at true height · line to the surface");
            row(Color.FromArgb(180, 184, 188), "Track: tick at 5 minutes, X at 10");
            row(Color.FromArgb(255, 220, 30), "Flashing red/yellow: inside an area now");
            row(Color.FromArgb(255, 40, 40), "Flashing red: forecast inside at T-0");
            row(Color.FromArgb(60, 220, 90), "Green helicopter: JOLLY rescue, cleared in");
            row(Color.FromArgb(255, 210, 31), "NOTAM airspace"); row(Color.FromArgb(255, 90, 54), "NOTMAR sea area");
        }

        private void Readout(Launch l, RectangleF r)
        {
            var c = Card(r, "Calculated telemetry");
            double t = l.Net.HasValue ? (now - l.Net.Value).TotalSeconds : double.NaN;
            bool flying = t >= 0 && t <= Flight.End;
            var w = Store.Weather; int hr = w == null ? -1 : (l.Net.HasValue && w.Hour(l.Net.Value) >= 0 ? w.Hour(l.Net.Value) : w.Hour(now));
            string src; var prof = Winds.For(l, out src);
            if (prof.Count < 2) { prof = null; src = null; }
            double at = flying ? t : 72; // before liftoff, show what the profile predicts at maximum dynamic pressure
            var s = Flight.At(at, l, prof, src);
            float y = c.Y, lh = Math.Min(30 * u, c.Height / 23.5f);
            Text(flying ? Rel(t) + (s.MaxQ ? "   MAX-Q" : "") : "PREDICTED AT MAX-Q  (T+1:12)", c.X, y, 15, flying ? th.Accent : th.Warn, true); y += lh * 1.2f;
            Action<string, string> row = (k, v) => { Text(Up(k), c.X, y + 3 * u, 13, th.Muted); Text(v, c.Right, y, 17, th.Ink, false, true, 1); y += lh; };
            row("Altitude", s.Alt.ToString("0.0") + " km");
            row("Downrange", s.Downrange.ToString("0.0") + " km");
            row("Speed", (s.V * 3600).ToString("#,0") + " km/h");
            row("Vertical speed", (s.Vv * 1000).ToString("#,0") + " m/s");
            row("Flight path angle", s.Gamma.ToString("0.0") + "°");
            row("Acceleration", s.G.ToString("0.00") + " g");
            row("Mach", s.Mach.ToString("0.00"));
            row("Dynamic pressure", (s.Q / 1000).ToString("0.0") + " kPa");
            row("Air temperature", s.TempC.ToString("0") + " °C");
            row("Air pressure", s.P.ToString(s.P < 10 ? "0.00" : "0") + " hPa");
            if (s.WindKt.HasValue)
            {
                row("Wind at altitude", s.WindKt.Value.ToString("0") + " kt from " + s.WindDir.Value.ToString("000") + "°");
                row("Headwind / crosswind", s.Head.Value.ToString("+0;-0") + " / " + s.Cross.Value.ToString("+0;-0") + " kt");
            }
            if (s.Tw.HasValue) row("Liftoff thrust to weight", s.Tw.Value.ToString("0.00"));
            y += 4 * u; Text("nominal ascent through " + s.Env, c.X, y, 12, th.Muted, false, false, -1, c.Width); y += lh;

            Line(th.Line, 1, c.X, y, c.Right, y); y += 12 * u;
            Text(Up("Range traffic"), c.X, y, 13, th.Muted, true); y += lh;
            bool live = (now - Store.PlanesAt).TotalSeconds < 60;
            if (Scene != null && View3DAvailable)
            {
                row("Aircraft within 220 km", !Settings.Aircraft ? "off" : live ? Scene.Aircraft.ToString() : "feed not answering");
                row("Vessels within 220 km", !Settings.Vessels ? "off" : Store.VesselNote.Length > 0 ? Store.VesselNote : Scene.Vessels.ToString());
                Text(Up("Inside a hazard area"), c.X, y + 3 * u, 13, th.Muted); Text(Scene.Inside.ToString(), c.Right, y, 17, Scene.Inside > 0 ? th.Bad : th.Ink, true, true, 1); y += lh;
                Text(Up("Inbound at T-0"), c.X, y + 3 * u, 13, th.Muted); Text(Scene.Inbound.ToString(), c.Right, y, 17, Scene.Inbound > 0 ? th.Bad : th.Ink, true, true, 1); y += lh;
                row("Hazard areas", Scene.Notams + " NOTAM · " + Scene.Notmars + " NOTMAR");
            }
            var cl = Store.Cloud;
            if (cl != null) row("Cloud max low/mid/high", (cl.MaxLow * 100).ToString("0") + " / " + (cl.MaxMid * 100).ToString("0") + " / " + (cl.MaxHigh * 100).ToString("0") + " %");
            if (cl != null) { Text("cloud volume valid " + cl.Valid.ToString("ddd HH:mm") + " UTC", c.X, y, 12, th.Muted); y += lh * 0.8f; }
            var rv = Store.Volume;
            Text(rv != null ? "radar volume " + rv.Id + " " + rv.Time.ToString("HH:mm") + " UTC · " + rv.Tilts + " tilts · max " + rv.MaxDbz.ToString("0") + " dBZ · tops " + rv.EchoTop.ToString("0.0") + " km" : Store.VolumeNote, c.X, y, 12, th.Muted, false, false, -1, c.Width);
        }

        // ---------------------------------------------------------------- map

        private void MapSlide(Launch l, RectangleF r)
        {
            float side = 430 * u;
            MapPanel(l, new RectangleF(r.X, r.Y, r.Width - side - 16 * u, r.Height), 300);
            var c = Card(new RectangleF(r.Right - side, r.Y, side, r.Height), "Key");
            float y = c.Y, lh = 30 * u;
            Action<Color, string> key = (col, s) => { using (var b = new SolidBrush(col)) g.FillRectangle(b, c.X, y + 6 * u, 22 * u, 5 * u); Text(s, c.X + 34 * u, y, 15, th.Ink2, false, false, -1, c.Width - 34 * u); y += lh; };
            key(th.Track, "Ground track, azimuth " + l.Heading.ToString("0") + "°" + (l.HeadingEstimated ? " (estimated)" : ""));
            key(Color.FromArgb(140, th.Track), "Ascent corridor footprint");
            key(Color.FromArgb(255, 210, 31), "NOTAM airspace (FAA space operations)");
            key(Color.FromArgb(255, 90, 54), "NOTMAR sea area");
            key(Color.FromArgb(180, 184, 188), "Aircraft, with track for the next 10 min");
            key(th.Bad, "Aircraft inside an area or the corridor");
            y += 10 * u; Line(th.Line, 1, c.X, y, c.Right, y); y += 14 * u;
            int n = Store.Planes.Count(p => !p.Ground && Flight.KmBetween(l.Lat, l.Lon, p.Lat, p.Lon) <= 300);
            bool live = (now - Store.PlanesAt).TotalSeconds < 60;
            Fact(c.X, y, c.Width, "Aircraft on the map", live ? n.ToString() : "—", live ? "live, updated every 10 seconds" : "aircraft feed not answering"); y += 92 * u;
            Fact(c.X, y, c.Width, "Radar", Store.Radar != null ? Store.Radar.At.ToString("HH:mm") + " UTC" : "—", Store.Radar != null ? "NOAA composite reflectivity" : "no radar picture for this site"); y += 92 * u;
            Fact(c.X, y, c.Width, "Hazard areas", Store.Areas.Count(a => !a.To.HasValue || a.To.Value > now).ToString(), "from the countdown site's snapshot");
        }

        /// <summary>North-up map: base map, radar, corridor, hazard areas and aircraft, halfKm from the pad to the top edge.</summary>
        private void MapPanel(Launch l, RectangleF r, double halfKm)
        {
            Store.TrafficWanted = DateTime.UtcNow;
            using (var p = Round(r, th.Radius)) using (var b = new SolidBrush(Color.FromArgb(18, 20, 23))) g.FillPath(b, p);
            var state = g.Save();
            using (var p = Round(r, th.Radius)) g.SetClip(p, CombineMode.Intersect);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = (float)(r.Height / 2 / halfKm); // pixels per km
            Func<double, double, PointF> pt = (lat, lon) => new PointF(cx + (float)(Flight.R * (lon - l.Lon) * Math.PI / 180 * Math.Cos(l.Lat * Math.PI / 180)) * k, cy - (float)(Flight.R * (lat - l.Lat) * Math.PI / 180) * k);
            foreach (var img in new[] { Store.Map, Store.Radar })
            {
                if (img == null || img.Lat != l.Lat || img.Lon != l.Lon) continue;
                float half = (float)img.HalfKm * k;
                try { lock (img) g.DrawImage(img.Image, cx - half, cy - half, half * 2, half * 2); } catch { }
            }
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1, u)))
                foreach (int ring in new[] { 50, 100, 200 }) { g.DrawEllipse(pen, cx - ring * k, cy - ring * k, ring * 2 * k, ring * 2 * k); Text(ring + " km", cx + 4 * u, cy - ring * k - 16 * u, 11, Color.FromArgb(150, 160, 170)); }

            // corridor footprint and ground track
            var left = new List<PointF>(); var right = new List<PointF>(); var mid = new List<PointF>();
            for (double d = 0; d <= Flight.CorridorKm; d += 20)
            {
                double la, lo, la2, lo2; Flight.Destination(l.Lat, l.Lon, l.Heading, d, out la, out lo); mid.Add(pt(la, lo));
                Flight.Destination(la, lo, l.Heading - 90, Flight.CorridorHalf(d), out la2, out lo2); left.Add(pt(la2, lo2));
                Flight.Destination(la, lo, l.Heading + 90, Flight.CorridorHalf(d), out la2, out lo2); right.Add(pt(la2, lo2));
            }
            right.Reverse();
            using (var b = new SolidBrush(Color.FromArgb(28, th.Track))) g.FillPolygon(b, left.Concat(right).ToArray());
            using (var pen = new Pen(Color.FromArgb(120, th.Track), Math.Max(1, u))) g.DrawPolygon(pen, left.Concat(right).ToArray());
            using (var pen = new Pen(th.Track, 2.5f * u)) g.DrawLines(pen, mid.ToArray());

            foreach (var a in Store.Areas)
            {
                if (a.To.HasValue && a.To.Value < now) continue;
                var col = a.Kind == "notmar" ? Color.FromArgb(255, 90, 54) : Color.FromArgb(255, 210, 31);
                foreach (var ring in a.Rings)
                {
                    var pts = ring.Select(q => pt(q[0], q[1])).ToArray(); if (pts.Length < 3) continue;
                    using (var b = new SolidBrush(Color.FromArgb(30, col))) g.FillPolygon(b, pts);
                    using (var pen = new Pen(col, 1.5f * u)) g.DrawPolygon(pen, pts);
                }
            }

            double unix = Store.Unix(now); double toT0 = l.Net.HasValue ? (l.Net.Value - now).TotalMinutes : double.NaN;
            bool hot = toT0 <= 120 && toT0 >= -15;
            foreach (var v in Store.Planes)
            {
                double age = unix - v.T; if (v.Ground || age > 1200) continue;
                double kmMin = v.Kt * 1.852 / 60, la, lo; Flight.Destination(v.Lat, v.Lon, v.Track, kmMin * age / 60, out la, out lo);
                var p = pt(la, lo); if (!r.Contains(p)) continue;
                bool cleared = Flight.IsJolly(v), alert = !cleared && ((hot && Flight.InCorridor(l, la, lo)) || Store.Areas.Any(a => (!a.To.HasValue || a.To.Value > now) && (!a.From.HasValue || (a.From.Value - now).TotalHours < 2) && a.Rings.Any(q => Flight.InRing(la, lo, q))));
                var col = alert ? th.Bad : cleared ? Color.FromArgb(60, 220, 90) : Color.FromArgb(200, 204, 208);
                float hx = (float)Math.Sin(v.Track * Math.PI / 180), hy = -(float)Math.Cos(v.Track * Math.PI / 180), s = (alert ? 9 : 6) * u;
                double horizon = kmMin > 0.05 ? Math.Min(10, 25 / kmMin) : 0;
                using (var pen = new Pen(Color.FromArgb(150, col), Math.Max(1, u))) g.DrawLine(pen, p.X, p.Y, p.X + hx * (float)(kmMin * horizon) * k, p.Y + hy * (float)(kmMin * horizon) * k);
                using (var b = new SolidBrush(col))
                    g.FillPolygon(b, new[] { new PointF(p.X + hx * s * 1.4f, p.Y + hy * s * 1.4f), new PointF(p.X - hx * s - hy * s * 0.8f, p.Y - hy * s + hx * s * 0.8f), new PointF(p.X - hx * s * 0.4f, p.Y - hy * s * 0.4f), new PointF(p.X - hx * s + hy * s * 0.8f, p.Y - hy * s - hx * s * 0.8f) });
                if (alert) Text(v.Name, p.X + 12 * u, p.Y - 8 * u, 12, th.Bad, true);
            }
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u);
            Text(l.PadName, cx + 10 * u, cy + 6 * u, 13, Color.White, true);
            Text("N", r.Right - 34 * u, r.Y + 14 * u, 16, Color.White, true); Line(Color.White, 2, r.Right - 28 * u, r.Y + 62 * u, r.Right - 28 * u, r.Y + 40 * u);
            g.Restore(state);
            using (var p = Round(r, th.Radius)) using (var pen = new Pen(th.Line, Math.Max(1, u))) g.DrawPath(pen, p);
        }

        // ---------------------------------------------------------------- weather

        private struct Check { public string Name, Value; public int Level; }

        private void Weather(Launch l, RectangleF r)
        {
            var w = Store.Weather;
            if (w == null || w.Lat != l.Lat) { Card(r, "Weather"); Text("weather loading…", r.X + r.Width / 2, r.Y + r.Height / 2, 20, th.Muted, false, false, 0); return; }
            int hNow = w.Hour(now), hT0 = l.Net.HasValue ? w.Hour(l.Net.Value) : -1, hr = hT0 >= 0 ? hT0 : hNow;
            string when = hT0 >= 0 ? "Forecast at T-0" : "Conditions now";
            float gap = 16 * u, colW = 400 * u, upperW = 470 * u;

            var c = Card(new RectangleF(r.X, r.Y, colW, r.Height * 0.54f), when + " · " + l.PadName);
            if (hr >= 0)
            {
                float y = c.Y, half = c.Width / 2;
                Fact(c.X, y, half, "Temperature", Num(CtoF(w.Temp[hr])) + " °F", Num(w.Temp[hr]) + " °C · dew point " + Num(CtoF(w.Dew[hr])) + " °F");
                Fact(c.X + half, y, half, "Humidity", Num(w.Hum[hr]) + "%", Num(w.Msl[hr]) + " hPa"); y += 92 * u;
                Fact(c.X, y, half, "Wind", Num(w.Wind[hr]) + " kt", "from " + Num(w.Dir[hr], "000") + "° " + Compass(w.Dir[hr]));
                Fact(c.X + half, y, half, "Gusts", Num(w.Gust[hr]) + " kt"); y += 92 * u;
                Fact(c.X, y, half, "Cloud low / mid / high", Num(w.Low[hr]) + " / " + Num(w.Mid[hr]) + " / " + Num(w.High[hr]), "percent cover");
                Fact(c.X + half, y, half, "Visibility", Num(w.Vis[hr] / 1609.34, "0.#") + " mi", Num(w.Vis[hr] / 1000, "0.#") + " km"); y += 92 * u;
                Fact(c.X, y, half, "Rain chance", Num(w.Pop[hr]) + "%", Num(w.Rain[hr], "0.0") + " mm in the hour");
                Fact(c.X + half, y, half, "CAPE", Num(w.Cape[hr]) + " J/kg", w.Cape[hr] > 1000 ? "thunderstorm fuel present" : "little thunderstorm fuel");
            }

            // indicative checks
            var k = Card(new RectangleF(r.X, r.Y + r.Height * 0.54f + gap, colW, r.Height * 0.46f - gap), "Launch weather watch · indicative only");
            if (hr >= 0)
            {
                string windSrc; var prof = Winds.For(l, out windSrc); double jet = prof.Where(p => p.P <= 300 && p.P >= 150 && !double.IsNaN(p.Kt)).Select(p => p.Kt).DefaultIfEmpty(double.NaN).Max();
                Func<double, double, double, int> lv = (v, watch, stop) => double.IsNaN(v) ? 0 : v >= stop ? 2 : v >= watch ? 1 : 0;
                var checks = new[]
                {
                    new Check { Name = "Surface wind", Value = Num(w.Wind[hr]) + " kt", Level = lv(w.Wind[hr], 22, 30) },
                    new Check { Name = "Gusts", Value = Num(w.Gust[hr]) + " kt", Level = lv(w.Gust[hr], 28, 35) },
                    new Check { Name = "Rain", Value = Num(w.Pop[hr]) + "% · " + Num(w.Rain[hr], "0.0") + " mm", Level = Math.Max(lv(w.Rain[hr], 0.1, 0.5), lv(w.Pop[hr], 40, 101)) },
                    new Check { Name = "Lightning potential", Value = "CAPE " + Num(w.Cape[hr]), Level = lv(w.Cape[hr], 1000, 2000) },
                    new Check { Name = "Thick cloud", Value = "low+mid " + Num(Math.Max(w.Low[hr], w.Mid[hr])) + "%", Level = lv(Math.Max(w.Low[hr], w.Mid[hr]), 90, 101) },
                    new Check { Name = "Upper-level wind", Value = Num(jet) + " kt max", Level = lv(jet, 100, 130) },
                };
                float y = k.Y, lh = Math.Min(40 * u, (k.Height - 26 * u) / checks.Length);
                foreach (var ch in checks)
                {
                    var col = ch.Level == 2 ? th.Bad : ch.Level == 1 ? th.Warn : th.Good;
                    Pill(ch.Level == 2 ? "NO-GO" : ch.Level == 1 ? "WATCH" : "GO", k.X, y, 12, col);
                    Text(ch.Name, k.X + 92 * u, y + 2 * u, 16, th.Ink, false, false, -1, k.Width - 250 * u); Text(ch.Value, k.Right, y + 2 * u, 15, th.Ink2, false, true, 1);
                    y += lh;
                }
                Text("simple thresholds on the forecast, not the range's commit criteria", k.X, k.Bottom - 16 * u, 11, th.Muted, false, false, -1, k.Width);
            }

            // charts
            float chX = r.X + colW + gap, chW = r.Width - colW - upperW - 2 * gap, chH = (r.Height - 2 * gap) / 3;
            double t0 = l.Net.HasValue ? Store.Unix(l.Net.Value) : double.NaN;
            Chart(new RectangleF(chX, r.Y, chW, chH), "Wind and gusts · kt", w.Time, t0, new[] { w.Wind, w.Gust }, new[] { th.Info, th.Warn }, new[] { "wind", "gust" }, 0);
            Chart(new RectangleF(chX, r.Y + chH + gap, chW, chH), "Cloud cover · %", w.Time, t0, new[] { w.Low, w.Mid, w.High }, new[] { th.Info, th.Good, th.Ink2 }, new[] { "low", "mid", "high" }, 100);
            Chart(new RectangleF(chX, r.Y + 2 * (chH + gap), chW, chH), "Rain chance · %   and   CAPE · J/kg ÷ 30", w.Time, t0, new[] { w.Pop, w.Cape.Select(v => v / 30).ToArray() }, new[] { th.Info, th.Bad }, new[] { "rain", "CAPE" }, 100);

            // upper winds
            var up = Card(new RectangleF(r.Right - upperW, r.Y, upperW, r.Height), "Winds aloft");
            string aloftSrc; var lv2 = Winds.For(l, out aloftSrc);
            if (lv2.Count > 0)
            {
                if (lv2.Count > 22) lv2 = Thin(lv2, 22);
                float y = up.Y, lh = Math.Min(44 * u, (up.Height - 30 * u) / (lv2.Count + 1));
                float[] cols = { 0, 0.2f, 0.42f, 0.62f, 0.82f }; string[] headings = { "hPa", "height", "wind", "from", "temp" };
                for (int i = 0; i < 5; i++) Text(Up(headings[i]), up.X + cols[i] * up.Width, y, 12, th.Muted, true);
                y += lh * 0.8f;
                double max = Math.Max(60, lv2.Max(p => double.IsNaN(p.Kt) ? 0 : p.Kt));
                foreach (var p in Enumerable.Reverse(lv2))
                {
                    var col = p.Kt >= 100 ? th.Bad : p.Kt >= 70 ? th.Warn : th.Ink;
                    using (var b = new SolidBrush(Color.FromArgb(46, col))) g.FillRectangle(b, up.X, y + lh * 0.72f, (float)(up.Width * Math.Min(1, p.Kt / max)), 4 * u);
                    Text(p.P.ToString("0"), up.X, y, 16, th.Ink2, false, true);
                    Text((p.Z / 1000).ToString("0.0") + " km", up.X + cols[1] * up.Width, y, 16, th.Ink2, false, true);
                    Text(Num(p.Kt) + " kt", up.X + cols[2] * up.Width, y, 16, col, true, true);
                    Text(Num(p.Dir, "000") + "° " + (double.IsNaN(p.Dir) ? "" : Compass(p.Dir)), up.X + cols[3] * up.Width, y, 16, th.Ink2, false, true);
                    Text(Num(p.T) + " °C", up.X + cols[4] * up.Width, y, 16, th.Ink2, false, true);
                    y += lh;
                }
                Text(aloftSrc, up.X, up.Bottom - 16 * u, 11, th.Muted, false, false, -1, up.Width);
            }
        }

        private void Chart(RectangleF r, string title, double[] time, double t0, double[][] series, Color[] colors, string[] names, double fixedMax)
        {
            var c = Card(r, title);
            float lx = c.Right;
            for (int i = names.Length - 1; i >= 0; i--) { lx -= Text(names[i], lx, r.Y + 14 * u, 12, colors[i], true, false, 1) + 14 * u; }
            double a = Store.Unix(now) - 3 * 3600, b = a + 48 * 3600, max = fixedMax;
            if (max <= 0) { foreach (var s in series) for (int i = 0; i < s.Length && i < time.Length; i++) if (time[i] >= a && time[i] <= b && !double.IsNaN(s[i])) max = Math.Max(max, s[i]); max = Math.Max(10, Math.Ceiling(max / 10) * 10); }
            var p = new RectangleF(c.X + 34 * u, c.Y + 4 * u, c.Width - 40 * u, c.Height - 26 * u);
            Func<double, float> px = t => p.X + (float)((t - a) / (b - a)) * p.Width; Func<double, float> py = v => p.Bottom - (float)(Math.Min(max, v) / max) * p.Height;
            for (int i = 0; i <= 2; i++) { float y = p.Bottom - p.Height * i / 2; Line(th.Line, 1, p.X, y, p.Right, y); Text((max * i / 2).ToString("0"), p.X - 6 * u, y - 8 * u, 11, th.Muted, false, true, 1); }
            for (double t = Math.Ceiling(a / 21600) * 21600; t <= b; t += 21600)
            {
                var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t);
                Line(Color.FromArgb(d.Hour == 0 ? 120 : 50, th.Line), 1, px(t), p.Y, px(t), p.Bottom);
                Text(d.Hour == 0 ? d.ToString("ddd") : d.ToString("HH") + "Z", px(t), p.Bottom + 4 * u, 11, th.Muted, d.Hour == 0, true, 0);
            }
            for (int k = 0; k < series.Length; k++)
            {
                var pts = new List<PointF>();
                for (int i = 0; i < series[k].Length && i < time.Length; i++) if (time[i] >= a && time[i] <= b && !double.IsNaN(series[k][i])) pts.Add(new PointF(px(time[i]), py(series[k][i])));
                if (pts.Count > 1) using (var pen = new Pen(colors[k], 2.2f * u) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());
            }
            Line(th.Muted, 1, px(Store.Unix(now)), p.Y, px(Store.Unix(now)), p.Bottom);
            if (!double.IsNaN(t0) && t0 >= a && t0 <= b) { Line(th.Accent, 2, px(t0), p.Y, px(t0), p.Bottom); Text("T-0", px(t0) + 4 * u, p.Y, 11, th.Accent, true); }
        }

        // ---------------------------------------------------------------- mission and schedule

        private void Schedule(RectangleF r)
        {
            var c = Card(r, "Upcoming launches · selected sites");
            var all = Store.Launches.Where(l => l.Net.HasValue && l.Net.Value > now.AddMinutes(-20)).ToList();
            float rowH = Math.Min(74 * u, c.Height / Math.Max(1, Math.Min(all.Count, 11))), y = c.Y; int n = (int)(c.Height / rowH);
            float[] x = { 0, 0.15f, 0.48f, 0.66f, 0.89f };
            for (int i = 0; i < Math.Min(n, all.Count); i++)
            {
                var l = all[i];
                if (i == 0) using (var b = new SolidBrush(Color.FromArgb(30, th.Accent))) g.FillRectangle(b, c.X - 8 * u, y - 4 * u, c.Width + 16 * u, rowH - 6 * u);
                Text(Clock(l, now, false), c.X, y, 20, i == 0 ? th.Accent : th.Ink, true, true);
                Text(l.Net.Value.ToString("ddd d MMM HH:mm") + " UTC" + (l.Precise ? "" : " · unconfirmed"), c.X, y + 28 * u, 13, th.Muted);
                Text(l.Name, c.X + x[1] * c.Width, y, 20, th.Ink, true, false, -1, (x[2] - x[1]) * c.Width - 20 * u);
                Text(l.MissionType ?? "", c.X + x[1] * c.Width, y + 28 * u, 13, th.Muted, false, false, -1, (x[2] - x[1]) * c.Width - 20 * u);
                Text(l.Provider, c.X + x[2] * c.Width, y, 17, th.Ink2, false, false, -1, (x[3] - x[2]) * c.Width - 20 * u);
                Text(l.PadName, c.X + x[3] * c.Width, y, 17, th.Ink2, false, false, -1, (x[4] - x[3]) * c.Width - 20 * u);
                Text(l.Location, c.X + x[3] * c.Width, y + 26 * u, 13, th.Muted, false, false, -1, (x[4] - x[3]) * c.Width - 20 * u);
                Pill(Up(l.Status), c.Right, y, 13, StatusColor(l), 1);
                y += rowH;
            }
            if (all.Count == 0) Text("no upcoming launches at the selected sites", c.X, c.Y, 18, th.Muted);
        }

        // ---------------------------------------------------------------- terminal count

        /// <summary>From T-1 hour: count top centre, corridor left, live video right with the timeline under it.</summary>
        private void Terminal(Launch l, RectangleF r)
        {
            float gap = 16 * u, topH = 190 * u;
            var top = new RectangleF(r.X, r.Y, r.Width, topH); Card(top, null);
            Out.Clock = new RectangleF(top.X + top.Width * 0.2f, top.Y + 40 * u, top.Width * 0.6f, 120 * u); Out.ClockSize = 104;
            Text(l.Name, top.X + top.Width / 2, top.Y + 10 * u, 22, th.Ink, true, false, 0, top.Width * 0.6f);
            Text(Up("Terminal count"), top.X + 20 * u, top.Y + 16 * u, 15, th.Accent, true);
            Text(l.Rocket, top.X + 20 * u, top.Y + 44 * u, 19, th.Ink, true, false, -1, top.Width * 0.19f);
            Text(l.PadName, top.X + 20 * u, top.Y + 74 * u, 15, th.Ink2, false, false, -1, top.Width * 0.19f);
            if (l.Net.HasValue) Text(PadTime(l, l.Net.Value, "HH:mm:ss"), top.X + 20 * u, top.Y + 100 * u, 15, th.Ink2, false, false, -1, top.Width * 0.19f);
            Pill(Up(l.StatusName.Length > 0 ? l.StatusName : l.Status), top.Right - 20 * u, top.Y + 16 * u, 15, StatusColor(l), 1);
            Text(now.ToString("HH:mm:ss") + " UTC", top.Right - 20 * u, top.Y + 60 * u, 22, th.Ink2, false, true, 1);
            var w = Store.Weather; int hr = w == null ? -1 : w.Hour(now);
            if (hr >= 0) Text("wind " + Num(w.Wind[hr]) + " kt " + Compass(w.Dir[hr]) + " · gust " + Num(w.Gust[hr]), top.Right - 20 * u, top.Y + 100 * u, 15, th.Ink2, false, false, 1);
            if (l.Prob.HasValue) Text("weather " + l.Prob.Value.ToString("0") + "% GO", top.Right - 20 * u, top.Y + 126 * u, 15, l.Prob >= 70 ? th.Good : th.Warn, true, false, 1);

            float y = top.Bottom + gap, h = r.Bottom - y, lw = (r.Width - gap) * 0.5f;
            var left = new RectangleF(r.X, y, lw, h);
            if (View3DAvailable) Out.View3D = left; else MapPanel(l, left, 200);
            float rx = left.Right + gap, rw = r.Right - rx, vh = Math.Min(h * 0.6f, rw * 9 / 16);
            var video = new RectangleF(rx, y, rw, vh);
            if (VideoAvailable) Out.Video = video; else MapPanel(l, video, 150);
            Timeline(l, new RectangleF(rx, video.Bottom + gap, rw, h - vh - gap), false);
        }

        /// <summary>The running clock, drawn every frame on top of the cached picture.</summary>
        public static void DrawClock(Graphics g, Layout o, float u, DateTime at)
        {
            if (o.Clock.Width < 2) return;
            var th = Theme.Cur; var l = Store.Current; if (l == null) return;
            string s = Clock(l, at);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var f = new Font(th.Mono, Math.Max(4, o.ClockSize * u), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces })
            using (var b = new SolidBrush(l.Status == "Hold" ? th.Warn : th.Ink))
            {
                // measure a fixed-width sample so the digits do not shuffle sideways as they change
                float w = g.MeasureString(new string('0', s.Length), f, 100000, fmt).Width, h = f.GetHeight(g);
                g.DrawString(s, f, b, o.Clock.X + (o.Clock.Width - w) / 2, o.Clock.Y + (o.Clock.Height - h) / 2, fmt);
            }
        }
    }
}

// The rest of the slides: live video, commit criteria, hourly, Weather Service, upper air,
// sea state and space weather, mission, vehicle, updates and the full data listing.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LaunchCountdownSaver
{
    internal sealed partial class Painter
    {
        private void Waiting(RectangleF r, string title, string text)
        {
            Card(r, title); Text(text, r.X + r.Width / 2, r.Y + r.Height / 2, 20, th.Muted, false, false, 0, r.Width - 60 * u);
        }

        // ---------------------------------------------------------------- live video

        private void Live(Launch l, RectangleF r)
        {
            float side = 430 * u;
            var v = new RectangleF(r.X, r.Y, r.Width - side - 16 * u, r.Height);
            if (VideoAvailable) { Out.Video = v; using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, v); }
            else Waiting(v, "Live video", "live video needs the Microsoft Edge WebView2 runtime, which was not found");
            var c = Card(new RectangleF(v.Right + 16 * u, r.Y, side, r.Height), "On screen");
            double t = l.Net.HasValue ? (now - l.Net.Value).TotalMinutes : double.NaN;
            bool cast = l.Video != null && t > -120 && t < 60;
            float y = c.Y;
            y += Fact(c.X, y, c.Width, "Stream", cast ? "Launch webcast" : "Pad and weather stream", cast ? "listed for this launch" : "plays muted");
            y += Fact(c.X, y, c.Width, "Webcast", l.Video != null ? "listed" : "none listed yet", l.Video != null ? "shown from T-2 hours to T+1 hour" : "the pad stream is shown instead");
            var w = Store.Weather; int hr = w == null ? -1 : w.Hour(now);
            if (hr >= 0)
            {
                y += Fact(c.X, y, c.Width, "Wind now", Num(w.Wind[hr]) + " kt " + Compass(w.Dir[hr]), "gusting " + Num(w.Gust[hr]) + " kt");
                y += Fact(c.X, y, c.Width, "Cloud now", Num(Math.Max(w.Low[hr], Math.Max(w.Mid[hr], w.High[hr]))) + "%", "low " + Num(w.Low[hr]) + " · mid " + Num(w.Mid[hr]) + " · high " + Num(w.High[hr]));
            }
        }

        // ---------------------------------------------------------------- commit criteria

        private struct Rule { public string Name, Value, Limit; public int Level; }

        private void Criteria(Launch l, RectangleF r)
        {
            var w = Store.Weather;
            if (w == null || w.Lat != l.Lat) { Waiting(r, "Launch commit criteria", "weather loading…"); return; }
            int hT0 = l.Net.HasValue ? w.Hour(l.Net.Value) : -1, hr = hT0 >= 0 ? hT0 : w.Hour(now);
            if (hr < 0) { Waiting(r, "Launch commit criteria", "no forecast for this time"); return; }
            string windSrc; var prof = Winds.For(l, out windSrc);
            double jet = prof.Where(p => p.P <= 300 && p.P >= 150 && !double.IsNaN(p.Kt)).Select(p => p.Kt).DefaultIfEmpty(double.NaN).Max();
            double shear = 0; // strongest change of wind between neighbouring levels, per kilometre
            for (int i = 1; i < prof.Count; i++)
            {
                double u1 = -prof[i - 1].Kt * Math.Sin(prof[i - 1].Dir * Math.PI / 180), v1 = -prof[i - 1].Kt * Math.Cos(prof[i - 1].Dir * Math.PI / 180);
                double u2 = -prof[i].Kt * Math.Sin(prof[i].Dir * Math.PI / 180), v2 = -prof[i].Kt * Math.Cos(prof[i].Dir * Math.PI / 180), dz = (prof[i].Z - prof[i - 1].Z) / 1000;
                if (dz > 0.1 && !double.IsNaN(u1 + u2)) shear = Math.Max(shear, Math.Sqrt((u2 - u1) * (u2 - u1) + (v2 - v1) * (v2 - v1)) / dz);
            }
            double freezing = double.NaN; // height where the profile crosses 0 °C
            for (int i = 1; i < prof.Count; i++) if (prof[i - 1].T > 0 && prof[i].T <= 0) { freezing = prof[i - 1].Z + (prof[i].Z - prof[i - 1].Z) * prof[i - 1].T / (prof[i - 1].T - prof[i].T); break; }
            double thick = Math.Max(w.Low[hr], w.Mid[hr]);
            var sv = Store.Service; var sea = Store.Ocean; var sun = Store.Sun;
            int storm = sv == null ? 0 : sv.Alerts.Count(a => (a.Event ?? "").IndexOf("Thunderstorm", StringComparison.OrdinalIgnoreCase) >= 0 || (a.Event ?? "").IndexOf("Tornado", StringComparison.OrdinalIgnoreCase) >= 0);
            Func<double, double, double, int> lv = (v, watch, stop) => double.IsNaN(v) ? -1 : v >= stop ? 2 : v >= watch ? 1 : 0;
            var rules = new List<Rule>
            {
                new Rule { Name = "Surface wind", Value = Num(w.Wind[hr]) + " kt from " + Num(w.Dir[hr], "000") + "°", Limit = "watch 22 kt · no-go 30 kt", Level = lv(w.Wind[hr], 22, 30) },
                new Rule { Name = "Surface gusts", Value = Num(w.Gust[hr]) + " kt", Limit = "watch 28 kt · no-go 35 kt", Level = lv(w.Gust[hr], 28, 35) },
                new Rule { Name = "Upper-level wind (300–150 hPa)", Value = Num(jet) + " kt · " + windSrc, Limit = "watch 100 kt · no-go 130 kt", Level = lv(jet, 100, 130) },
                new Rule { Name = "Wind shear aloft", Value = Num(shear, "0.0") + " kt per km", Limit = "watch 15 · no-go 25 kt per km", Level = lv(shear, 15, 25) },
                new Rule { Name = "Precipitation at the pad", Value = Num(w.Pop[hr]) + "% chance · " + Num(w.Rain[hr], "0.0") + " mm/h", Limit = "watch 40% or 0.1 mm · no-go 0.5 mm/h", Level = Math.Max(lv(w.Rain[hr], 0.1, 0.5), lv(w.Pop[hr], 40, 101)) },
                new Rule { Name = "Lightning potential (CAPE)", Value = Num(w.Cape[hr]) + " J/kg", Limit = "watch 1000 · no-go 2000 J/kg", Level = lv(w.Cape[hr], 1000, 2000) },
                new Rule { Name = "Thick cloud layer", Value = "low " + Num(w.Low[hr]) + "% · mid " + Num(w.Mid[hr]) + "%", Limit = "watch when a layer is 90% or more", Level = lv(thick, 90, 101) },
                new Rule { Name = "Cloud through the freezing level", Value = double.IsNaN(freezing) ? "—" : "0 °C at " + (freezing / 1000).ToString("0.0") + " km · mid cloud " + Num(w.Mid[hr]) + "%", Limit = "watch when mid cloud is 70% or more", Level = double.IsNaN(freezing) ? -1 : lv(w.Mid[hr], 70, 101) },
                new Rule { Name = "Visibility", Value = Num(w.Vis[hr] / 1609.34, "0.#") + " mi", Limit = "watch below 4 mi", Level = double.IsNaN(w.Vis[hr]) ? -1 : w.Vis[hr] < 6437 ? 1 : 0 },
                new Rule { Name = "Temperature", Value = Num(CtoF(w.Temp[hr])) + " °F", Limit = "watch below 40 °F or above 99 °F", Level = double.IsNaN(w.Temp[hr]) ? -1 : (CtoF(w.Temp[hr]) < 40 || CtoF(w.Temp[hr]) > 99 ? 1 : 0) },
                new Rule { Name = "Weather Service storm warnings", Value = sv == null ? "—" : !sv.Covered ? "outside NWS coverage" : storm + " active of " + sv.Alerts.Count + " alerts", Limit = "no-go with a thunderstorm or tornado warning", Level = sv == null || !sv.Covered ? -1 : storm > 0 ? 2 : sv.Alerts.Count > 0 ? 1 : 0 },
                new Rule { Name = "Sea state downrange", Value = sea == null || double.IsNaN(sea.Wave) ? "—" : sea.Wave.ToString("0.0") + " m waves", Limit = "watch 2.5 m (recovery)", Level = sea == null ? -1 : lv(sea.Wave, 2.5, 99) },
                new Rule { Name = "Space weather", Value = sun == null ? "—" : sun.R + " · " + sun.S + " · " + sun.G + " · Kp " + Num(sun.Kp, "0.0"), Limit = "watch at S2 or Kp 6", Level = sun == null ? -1 : (sun.S.CompareTo("S2") >= 0 && sun.S != "—") || sun.Kp >= 6 ? 1 : 0 },
            };
            int worst = rules.Max(x => x.Level);
            var c = Card(r, "Launch commit criteria · " + (hT0 >= 0 ? "forecast at T-0" : "conditions now — T-0 is beyond the forecast") + " · indicative thresholds, not the range's official rules");
            Pill(worst == 2 ? "NO-GO ON THESE THRESHOLDS" : worst == 1 ? "WATCH" : "ALL WITHIN LIMITS", c.Right, r.Y + 10 * u, 14, worst == 2 ? th.Bad : worst == 1 ? th.Warn : th.Good, 1);
            if (l.Prob.HasValue) Text("range forecast: " + l.Prob.Value.ToString("0") + "% GO" + (l.Concerns != null ? " · " + l.Concerns : ""), c.X, c.Y, 17, th.Ink2);
            float y = c.Y + (l.Prob.HasValue ? 38 * u : 0), lh = Math.Min(62 * u, (c.Bottom - y) / rules.Count);
            foreach (var x in rules)
            {
                var col = x.Level == 2 ? th.Bad : x.Level == 1 ? th.Warn : x.Level == 0 ? th.Good : th.Muted;
                Pill(x.Level == 2 ? "NO-GO" : x.Level == 1 ? "WATCH" : x.Level == 0 ? "GO" : "N/A", c.X, y + 4 * u, 14, col);
                Text(x.Name, c.X + 110 * u, y + 4 * u, 20, th.Ink, true, false, -1, c.Width * 0.36f);
                Text(x.Value, c.X + c.Width * 0.44f, y + 4 * u, 20, x.Level > 0 ? col : th.Ink, false, true, -1, c.Width * 0.3f);
                Text(x.Limit, c.Right, y + 8 * u, 15, th.Muted, false, false, 1, c.Width * 0.24f);
                Line(Color.FromArgb(90, th.Line), 1, c.X, y + lh - 6 * u, c.Right, y + lh - 6 * u);
                y += lh;
            }
        }

        // ---------------------------------------------------------------- hourly

        private void Hourly(Launch l, RectangleF r)
        {
            var w = Store.Weather;
            if (w == null || w.Lat != l.Lat) { Waiting(r, "Hourly forecast", "weather loading…"); return; }
            var c = Card(r, "Hourly forecast at " + l.PadName + " · Open-Meteo");
            int first = Math.Max(0, w.Hour(now)), hT0 = l.Net.HasValue ? w.Hour(l.Net.Value) : -1;
            float head = 30 * u, rowH = Math.Min(36 * u, (c.Height - head) / 24); int rows = Math.Min(w.Time.Length - first, (int)((c.Height - head) / rowH));
            string[] cols = { "Time (UTC)", "Pad time", "Temp", "Dew pt", "Wind", "Gust", "From", "Low", "Mid", "High", "Rain %", "mm", "CAPE", "Vis mi" };
            float[] x = { 0, 0.12f, 0.24f, 0.31f, 0.38f, 0.45f, 0.52f, 0.60f, 0.66f, 0.72f, 0.78f, 0.84f, 0.89f, 0.95f };
            for (int i = 0; i < cols.Length; i++) Text(Up(cols[i]), c.X + x[i] * c.Width, c.Y, 12, th.Muted, true);
            for (int n = 0; n < rows; n++)
            {
                int i = first + n; float y = c.Y + head + n * rowH; var t = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(w.Time[i]);
                if (i == hT0) { using (var b = new SolidBrush(Color.FromArgb(46, th.Accent))) g.FillRectangle(b, c.X - 8 * u, y - 2 * u, c.Width + 16 * u, rowH); Text("T-0", c.Right, y, 13, th.Accent, true, false, 1); }
                Func<double, double, double, Color> col = (val, watch, stop) => val >= stop ? th.Bad : val >= watch ? th.Warn : th.Ink;
                string pad = PadTime(l, t, "ddd HH:mm"); pad = pad.Substring(0, pad.IndexOf(" UTC") > 0 ? pad.IndexOf(" UTC") : Math.Min(pad.Length, 9));
                string[] v = { t.ToString("ddd HH:mm"), pad, Num(CtoF(w.Temp[i])) + "°F", Num(CtoF(w.Dew[i])) + "°F", Num(w.Wind[i]) + " kt", Num(w.Gust[i]) + " kt", Num(w.Dir[i], "000") + "° " + Compass(w.Dir[i]),
                    Num(w.Low[i]) + "%", Num(w.Mid[i]) + "%", Num(w.High[i]) + "%", Num(w.Pop[i]) + "%", Num(w.Rain[i], "0.0"), Num(w.Cape[i]), Num(w.Vis[i] / 1609.34, "0") };
                Color[] k = { th.Ink2, th.Ink2, th.Ink, th.Ink2, col(w.Wind[i], 22, 30), col(w.Gust[i], 28, 35), th.Ink2, th.Ink2, th.Ink2, th.Ink2, col(w.Pop[i], 40, 70), col(w.Rain[i], 0.1, 0.5), col(w.Cape[i], 1000, 2000), th.Ink2 };
                for (int j = 0; j < v.Length; j++) Text(v[j], c.X + x[j] * c.Width, y, 16, k[j], false, true);
            }
        }

        // ---------------------------------------------------------------- National Weather Service

        private void Alerts(Launch l, RectangleF r)
        {
            var s = Store.Service;
            if (s == null || s.Lat != l.Lat) { Waiting(r, "National Weather Service", "loading…"); return; }
            if (!s.Covered) { Waiting(r, "National Weather Service", "this launch site is outside the United States, which the Weather Service does not cover — see the Forecast and Hourly slides"); return; }
            float gap = 16 * u, lw = r.Width * 0.36f;
            var a = Card(new RectangleF(r.X, r.Y, lw, r.Height * 0.5f), "Active alerts at the pad" + (s.Office != null ? " · office " + s.Office : ""));
            float y = a.Y;
            if (s.Alerts.Count == 0) { Pill("NONE", a.X, y, 14, th.Good); Text("no watches, warnings or advisories", a.X + 80 * u, y + 2 * u, 17, th.Ink2); }
            foreach (var al in s.Alerts)
            {
                if (y > a.Bottom - 70 * u) break;
                var col = al.Severity == "Extreme" || al.Severity == "Severe" ? th.Bad : th.Warn;
                Text(al.Event, a.X, y, 20, col, true, false, -1, a.Width); y += 30 * u;
                y += Wrapped(al.Headline, new RectangleF(a.X, y, a.Width, 70 * u), 14, th.Ink2) + 14 * u;
            }
            var o = Card(new RectangleF(r.X, r.Y + r.Height * 0.5f + gap, lw, r.Height * 0.5f - gap), "Latest observation" + (s.Station != null ? " · " + s.Station : ""));
            if (s.Station == null) Text("no observation available", o.X, o.Y, 17, th.Muted);
            else
            {
                float half = o.Width / 2; y = o.Y;
                Fact(o.X, y, half, "Sky", s.Sky ?? "—", s.ObsAt.HasValue ? "observed " + s.ObsAt.Value.ToString("HH:mm") + " UTC" : null);
                Fact(o.X + half, y, half, "Temperature", Num(CtoF(s.TempC)) + " °F", "dew point " + Num(CtoF(s.DewC)) + " °F"); y += 92 * u;
                Fact(o.X, y, half, "Wind", Num(s.WindKt) + " kt", double.IsNaN(s.WindDir) ? "variable" : "from " + Num(s.WindDir, "000") + "° " + Compass(s.WindDir));
                Fact(o.X + half, y, half, "Gusts", double.IsNaN(s.GustKt) ? "none" : Num(s.GustKt) + " kt"); y += 92 * u;
                Fact(o.X, y, half, "Visibility", Num(s.VisKm / 1.609, "0.#") + " mi"); Fact(o.X + half, y, half, "Pressure", Num(s.Hpa, "0.0") + " hPa");
            }
            var f = Card(new RectangleF(r.X + lw + gap, r.Y, r.Width - lw - gap, r.Height), "Forecast");
            y = f.Y;
            foreach (var p in s.Periods)
            {
                if (y > f.Bottom - 80 * u) break;
                Text(p.Name, f.X, y, 18, th.Ink, true); Text(Num(p.Temp) + " °F", f.X + 230 * u, y, 18, p.Day ? th.Warn : th.Info, true, true); Text(p.Wind, f.X + 330 * u, y + 2 * u, 15, th.Ink2);
                Text(p.Short, f.X + 520 * u, y + 2 * u, 15, th.Ink2, false, false, -1, f.Width - 520 * u); y += 28 * u;
                y += Wrapped(p.Detail, new RectangleF(f.X, y, f.Width, 64 * u), 14, th.Muted) + 14 * u;
            }
        }

        // ---------------------------------------------------------------- upper air

        private void Upper(Launch l, RectangleF r)
        {
            var w = Store.Weather;
            if (w == null || w.Lat != l.Lat) { Waiting(r, "Upper air", "weather loading…"); return; }
            int hT0 = l.Net.HasValue ? w.Hour(l.Net.Value) : -1, hr = hT0 >= 0 ? hT0 : w.Hour(now); string src; var lv = Winds.For(l, out src);
            if (lv.Count < 3) { Waiting(r, "Upper air", "no sounding for this time"); return; }
            lv = lv.Where(q => q.P >= 95).ToList(); // the charts stop at 100 hPa
            string when = "· " + src;
            float gap = 16 * u, sw = r.Width * 0.42f, hw = r.Width * 0.27f;

            // skew-T log-P
            var c = Card(new RectangleF(r.X, r.Y, sw, r.Height), "Sounding (skew-T) " + when);
            var p = new RectangleF(c.X + 50 * u, c.Y + 6 * u, c.Width - 60 * u, c.Height - 40 * u);
            const double pTop = 100, pBot = 1050, tMin = -40, tMax = 40;
            Func<double, float> py = pr => p.Y + (float)(Math.Log(pr / pTop) / Math.Log(pBot / pTop)) * p.Height;
            Func<double, double, float> px = (t, pr) => p.X + (float)((t - tMin) / (tMax - tMin)) * p.Width + (p.Bottom - py(pr)) * 0.75f; // isotherms lean to the right
            var state = g.Save(); g.SetClip(p);
            for (int t = -120; t <= 40; t += 10) Line(t == 0 ? Color.FromArgb(180, th.Info) : th.Line, 1, px(t, pBot), py(pBot), px(t, pTop), py(pTop));
            foreach (int pr in new[] { 1000, 850, 700, 500, 400, 300, 250, 200, 150, 100 }) Line(th.Line, 1, p.X, py(pr), p.Right, py(pr));
            Func<double, double, double> dew = (t, rh) => { double a = Math.Log(Math.Max(1, rh) / 100) + 17.62 * t / (243.12 + t); return 243.12 * a / (17.62 - a); };
            var tp = lv.Select(q => new PointF(px(q.T, q.P), py(q.P))).ToArray();
            var dp = lv.Where(q => !double.IsNaN(q.Rh)).Select(q => new PointF(px(dew(q.T, q.Rh), q.P), py(q.P))).ToArray();
            using (var pen = new Pen(th.Bad, 3 * u) { LineJoin = LineJoin.Round }) g.DrawLines(pen, tp);
            if (dp.Length > 1) using (var pen = new Pen(th.Good, 3 * u) { LineJoin = LineJoin.Round }) g.DrawLines(pen, dp);
            g.Restore(state);
            foreach (int pr in new[] { 1000, 850, 700, 500, 400, 300, 250, 200, 150, 100 }) Text(pr.ToString(), p.X - 6 * u, py(pr) - 8 * u, 12, th.Muted, false, true, 1);
            for (int t = -40; t <= 40; t += 20) Text(t + "°C", px(t, pBot), p.Bottom + 6 * u, 12, th.Muted, false, true, 0);
            Text("temperature", p.Right, p.Y + 4 * u, 13, th.Bad, true, false, 1); Text("dew point", p.Right, p.Y + 24 * u, 13, th.Good, true, false, 1);
            // wind barbs down the right-hand side, as arrows
            foreach (var q in Thin(lv, 26))
            {
                if (double.IsNaN(q.Kt)) continue;
                float y = py(q.P), x = p.Right - 150 * u, len = (float)Math.Min(60, 14 + q.Kt * 0.5) * u;
                float dx = -(float)Math.Sin(q.Dir * Math.PI / 180) * len, dy = (float)Math.Cos(q.Dir * Math.PI / 180) * len;
                using (var pen = new Pen(q.Kt >= 100 ? th.Bad : q.Kt >= 70 ? th.Warn : th.Ink2, 2 * u) { EndCap = LineCap.ArrowAnchor }) g.DrawLine(pen, x - dx / 2, y - dy / 2, x + dx / 2, y + dy / 2);
            }

            // hodograph
            var h = Card(new RectangleF(r.X + sw + gap, r.Y, hw, r.Height * 0.56f), "Hodograph · kt");
            float cx = h.X + h.Width / 2, cy = h.Y + h.Height / 2, rad = Math.Min(h.Width, h.Height) / 2 - 10 * u;
            double max = Math.Max(40, Math.Ceiling(lv.Max(q => double.IsNaN(q.Kt) ? 0 : q.Kt) / 20) * 20);
            for (double ring = 20; ring <= max; ring += max > 100 ? 40 : 20)
            {
                float rr = (float)(ring / max) * rad;
                using (var pen = new Pen(th.Line, Math.Max(1, u))) g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                Text(ring.ToString("0"), cx + 3 * u, cy - rr - 2 * u, 11, th.Muted, false, true);
            }
            Line(th.Line, 1, cx - rad, cy, cx + rad, cy); Line(th.Line, 1, cx, cy - rad, cx, cy + rad);
            var hp = lv.Where(q => !double.IsNaN(q.Kt)).Select(q => new PointF(cx - (float)(Math.Sin(q.Dir * Math.PI / 180) * q.Kt / max) * rad, cy + (float)(Math.Cos(q.Dir * Math.PI / 180) * q.Kt / max) * rad)).ToArray();
            for (int i = 1; i < hp.Length; i++)
                using (var pen = new Pen(lv[i].Z < 3000 ? th.Good : lv[i].Z < 9000 ? th.Warn : th.Bad, 3 * u)) g.DrawLine(pen, hp[i - 1], hp[i]);
            Text("surface–3 km", h.X, h.Bottom - 14 * u, 12, th.Good, true); Text("3–9 km", h.X + 110 * u, h.Bottom - 14 * u, 12, th.Warn, true); Text("above 9 km", h.X + 180 * u, h.Bottom - 14 * u, 12, th.Bad, true);

            // stability numbers
            var s = Card(new RectangleF(r.X + sw + gap, r.Y + r.Height * 0.56f + gap, hw, r.Height * 0.44f - gap), "Indices and sources · U changes the source");
            double freezing = double.NaN, trop = lv.OrderBy(q => q.T).First().Z;
            for (int i = 1; i < lv.Count; i++) if (lv[i - 1].T > 0 && lv[i].T <= 0) { freezing = lv[i - 1].Z + (lv[i].Z - lv[i - 1].Z) * lv[i - 1].T / (lv[i - 1].T - lv[i].T); break; }
            var jet = lv.Where(q => !double.IsNaN(q.Kt)).OrderByDescending(q => q.Kt).First();
            float half = s.Width / 2, sy = s.Y;
            Fact(s.X, sy, half, "CAPE", Num(w.Cape[hr]) + " J/kg"); Fact(s.X + half, sy, half, "Freezing level", double.IsNaN(freezing) ? "—" : (freezing / 1000).ToString("0.0") + " km", double.IsNaN(freezing) ? null : (freezing * 3.28084).ToString("#,0") + " ft"); sy += 92 * u;
            Fact(s.X, sy, half, "Strongest wind", Num(jet.Kt) + " kt", "at " + (jet.Z / 1000).ToString("0.0") + " km, " + jet.P.ToString("0") + " hPa");
            Fact(s.X + half, sy, half, "Coldest level", (trop / 1000).ToString("0.0") + " km", Num(lv.Min(q => q.T)) + " °C"); sy += 96 * u;
            // what each source has right now, and which one is in use
            var bal = Store.Balloon; var pro = Store.Profiler;
            Action<string, string, bool> line = (k, v, ok) => { Text(Up(k), s.X, sy + 2 * u, 12, th.Muted, true); Text(v, s.X + 150 * u, sy, 14, ok ? th.Ink : th.Muted, false, false, -1, s.Width - 150 * u); sy += 24 * u; };
            line("In use", Settings.UpperSource == "auto" ? "automatic: measurements near launch" : Winds.SourceName(Settings.UpperSource), true);
            line("Balloon", bal != null ? bal.Station + " " + bal.At.ToString("ddd HH:mm") + " UTC · to " + (bal.Levels.Max(q => q.Z) / 1000).ToString("0") + " km" : Store.BalloonNote, bal != null);
            line("Radar profile", pro != null ? pro.Station + " " + pro.At.ToString("HH:mm") + " UTC · to " + (pro.Levels.Max(q => q.Z) / 1000).ToString("0.0") + " km" : Store.ProfilerNote, pro != null);
            line("TDRWP", "no public live feed (NASA's archive runs months behind)", false);
            line("Model", "Open-Meteo, hourly, to 16 km", true);

            // table
            var tb = Card(new RectangleF(r.X + sw + hw + 2 * gap, r.Y, r.Width - sw - hw - 2 * gap, r.Height), "Levels");
            var full = lv; lv = Thin(lv, 20);
            float ty = tb.Y, lh = Math.Min(48 * u, (tb.Height - 30 * u) / (lv.Count + 1));
            float[] cols = { 0, 0.16f, 0.36f, 0.56f, 0.76f, 0.9f }; string[] heads = { "hPa", "height", "wind", "from", "temp", "RH" };
            for (int i = 0; i < heads.Length; i++) Text(Up(heads[i]), tb.X + cols[i] * tb.Width, ty, 12, th.Muted, true);
            ty += lh * 0.8f;
            foreach (var q in Enumerable.Reverse(lv))
            {
                var col = q.Kt >= 100 ? th.Bad : q.Kt >= 70 ? th.Warn : th.Ink;
                Text(q.P.ToString("0"), tb.X, ty, 16, th.Ink2, false, true); Text((q.Z / 1000).ToString("0.0") + " km", tb.X + cols[1] * tb.Width, ty, 16, th.Ink2, false, true);
                Text(Num(q.Kt) + " kt", tb.X + cols[2] * tb.Width, ty, 16, col, true, true); Text(Num(q.Dir, "000") + "° " + (double.IsNaN(q.Dir) ? "" : Compass(q.Dir)), tb.X + cols[3] * tb.Width, ty, 16, th.Ink2, false, true);
                Text(Num(q.T) + "°C", tb.X + cols[4] * tb.Width, ty, 16, th.Ink2, false, true); Text(Num(q.Rh) + "%", tb.X + cols[5] * tb.Width, ty, 16, th.Ink2, false, true);
                ty += lh;
            }
        }

        // ---------------------------------------------------------------- sea state and space weather

        private void SeaSlide(Launch l, RectangleF r)
        {
            float gap = 16 * u, lw = (r.Width - gap) / 2;
            var sea = Store.Ocean; var left = new RectangleF(r.X, r.Y, lw, r.Height);
            if (sea == null || sea.Lat != l.Lat) Waiting(left, "Sea state", "loading…");
            else
            {
                var c = Card(new RectangleF(left.X, left.Y, lw, 230 * u), "Sea state 30 km downrange · Open-Meteo marine");
                float q = c.Width / 4;
                Fact(c.X, c.Y, q, "Waves", Num(sea.Wave, "0.0") + " m", Num(sea.Wave * 3.28084, "0.0") + " ft"); Fact(c.X + q, c.Y, q, "Wave period", Num(sea.WavePeriod, "0.0") + " s", double.IsNaN(sea.WaveDir) ? null : "from " + Compass(sea.WaveDir));
                Fact(c.X + 2 * q, c.Y, q, "Swell", Num(sea.Swell, "0.0") + " m", Num(sea.SwellPeriod, "0.0") + " s period"); Fact(c.X + 3 * q, c.Y, q, "Sea surface", Num(CtoF(sea.TempC)) + " °F", Num(sea.TempC, "0.0") + " °C");
                Chart(new RectangleF(left.X, c.Y + 230 * u, lw, (r.Height - 230 * u - 2 * gap) * 0.5f), "Waves and swell · metres ×10", sea.Time, l.Net.HasValue ? Store.Unix(l.Net.Value) : double.NaN,
                    new[] { sea.Waves.Select(v => v * 10).ToArray(), sea.Swells.Select(v => v * 10).ToArray() }, new[] { th.Info, th.Good }, new[] { "waves", "swell" }, 0);
                var t = Card(new RectangleF(left.X, left.Bottom - (r.Height - 230 * u - 2 * gap) * 0.5f, lw, (r.Height - 230 * u - 2 * gap) * 0.5f), "Tides" + (sea.TideStation != null ? " · " + sea.TideStation + " · NOAA" : ""));
                if (sea.Tides.Count == 0) Text("no NOAA tide station near this site", t.X, t.Y, 16, th.Muted);
                float tx = t.X, cw = t.Width / 4; int n = 0;
                foreach (var tide in sea.Tides.Where(x => x.At > now.AddHours(-1)).Take(8))
                {
                    float yy = t.Y + (n / 4) * 92 * u;
                    Fact(tx + (n % 4) * cw, yy, cw, tide.High ? "High" : "Low", tide.Ft.ToString("0.0") + " ft", tide.At.ToString("ddd HH:mm") + " UTC", tide.High ? th.Info : th.Ink2); n++;
                }
            }
            var sun = Store.Sun; var right = new RectangleF(r.X + lw + gap, r.Y, lw, r.Height);
            if (sun == null) { Waiting(right, "Space weather", "loading…"); return; }
            var s = Card(new RectangleF(right.X, right.Y, lw, 230 * u), "Space weather · NOAA SWPC");
            float w4 = s.Width / 4;
            Func<string, Color> sc = v => v.EndsWith("0") || v == "—" ? th.Good : v.CompareTo("X2") < 0 && (v.EndsWith("1") || v.EndsWith("2")) ? th.Warn : th.Bad;
            Fact(s.X, s.Y, w4, "Radio blackout", sun.R, "R scale", sc(sun.R)); Fact(s.X + w4, s.Y, w4, "Radiation storm", sun.S, "S scale", sc(sun.S));
            Fact(s.X + 2 * w4, s.Y, w4, "Geomagnetic storm", sun.G, "G scale", sc(sun.G)); Fact(s.X + 3 * w4, s.Y, w4, "Planetary Kp", Num(sun.Kp, "0.0"), sun.Kp >= 5 ? "storm level" : "quiet to unsettled", sun.Kp >= 5 ? th.Warn : th.Ink);
            Fact(s.X, s.Y + 92 * u, w4, "Solar wind", Num(sun.WindKms) + " km/s"); Fact(s.X + w4, s.Y + 92 * u, w4, "Field Bt / Bz", Num(sun.Bt, "0.0") + " / " + Num(sun.Bz, "0.0") + " nT");
            Fact(s.X + 2 * w4, s.Y + 92 * u, w4, "10.7 cm flux", Num(sun.Flux) + " sfu");
            var k = Card(new RectangleF(right.X, right.Y + 230 * u + gap, lw, r.Height - 230 * u - gap), "Planetary Kp index · last days");
            var hist = sun.KpHistory.Skip(Math.Max(0, sun.KpHistory.Count - 40)).ToList();
            if (hist.Count > 0)
            {
                float bw = k.Width / hist.Count, baseY = k.Bottom - 26 * u, hh = baseY - k.Y - 10 * u;
                for (int i = 0; i <= 9; i += 3) { float y = baseY - hh * i / 9; Line(th.Line, 1, k.X, y, k.Right, y); Text(i.ToString(), k.X - 2 * u, y - 16 * u, 11, th.Muted, false, true); }
                for (int i = 0; i < hist.Count; i++)
                {
                    double v = hist[i][1]; var col = v >= 5 ? th.Bad : v >= 4 ? th.Warn : th.Good;
                    using (var b = new SolidBrush(col)) g.FillRectangle(b, k.X + i * bw + 2 * u, baseY - (float)(v / 9) * hh, bw - 4 * u, (float)(v / 9) * hh);
                    var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(hist[i][0]);
                    if (d.Hour == 0) Text(d.ToString("ddd"), k.X + i * bw, baseY + 4 * u, 11, th.Muted);
                }
            }
        }

        // ---------------------------------------------------------------- mission, vehicle, updates

        private void Mission(Launch l, RectangleF r)
        {
            float gap = 16 * u, lw = r.Width * 0.6f;
            var c = Card(new RectangleF(r.X, r.Y, lw, r.Height), "Mission · " + (l.Mission ?? l.Name));
            Wrapped(l.Description ?? "No mission description has been published.", new RectangleF(c.X, c.Y, c.Width, c.Height), 24, th.Ink);
            var f = Card(new RectangleF(r.X + lw + gap, r.Y, r.Width - lw - gap, r.Height), "Facts");
            float y = f.Y, half = f.Width / 2;
            Fact(f.X, y, half, "Type", l.MissionType); Fact(f.X + half, y, half, "Orbit", l.OrbitName ?? l.Orbit); y += 92 * u;
            Fact(f.X, y, half, "Provider", l.Provider); Fact(f.X + half, y, half, "Status", l.StatusName); y += 92 * u;
            Fact(f.X, y, f.Width, "Pad", l.PadName, l.Location); y += 92 * u;
            Fact(f.X, y, half, "Pad position", l.Lat.ToString("0.0000") + "°, " + l.Lon.ToString("0.0000") + "°"); Fact(f.X + half, y, half, "Launches from this pad", l.PadCount > 0 ? l.PadCount.ToString() : null); y += 92 * u;
            Fact(f.X, y, half, "Launch azimuth", l.Heading.ToString("0") + "°" + (l.HeadingEstimated ? " est." : ""), "≈ " + Flight.Inclination(l.Lat, l.Heading).ToString("0.0") + "° inclination");
            Fact(f.X + half, y, half, "Weather", l.Prob.HasValue ? l.Prob.Value.ToString("0") + "% GO" : "no forecast yet", l.Concerns); y += 92 * u;
            if (l.Net.HasValue)
            {
                Fact(f.X, y, f.Width, "T-0", PadTime(l, l.Net.Value, "ddd d MMM HH:mm:ss"), l.Net.Value.ToString("ddd d MMM HH:mm:ss") + " UTC" + (Count.Mine(l) ? " · set by you" : l.Precise ? "" : " · not confirmed")); y += 92 * u;
                if (l.WinOpen.HasValue && l.WinClose.HasValue) Fact(f.X, y, f.Width, "Window", l.WinOpen.Value.ToString("HH:mm:ss") + " – " + l.WinClose.Value.ToString("HH:mm:ss") + " UTC", (l.WinClose.Value - l.WinOpen.Value).TotalMinutes.ToString("0") + " minutes");
            }
        }

        private void Vehicle(Launch l, RectangleF r)
        {
            var v = Card(r, "Vehicle · " + l.Rocket);
            float fw = v.Width / 6; Func<double?, string, string, string> q = (x, f, unit) => x.HasValue ? x.Value.ToString(f) + unit : null;
            Fact(v.X, v.Y, fw, "Height", q(l.LengthM, "0.#", " m")); Fact(v.X + fw, v.Y, fw, "Diameter", q(l.DiamM, "0.#", " m")); Fact(v.X + 2 * fw, v.Y, fw, "Liftoff mass", q(l.MassT, "#,0", " t"));
            Fact(v.X + 3 * fw, v.Y, fw, "Liftoff thrust", q(l.ThrustKN, "#,0", " kN")); Fact(v.X + 4 * fw, v.Y, fw, "To low Earth orbit", l.LeoKg.HasValue ? (l.LeoKg.Value / 1000).ToString("0.#") + " t" : null);
            Fact(v.X + 5 * fw, v.Y, fw, "To transfer orbit", l.GtoKg.HasValue ? (l.GtoKg.Value / 1000).ToString("0.#") + " t" : null);
            float y = v.Y + 100 * u;
            Fact(v.X, y, fw, "Thrust to weight", l.ThrustKN.HasValue && l.MassT.HasValue && l.MassT > 0 ? (l.ThrustKN.Value / (l.MassT.Value * 9.81)).ToString("0.00") : null, "at liftoff");
            Fact(v.X + fw, y, fw, "Flights", l.RocketCount > 0 ? l.RocketCount.ToString() : "0", l.RocketCount > 0 ? l.RocketOk + " successful" : "first flight of this configuration");
            Fact(v.X + 2 * fw, y, fw * 2, "Provider", l.Provider);
            Wrapped(l.VehicleText ?? "No vehicle description has been published.", new RectangleF(v.X, y + 110 * u, v.Width, v.Bottom - y - 110 * u), 22, th.Ink2);
        }

        private void Updates(Launch l, RectangleF r)
        {
            var n = Card(r, "Updates · " + l.Name);
            float y = n.Y;
            if (l.Updates.Count == 0) Text("no updates posted for this launch", n.X, y, 18, th.Muted);
            foreach (var up in l.Updates)
            {
                if (y > n.Bottom - 60 * u) break;
                Text((up.At.HasValue ? up.At.Value.ToString("ddd d MMM HH:mm") + " UTC" : "") + (up.By != null ? "  ·  " + up.By : ""), n.X, y, 14, th.Accent, true); y += 26 * u;
                y += Wrapped(up.Text, new RectangleF(n.X, y, n.Width, Math.Min(120 * u, n.Bottom - y)), 20, th.Ink) + 20 * u;
            }
        }

        // ---------------------------------------------------------------- everything, as a listing

        private void Raw(Launch l, RectangleF r)
        {
            float gap = 16 * u, lw = r.Width * 0.5f;
            var c = Card(new RectangleF(r.X, r.Y, lw, r.Height), "Launch record · Launch Library 2");
            var rows = new List<string[]>
            {
                new[] { "id", l.Id }, new[] { "name", l.Name }, new[] { "status", l.Status + " · " + l.StatusName }, new[] { "net (published)", l.SourceNet.HasValue ? l.SourceNet.Value.ToString("yyyy-MM-dd HH:mm:ss") + "Z" : null },
                new[] { "net (counted)", l.Net.HasValue ? l.Net.Value.ToString("yyyy-MM-dd HH:mm:ss") + "Z" : null }, new[] { "net precise", l.Precise.ToString() },
                new[] { "window", (l.WinOpen.HasValue ? l.WinOpen.Value.ToString("HH:mm:ss") : "?") + " – " + (l.WinClose.HasValue ? l.WinClose.Value.ToString("HH:mm:ss") : "?") + "Z" },
                new[] { "provider", l.Provider }, new[] { "rocket", l.Rocket }, new[] { "mission", l.Mission }, new[] { "mission type", l.MissionType }, new[] { "orbit", l.Orbit + " · " + l.OrbitName },
                new[] { "pad", l.PadName }, new[] { "location", l.Location }, new[] { "pad lat, lon", l.Lat.ToString("0.00000") + ", " + l.Lon.ToString("0.00000") }, new[] { "time zone", l.Tz },
                new[] { "azimuth", l.Heading.ToString("0.#") + "° " + (l.HeadingEstimated ? "(estimated from the orbit)" : "(published)") }, new[] { "probability", l.Prob.HasValue ? l.Prob.Value + "%" : null },
                new[] { "weather concerns", l.Concerns }, new[] { "thrust kN", l.ThrustKN.ToString() }, new[] { "mass t", l.MassT.ToString() }, new[] { "length m", l.LengthM.ToString() }, new[] { "diameter m", l.DiamM.ToString() },
                new[] { "LEO kg", l.LeoKg.ToString() }, new[] { "GTO kg", l.GtoKg.ToString() }, new[] { "timeline events", l.Timeline.Count + (l.Timeline.Count > 0 && l.Timeline[0].Nominal ? " (nominal)" : "") },
                new[] { "updates", l.Updates.Count.ToString() }, new[] { "webcast", l.Video },
            };
            float y = c.Y, lh = Math.Min(30 * u, c.Height / rows.Count);
            foreach (var row in rows) { Text(row[0], c.X, y, 14, th.Muted, false, true); Text(string.IsNullOrEmpty(row[1]) ? "—" : row[1], c.X + 210 * u, y, 14, th.Ink, false, true, -1, c.Width - 210 * u); y += lh; }

            var f = Card(new RectangleF(r.X + lw + gap, r.Y, r.Width - lw - gap, r.Height * 0.55f), "Sources");
            var names = new Dictionary<string, string>
            {
                { "LoadLaunches", "Launches · Launch Library 2" }, { "LoadHeadings", "Azimuths · countdown site" }, { "LoadWeather", "Weather · Open-Meteo" }, { "LoadClouds", "Cloud layers · Open-Meteo" },
                { "LoadMap", "Base map · Esri" }, { "LoadRadar", "Radar mosaic · NOAA" }, { "LoadAreas", "Hazard areas · countdown site" }, { "LoadNws", "Weather Service · NWS" },
                { "LoadSea", "Sea state and tides · Open-Meteo, NOAA" }, { "LoadSpace", "Space weather · NOAA SWPC" }, { "LoadVolume", "Radar volume · NEXRAD Level II" }, { "LoadVessels", "Vessels · aisstream.io" },
            };
            y = f.Y; lh = Math.Min(32 * u, f.Height / (names.Count + 1));
            lock (Store.Feeds)
                foreach (var kv in names)
                {
                    string st; bool has = Store.Feeds.TryGetValue(kv.Key, out st);
                    Text(kv.Value, f.X, y, 15, th.Ink2); Text(has ? st : "not asked yet", f.X + 330 * u, y, 14, !has ? th.Muted : st.StartsWith("ok") ? th.Good : th.Bad, false, true, -1, f.Width - 330 * u); y += lh;
                }
            bool live = (now - Store.PlanesAt).TotalSeconds < 60;
            Text("Aircraft · adsb.lol", f.X, y, 15, th.Ink2); Text(live ? "ok " + Store.PlanesAt.ToString("HH:mm:ss") + " · " + Store.Planes.Count + " aircraft" : "asked only while a map or the 3D view is showing", f.X + 330 * u, y, 14, live ? th.Good : th.Muted, false, true);

            var s = Card(new RectangleF(r.X + lw + gap, r.Y + r.Height * 0.55f + gap, r.Width - lw - gap, r.Height * 0.45f - gap), "Settings in use");
            var set = new[]
            {
                new[] { "theme", Settings.Theme }, new[] { "sites", Settings.SiteIds.Length == 0 ? "everywhere" : "Launch Library locations " + Settings.SiteIds }, new[] { "seconds per slide", Settings.SecondsPerTab.ToString() },
                new[] { "display off after", Settings.DisplayOffMinutes == 0 ? "left to Windows" : Settings.DisplayOffMinutes + " min" }, new[] { "wake for launch", Settings.WakeForLaunch.ToString() },
                new[] { "terminal layout", Settings.TerminalLayout.ToString() }, new[] { "3D", "view " + Settings.View + " · scale " + Settings.VerticalScale + "× · fields for " + (Settings.CloudTime == "now" ? "now" : "launch time") },
                new[] { "3D layers", string.Join(" ", new[] { Settings.Clouds ? "clouds" : null, Settings.RadarVolume ? "radar" : null, Settings.Aircraft ? "aircraft" : null, Settings.Vessels ? "vessels" : null, Settings.Hazards ? "hazards" : null, Settings.Wind ? "wind" : null }.Where(x => x != null)) },
            };
            y = s.Y; lh = Math.Min(30 * u, s.Height / set.Length);
            foreach (var row in set) { Text(row[0], s.X, y, 14, th.Muted, false, true); Text(row[1], s.X + 210 * u, y, 14, th.Ink, false, true, -1, s.Width - 210 * u); y += lh; }
        }
    }
}

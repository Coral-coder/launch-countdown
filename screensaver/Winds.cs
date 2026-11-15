// Upper-level winds from measurements, not just the forecast model: weather balloons (radiosondes)
// and radar wind profilers. Whichever source is chosen feeds the winds-aloft displays and every
// calculation that uses them (telemetry, shear and jet checks, the 3D wind column).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace LaunchCountdownSaver
{
    internal sealed class Sounding
    {
        public string Kind, Station, Name; public DateTime At; public double Lat, Lon; public bool WindOnly;
        public List<Level> Levels = new List<Level>(); // Z in metres; T, P, Rh are NaN where the instrument does not measure them
        public double AgeMinutes { get { return (DateTime.UtcNow - At).TotalMinutes; } }
    }

    internal static class Winds
    {
        private static readonly string[] Order = { "auto", "profiler", "balloon", "model" };
        public static string NextSource(string s) { return Order[(Array.IndexOf(Order, s) + 1 + Order.Length) % Order.Length]; }
        public static string SourceName(string s)
        {
            switch (s) { case "profiler": return "radar wind profiler"; case "balloon": return "weather balloon"; case "model": return "forecast model"; default: return "automatic (newest measurement near launch, model otherwise)"; }
        }

        private static string Age(Sounding s)
        {
            double m = s.AgeMinutes;
            return s.At.ToString("HH:mm") + " UTC, " + (m < 90 ? m.ToString("0") + " min ago" : (m / 60).ToString("0.#") + " h ago");
        }

        /// <summary>
        /// The profile to show and calculate with. A measurement only describes the air now, so "automatic"
        /// uses one when T-0 is within six hours (or already past) and the forecast for T-0 otherwise.
        /// </summary>
        public static List<Level> For(Launch l, out string label)
        {
            var w = Store.Weather; var now = DateTime.UtcNow;
            label = "no upper-air data yet";
            if (w == null || l == null || w.Lat != l.Lat) return new List<Level>();
            int hNow = w.Hour(now), hT0 = l.Net.HasValue ? w.Hour(l.Net.Value) : -1;
            bool near = !l.Net.HasValue || Math.Abs((l.Net.Value - now).TotalHours) < 6;
            string src = Settings.UpperSource; Sounding obs = null; string missing = null;
            var prof = Store.Profiler; var ball = Store.Balloon;
            Func<Sounding, double, bool> good = (s, maxAge) => s != null && s.Levels.Count >= 3 && s.AgeMinutes <= maxAge && Flight.KmBetween(l.Lat, l.Lon, s.Lat, s.Lon) < 350;
            if (src == "profiler") { if (good(prof, 180)) obs = prof; else missing = "no current profiler data for this site"; }
            else if (src == "balloon") { if (good(ball, 36 * 60)) obs = ball; else missing = "no recent balloon for this site"; }
            else if (src != "model" && near && (good(prof, 45) || good(ball, 12 * 60)))
            {
                // everything measured: the balloon over the model, then the radar's fresher low-level winds over that
                var lv = w.Profile(hNow); var parts = new List<string>();
                if (good(ball, 12 * 60)) { lv = Merge(ball, lv); parts.Add(ball.Name + " " + ball.Station + " " + Age(ball)); }
                if (good(prof, 45)) { lv = Merge(prof, lv); parts.Add(prof.Name + " " + prof.Station + " " + Age(prof)); }
                label = string.Join(" + ", parts) + " · model above";
                return lv;
            }

            if (obs == null)
            {
                label = (hT0 >= 0 ? "forecast model at T-0" : "forecast model now") + (missing != null ? " (" + missing + ")" : "");
                return w.Profile(hT0 >= 0 ? hT0 : hNow);
            }
            label = obs.Name + " " + obs.Station + " · " + Age(obs) + (obs.WindOnly ? " · model above" : "");
            return Merge(obs, w.Profile(hNow));
        }

        /// <summary>
        /// Measured winds at the measured heights. Temperature and pressure come from the balloon when it has
        /// them and from the model otherwise, and the model fills in above and below where the instrument stops.
        /// </summary>
        private static List<Level> Merge(Sounding obs, List<Level> model)
        {
            var o = new List<Level>();
            var lv = obs.Levels.Where(x => !double.IsNaN(x.Kt) && !double.IsNaN(x.Z)).OrderBy(x => x.Z).ToList();
            if (lv.Count == 0) return model;
            double bottom = lv[0].Z, top = lv[lv.Count - 1].Z;
            o.AddRange(model.Where(m => m.Z < bottom - 300));
            foreach (var x in lv)
            {
                var q = x;
                if (double.IsNaN(q.T)) q.T = Interp(model, q.Z, m => m.T, false);
                if (double.IsNaN(q.P) || q.P <= 0) q.P = Interp(model, q.Z, m => m.P, true);
                if (double.IsNaN(q.Rh)) q.Rh = Interp(model, q.Z, m => m.Rh, false);
                if (!double.IsNaN(q.T) && !double.IsNaN(q.P)) o.Add(q);
            }
            o.AddRange(model.Where(m => m.Z > top + 300));
            return o.Count >= 2 ? o : model;
        }

        private static double Interp(List<Level> m, double z, Func<Level, double> f, bool log)
        {
            if (m.Count == 0) return double.NaN;
            if (z <= m[0].Z) return f(m[0]);
            for (int i = 1; i < m.Count; i++)
            {
                if (z > m[i].Z) continue;
                double t = (z - m[i - 1].Z) / (m[i].Z - m[i - 1].Z), a = f(m[i - 1]), b = f(m[i]);
                return log ? a * Math.Pow(b / a, t) : a + (b - a) * t;
            }
            return f(m[m.Count - 1]);
        }
    }

    internal static partial class Store
    {
        public static volatile Sounding Balloon, Profiler;
        public static volatile string BalloonNote = "looking for a balloon sounding…", ProfilerNote = "looking for profiler data…";

        // Upper-air stations near launch sites: id, name, latitude, longitude, WMO number. The first three are the ranges' own.
        private static readonly object[][] BalloonSites =
        {
            new object[] { "KXMR", "Cape Canaveral", 28.48, -80.55, 74794 }, new object[] { "KVBG", "Vandenberg", 34.75, -120.57, 72393 }, new object[] { "KWAL", "Wallops Island", 37.94, -75.47, 72402 },
            new object[] { "KBRO", "Brownsville", 25.92, -97.42, 72250 }, new object[] { "KTBW", "Tampa Bay", 27.70, -82.40, 72210 }, new object[] { "KJAX", "Jacksonville", 30.48, -81.70, 72206 },
            new object[] { "KMFL", "Miami", 25.75, -80.38, 72202 }, new object[] { "KEPZ", "El Paso (Santa Teresa)", 31.87, -106.70, 72364 }, new object[] { "KMAF", "Midland", 31.94, -102.19, 72265 },
            new object[] { "KCRP", "Corpus Christi", 27.78, -97.51, 72251 }, new object[] { "KNKX", "San Diego (Miramar)", 32.85, -117.12, 72293 }, new object[] { "PADQ", "Kodiak", 57.75, -152.49, 70350 },
        };

        private static void LoadBalloon()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var near = BalloonSites.Where(s => Flight.KmBetween(c.Lat, c.Lon, (double)s[2], (double)s[3]) < 350).OrderBy(s => Flight.KmBetween(c.Lat, c.Lon, (double)s[2], (double)s[3])).ToList();
            if (near.Count == 0) { Balloon = null; BalloonNote = "no upper-air station near this site"; return; }
            Sounding best = null;
            foreach (var st in near)
            {
                var s = NewestBalloon((string)st[0], (string)st[1], (double)st[2], (double)st[3], (int)st[4]);
                if (s != null && (best == null || s.At > best.At.AddHours(3))) best = s; // a farther station only wins by being clearly newer
                if (best != null && best.AgeMinutes < 13 * 60) break;
            }
            Balloon = best; BalloonNote = best == null ? "no balloon in the last 36 hours" : "";
        }

        /// <summary>
        /// The newest sounding for a station, trying the last few 12-hourly launches. The University of Wyoming
        /// has the range balloons soonest but is often busy; Iowa State's archive is the fallback.
        /// </summary>
        private static Sounding NewestBalloon(string id, string name, double lat, double lon, int wmo)
        {
            var t = DateTime.UtcNow; t = new DateTime(t.Year, t.Month, t.Day, t.Hour >= 12 ? 12 : 0, 0, 0, DateTimeKind.Utc);
            for (int back = 0; back < 4; back++, t = t.AddHours(-12))
            {
                var s = new Sounding { Kind = "balloon", Station = id, Name = "balloon", At = t, Lat = lat, Lon = lon };
                for (int attempt = 0; attempt < 3 && s.Levels.Count == 0; attempt++)
                {
                    try
                    {
                        var csv = Get("https://weather.uwyo.edu/wsgi/sounding?datetime=" + t.ToString("yyyy-MM-dd") + "%20" + t.ToString("HH") + ":00:00&id=" + wmo + "&src=UNKNOWN&type=TEXT:CSV");
                        foreach (var row in csv.Split('\n').Skip(1))
                        {
                            // time, lon, lat, pressure hPa, height m, temperature, dew point, ice point, RH %, RH ice, mixing ratio, direction, speed m/s
                            var f = row.Split(','); if (f.Length < 13) continue;
                            Func<int, double> n = i => { double v; return double.TryParse(f[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : double.NaN; };
                            if (double.IsNaN(n(4))) continue;
                            s.Levels.Add(new Level { P = n(3), Z = n(4), T = n(5), Rh = n(8), Dir = n(11), Kt = n(12) * 1.94384 });
                        }
                    }
                    catch (System.Net.WebException e)
                    {
                        var r = e.Response as System.Net.HttpWebResponse;
                        if (r != null && (int)r.StatusCode == 404) break; // no such sounding
                        Thread.Sleep(2500);                               // "server too busy": try again
                    }
                }
                if (s.Levels.Count(x => !double.IsNaN(x.Kt)) >= 5) return s;
                try
                {
                    var p = J.Parse(Get("https://mesonet.agron.iastate.edu/json/raob.py?ts=" + t.ToString("yyyyMMddHHmm") + "&station=" + id))["profiles"][0];
                    s.Levels.Clear(); s.At = p["valid"].Time ?? t;
                    foreach (var x in p["profile"].Items)
                    {
                        double z = x["hght"].N(double.NaN), tc = x["tmpc"].N(double.NaN), td = x["dwpc"].N(double.NaN);
                        if (double.IsNaN(z)) continue;
                        double rh = double.IsNaN(tc) || double.IsNaN(td) ? double.NaN : 100 * Math.Exp(17.62 * td / (243.12 + td) - 17.62 * tc / (243.12 + tc));
                        s.Levels.Add(new Level { P = x["pres"].N(double.NaN), Z = z, T = tc, Kt = x["sknt"].N(double.NaN), Dir = x["drct"].N(double.NaN), Rh = rh });
                    }
                    if (s.Levels.Count(x => !double.IsNaN(x.Kt)) >= 5) return s;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// The radar wind profile: the nearest NEXRAD's velocity-azimuth display product, a column of measured
        /// winds over the radar about every five minutes. It reaches as high as there is something in the air
        /// to reflect from: a few kilometres on a clear day, the whole troposphere in cloud or rain.
        /// The KSC profiler (TDRWP) itself has no public live feed.
        /// </summary>
        private static void LoadProfiler()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var near = Radars.OrderBy(r => Flight.KmBetween(c.Lat, c.Lon, (double)r[1], (double)r[2])).First();
            if (Flight.KmBetween(c.Lat, c.Lon, (double)near[1], (double)near[2]) > 230) { Profiler = null; ProfilerNote = "no NEXRAD radar near this site"; return; }
            string id = (string)near[0];
            var raw = Bytes("https://tgftp.nws.noaa.gov/SL.us008001/DF.of/DC.radar/DS.48vwp/SI." + id.ToLowerInvariant() + "/sn.last");
            var text = System.Text.Encoding.GetEncoding(28591).GetString(raw); // the table is plain text inside a binary product
            var head = Regex.Match(text, @"VAD Algorithm Output\s+(\d\d)/(\d\d)/(\d\d)\s+(\d\d):(\d\d)");
            if (!head.Success) throw new Exception("no wind table in the radar product");
            Func<int, int> g = i => int.Parse(head.Groups[i].Value);
            var s = new Sounding { Kind = "profiler", Station = id, Name = "radar wind profile", WindOnly = true, Lat = (double)near[1], Lon = (double)near[2], At = new DateTime(2000 + g(3), g(1), g(2), g(4), g(5), 0, DateTimeKind.Utc) };
            // ALT (hundreds of feet)  U  V  W  DIR  SPD (knots)  RMS ...
            foreach (Match m in Regex.Matches(text, @"(\d{3})\s+(-?\d+\.\d)\s+(-?\d+\.\d)\s+(?:NA|-?\d+\.\d)\s+(\d{3})\s+(\d{3})\s+(\d+\.\d)"))
            {
                double z = int.Parse(m.Groups[1].Value) * 100 * 0.3048;
                if (s.Levels.Any(x => Math.Abs(x.Z - z) < 1)) continue;
                s.Levels.Add(new Level { Z = z, Dir = int.Parse(m.Groups[4].Value), Kt = int.Parse(m.Groups[5].Value), P = double.NaN, T = double.NaN, Rh = double.NaN });
            }
            if (s.Levels.Count < 3) { Profiler = null; ProfilerNote = "radar wind profile " + id + " has too few levels right now"; return; }
            Profiler = s; ProfilerNote = "";
        }
    }
}

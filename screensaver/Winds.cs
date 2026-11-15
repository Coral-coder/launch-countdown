// Upper-level winds from measurements, not just the forecast model: weather balloons (radiosondes)
// and radar wind profilers. Whichever source is chosen feeds the winds-aloft displays and every
// calculation that uses them (telemetry, shear and jet checks, the 3D wind column).

using System;
using System.Collections.Generic;
using System.Linq;

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
            else if (src != "model" && near) obs = good(prof, 45) ? prof : good(ball, 12 * 60) ? ball : null;

            if (obs == null)
            {
                label = (hT0 >= 0 ? "forecast model at T-0" : "forecast model now") + (missing != null ? " (" + missing + ")" : "");
                return w.Profile(hT0 >= 0 ? hT0 : hNow);
            }
            label = obs.Name + " " + obs.Station + " · " + Age(obs);
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

        // Upper-air stations near launch sites: id, name, latitude, longitude. The first three launch their own range balloons.
        private static readonly object[][] BalloonSites =
        {
            new object[] { "KXMR", "Cape Canaveral", 28.48, -80.55 }, new object[] { "KVBG", "Vandenberg", 34.75, -120.57 }, new object[] { "KWAL", "Wallops Island", 37.94, -75.47 },
            new object[] { "KBRO", "Brownsville", 25.92, -97.42 }, new object[] { "KTBW", "Tampa Bay", 27.70, -82.40 }, new object[] { "KJAX", "Jacksonville", 30.48, -81.70 },
            new object[] { "KMFL", "Miami", 25.75, -80.38 }, new object[] { "KEPZ", "El Paso (Santa Teresa)", 31.87, -106.70 }, new object[] { "KMAF", "Midland", 31.94, -102.19 },
            new object[] { "KCRP", "Corpus Christi", 27.78, -97.51 }, new object[] { "KNKX", "San Diego (Miramar)", 32.85, -117.12 }, new object[] { "PADQ", "Kodiak", 57.75, -152.49 },
        };

        private static void LoadBalloon()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var near = BalloonSites.Where(s => Flight.KmBetween(c.Lat, c.Lon, (double)s[2], (double)s[3]) < 350).OrderBy(s => Flight.KmBetween(c.Lat, c.Lon, (double)s[2], (double)s[3])).ToList();
            if (near.Count == 0) { Balloon = null; BalloonNote = "no upper-air station near this site"; return; }
            Sounding best = null;
            // balloons go up at 00Z and 12Z, and the ranges fly extra ones on launch days: look back hour by hour at the nearest station first
            foreach (var st in near)
            {
                var s = NewestBalloon((string)st[0], (string)st[1], (double)st[2], (double)st[3]);
                if (s != null && (best == null || s.At > best.At.AddHours(3))) best = s; // a farther station only wins by being clearly newer
                if (best != null && best.AgeMinutes < 13 * 60) break;
            }
            Balloon = best; BalloonNote = best == null ? "no balloon in the last 36 hours" : "";
        }

        /// <summary>The newest sounding Iowa State's archive has for a station, trying the last few 12-hourly launches.</summary>
        private static Sounding NewestBalloon(string id, string name, double lat, double lon)
        {
            var t = DateTime.UtcNow; t = new DateTime(t.Year, t.Month, t.Day, t.Hour >= 12 ? 12 : 0, 0, 0, DateTimeKind.Utc);
            for (int back = 0; back < 4; back++, t = t.AddHours(-12))
            {
                J d;
                try { d = J.Parse(Get("https://mesonet.agron.iastate.edu/json/raob.py?ts=" + t.ToString("yyyyMMddHHmm") + "&station=" + id)); } catch { continue; }
                var p = d["profiles"][0];
                if (p["profile"].Count < 5) continue;
                var s = new Sounding { Kind = "balloon", Station = id, Name = "balloon " + name, At = p["valid"].Time ?? t, Lat = lat, Lon = lon };
                foreach (var x in p["profile"].Items)
                {
                    double z = x["hght"].N(double.NaN), kt = x["sknt"].N(double.NaN), tc = x["tmpc"].N(double.NaN), td = x["dwpc"].N(double.NaN);
                    if (double.IsNaN(z)) continue;
                    double rh = double.IsNaN(tc) || double.IsNaN(td) ? double.NaN : 100 * Math.Exp(17.62 * td / (243.12 + td) - 17.62 * tc / (243.12 + tc));
                    s.Levels.Add(new Level { P = x["pres"].N(double.NaN), Z = z, T = tc, Kt = kt, Dir = x["drct"].N(double.NaN), Rh = rh });
                }
                if (s.Levels.Count(x => !double.IsNaN(x.Kt)) >= 5) return s;
            }
            return null;
        }

        private static void LoadProfiler()
        {
            Profiler = null; ProfilerNote = "no profiler feed connected yet";
        }
    }
}

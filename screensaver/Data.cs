// Everything the screensaver knows, fetched by the program itself on background threads:
// launches (Launch Library 2), weather and cloud layers (Open-Meteo), aircraft (adsb.lol),
// launch hazard areas (the countdown site's snapshot), a base map (Esri) and radar (NOAA).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LaunchCountdownSaver
{
    internal sealed class Site
    {
        public string Name; public int[] Ids;
        public Site(string name, params int[] ids) { Name = name; Ids = ids; }

        /// <summary>Launch Library 2 location ids, grouped the way people think of the sites.</summary>
        public static readonly Site[] All =
        {
            new Site("Florida (Cape Canaveral, Kennedy)", 12, 27),
            new Site("California (Vandenberg)", 11),
            new Site("Texas (Starbase, Van Horn)", 143, 29),
            new Site("Virginia (Wallops)", 21),
            new Site("Alaska (Kodiak)", 25),
            new Site("New Zealand (Mahia)", 10),
            new Site("French Guiana (Kourou)", 13),
            new Site("Japan (Tanegashima, Uchinoura)", 26, 24),
            new Site("India (Sriharikota)", 14),
            new Site("Kazakhstan (Baikonur)", 15),
            new Site("China (Jiuquan, Xichang, Taiyuan, Wenchang)", 17, 16, 19, 8),
        };
    }

    internal sealed class Evt { public double T; public string Name; public bool Nominal; }
    internal sealed class Note { public DateTime? At; public string Text, By; }

    internal sealed class Launch
    {
        public string Id, Name, Status, StatusName, Provider, Rocket, Mission, MissionType, Orbit, OrbitName, Description;
        public string PadName, Location, Tz, Concerns, Video, VehicleText;
        public DateTime? SourceNet, WinOpen, WinClose;
        /// <summary>T-0 as counted: the published time unless you set your own or are holding.</summary>
        public DateTime? Net { get { return Count.Net(this); } }
        public bool Precise, HeadingEstimated = true;
        public double Lat, Lon, Heading = 90;
        public double? Prob, ThrustKN, MassT, LengthM, DiamM, LeoKg, GtoKg;
        public int PadCount, RocketCount, RocketOk;
        public List<Evt> Timeline = new List<Evt>();
        public List<Note> Updates = new List<Note>();
        public bool HasPad { get { return Lat != 0 || Lon != 0; } }

        public static Launch From(J r)
        {
            var cfg = r["rocket"]["configuration"]; var pad = r["pad"]; var m = r["mission"];
            var l = new Launch
            {
                Id = r["id"].Str, Name = r["name"].Str ?? "Launch", Status = r["status"]["abbrev"].Str ?? "TBD", StatusName = r["status"]["name"].Str ?? "",
                Provider = r["launch_service_provider"]["name"].Str, Rocket = cfg["full_name"].Str ?? cfg["name"].Str,
                Mission = m["name"].Str, MissionType = m["type"].Str, Orbit = m["orbit"]["abbrev"].Str, OrbitName = m["orbit"]["name"].Str,
                Description = m["description"].Str, VehicleText = cfg["description"].Str,
                PadName = pad["name"].Str, Location = pad["location"]["name"].Str, Tz = pad["location"]["timezone_name"].Str,
                SourceNet = r["net"].Time, WinOpen = r["window_start"].Time, WinClose = r["window_end"].Time,
                Precise = (r["net_precision"]["id"].Num ?? 9) <= 1,
                Lat = pad["latitude"].N(), Lon = pad["longitude"].N(),
                Prob = r["probability"].Num, Concerns = r["weather_concerns"].Str,
                ThrustKN = cfg["to_thrust"].Num, MassT = cfg["launch_mass"].Num, LengthM = cfg["length"].Num, DiamM = cfg["diameter"].Num,
                LeoKg = cfg["leo_capacity"].Num, GtoKg = cfg["gto_capacity"].Num,
                PadCount = (int)pad["total_launch_count"].N(), RocketCount = (int)cfg["total_launch_count"].N(), RocketOk = (int)cfg["successful_launches"].N(),
            };
            if (l.Prob.HasValue && l.Prob < 0) l.Prob = null;
            foreach (var t in r["timeline"].Items)
            {
                double s;
                if (Duration(t["relative_time"].Str, out s)) l.Timeline.Add(new Evt { T = s, Name = t["type"]["abbrev"].Str ?? t["type"]["description"].Str ?? "Event" });
            }
            if (l.Timeline.Count == 0)
                foreach (var e in Flight.Events) l.Timeline.Add(new Evt { T = e.T, Name = e.Name, Nominal = true });
            l.Timeline.Sort((a, b) => a.T.CompareTo(b.T));
            foreach (var u in r["updates"].Items) l.Updates.Add(new Note { At = u["created_on"].Time, Text = u["comment"].Str, By = u["created_by"].Str });
            l.Updates.Sort((a, b) => Nullable.Compare(b.At, a.At));
            foreach (var v in r["vid_urls"].Items.OrderBy(x => x["priority"].N(99)))
            {
                var id = Regex.Match(v["url"].Str ?? "", @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|live/|embed/)|youtu\.be/)([\w-]{11})");
                if (id.Success) { l.Video = "https://www.youtube-nocookie.com/embed/" + id.Groups[1].Value + "?autoplay=1&mute=1&playsinline=1"; break; }
            }
            l.Heading = Flight.EstimateHeading(l.Lat, l.Orbit, l.Name);
            return l;
        }

        /// <summary>ISO 8601 duration with an optional sign ("-PT38M", "PT8M30S") in seconds.</summary>
        private static bool Duration(string s, out double seconds)
        {
            seconds = 0;
            var m = Regex.Match(s ?? "", @"^(-)?P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$");
            if (!m.Success) return false;
            Func<int, double> g = i => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            seconds = (g(2) * 86400 + g(3) * 3600 + g(4) * 60 + g(5)) * (m.Groups[1].Success ? -1 : 1);
            return true;
        }
    }

    internal struct Level { public double P, Z, T, Kt, Dir, Rh; }

    internal sealed class Wx
    {
        public DateTime Fetched;
        public double Lat, Lon;
        public double[] Time, Temp, Wind, Gust, Dir, Low, Mid, High, Pop, Rain, Cape, Vis, Dew, Hum, Msl;
        public static readonly int[] Levels = { 1000, 925, 850, 700, 600, 500, 400, 300, 250, 200, 150, 100 };
        public double[][] LvT, LvKt, LvDir, LvZ, LvRh;

        public int Hour(DateTime utc)
        {
            if (Time == null || Time.Length == 0) return -1;
            double t = (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            int best = 0;
            for (int i = 1; i < Time.Length; i++) if (Math.Abs(Time[i] - t) < Math.Abs(Time[best] - t)) best = i;
            return Math.Abs(Time[best] - t) <= 5400 ? best : -1;
        }

        public List<Level> Profile(int hour)
        {
            var o = new List<Level>();
            if (hour < 0 || LvZ == null) return o;
            for (int k = 0; k < Levels.Length; k++)
            {
                double z = LvZ[k][hour], t = LvT[k][hour];
                if (double.IsNaN(z) || double.IsNaN(t)) continue;
                o.Add(new Level { P = Levels[k], Z = z, T = t, Kt = LvKt[k][hour], Dir = LvDir[k][hour], Rh = LvRh[k][hour] });
            }
            o.Sort((a, b) => a.Z.CompareTo(b.Z));
            return o;
        }
    }

    /// <summary>Cloud cover on a grid around the pad: N x N columns, NZ layers from the surface to Top km.</summary>
    internal sealed class Clouds
    {
        public const int N = 7, NZ = 24; public const double Half = 150, Top = 16;
        public float[] Cover = new float[N * N * NZ]; // 0..1, index (k * N + j) * N + i, j south to north, i west to east
        public DateTime Valid; public double Lat, Lon; public float MaxLow, MaxMid, MaxHigh;
    }

    internal sealed class Plane
    {
        public string Id, Name, Type, Cat; public double Lat, Lon, AltFt, Kt, Track, Fpm, T; public bool Ground;
    }

    internal sealed class Area
    {
        public string Kind, Id, Title; public List<double[][]> Rings = new List<double[][]>(); public double FloorFt, CeilFt; public DateTime? From, To;
    }

    internal sealed class MapImage { public Bitmap Image; public double Lat, Lon, HalfKm; public DateTime At; }

    internal static partial class Store
    {
        public const double MapHalfKm = 400;
        public static volatile List<Launch> Launches = new List<Launch>();
        public static volatile Wx Weather;
        public static volatile Clouds Cloud;
        public static volatile List<Plane> Planes = new List<Plane>();
        public static DateTime PlanesAt = DateTime.MinValue;
        public static volatile List<Area> Areas = new List<Area>();
        public static volatile MapImage Map, Radar;
        public static volatile string LaunchNote = "loading launches…";
        public static DateTime LaunchesAt = DateTime.MinValue;
        /// <summary>Set by whatever is drawing aircraft; the feed is only polled while someone looks.</summary>
        public static DateTime TrafficWanted = DateTime.MinValue;
        public static int Version; // bumped whenever anything above changes, so views know to redraw

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static Dictionary<string, double> headings = new Dictionary<string, double>();
        private static bool started;
        private static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchCountdownSaver"); } }

        public static double Unix(DateTime t) { return (t - Epoch).TotalSeconds; }

        /// <summary>Set to follow a particular launch; otherwise the next one is followed.</summary>
        public static volatile string SelectedId;

        /// <summary>The launch being counted: the chosen one, or the first that has not flown more than twenty minutes ago.</summary>
        public static Launch Current
        {
            get
            {
                var now = DateTime.UtcNow; var all = Launches; var pick = SelectedId;
                if (pick != null) foreach (var l in all) if (l.Id == pick) return l;
                foreach (var l in all) if (l.Net.HasValue && l.Net.Value > now.AddMinutes(-20)) return l;
                return all.Count > 0 ? all[all.Count - 1] : null;
            }
        }

        private static bool light; private static volatile bool refresh;
        /// <summary>Fetch everything again soon (after a setting that changes what is fetched).</summary>
        public static void Refresh() { refresh = true; }

        /// <summary>launchesOnly is for the tray helper and the preview, which need nothing but the launch time.</summary>
        public static void Start(bool launchesOnly)
        {
            if (started) return; started = true; light = launchesOnly;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            try { var cached = File.ReadAllText(Path.Combine(Dir, "launches.json")); ParseLaunches(cached, "saved copy"); } catch { }
            new Thread(Slow) { IsBackground = true, Name = "data" }.Start();
            if (!light) new Thread(Fast) { IsBackground = true, Name = "aircraft" }.Start();
        }

        private static void Problem(string what, Exception e)
        {
#if TEST
            Console.WriteLine(what + ": " + e.Message + (e.InnerException != null ? " / " + e.InnerException.Message + (e.InnerException.InnerException != null ? " / " + e.InnerException.InnerException.Message : "") : ""));
#endif
        }

        private sealed class Job { public Action Run; public double Every; public DateTime Last = DateTime.MinValue; public string Key = ""; }

        private static void Slow()
        {
            var jobs = new[]
            {
                new Job { Run = LoadLaunches, Every = 600 }, new Job { Run = LoadHeadings, Every = 1800 }, new Job { Run = LoadWeather, Every = 600 },
                new Job { Run = LoadClouds, Every = 1800 }, new Job { Run = LoadMap, Every = 86400 }, new Job { Run = LoadRadar, Every = 300 }, new Job { Run = LoadAreas, Every = 600 },
                new Job { Run = LoadNws, Every = 900 }, new Job { Run = LoadSea, Every = 1800 }, new Job { Run = LoadSpace, Every = 900 },
            };
            if (light) jobs = new[] { new Job { Run = LoadLaunches, Every = 900 } };
            while (true)
            {
                var c = Current; var key = c == null ? "" : c.Id + c.Lat + c.Lon;
                if (refresh) { refresh = false; foreach (var j in jobs) if (j.Run != (Action)LoadLaunches) j.Last = DateTime.MinValue; }
                foreach (var j in jobs)
                {
                    // a different launch means different weather, map and radar straight away
                    bool due = (DateTime.UtcNow - j.Last).TotalSeconds >= j.Every || (j.Key != key && j.Run != (Action)LoadLaunches && j.Run != (Action)LoadHeadings);
                    if (!due) continue;
                    j.Last = DateTime.UtcNow; j.Key = key;
                    try { j.Run(); lock (Feeds) Feeds[j.Run.Method.Name] = "ok " + DateTime.UtcNow.ToString("HH:mm:ss"); }
                    catch (Exception e) { Problem(j.Run.Method.Name, e); lock (Feeds) Feeds[j.Run.Method.Name] = "failed " + DateTime.UtcNow.ToString("HH:mm:ss") + " · " + e.Message; }
                    Version++;
                }
                Thread.Sleep(1000);
            }
        }

        private static void Fast()
        {
            Thread.Sleep(4000); // let the launch list go first
            while (true)
            {
                if ((DateTime.UtcNow - TrafficWanted).TotalSeconds < 30) { try { LoadPlanes(); } catch { } Thread.Sleep(10000); }
                else Thread.Sleep(500);
            }
        }

        private sealed class Client : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address); var h = r as HttpWebRequest;
                r.Timeout = 25000;
                if (h != null) { h.AutomaticDecompression = DecompressionMethods.GZip; h.UserAgent = "LaunchCountdownScreensaver/2"; h.ReadWriteTimeout = 25000; }
                return r;
            }
        }
        private static string Get(string url) { using (var c = new Client { Encoding = Encoding.UTF8 }) return c.DownloadString(url); }
        private static byte[] Bytes(string url) { using (var c = new Client()) return c.DownloadData(url); }
        private static string F(double v, string fmt = "0.###") { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- launches

        private static void LoadLaunches()
        {
            var ids = Settings.SiteIds;
            var url = "https://ll.thespacedevs.com/2.3.0/launches/upcoming/?limit=12&mode=detailed&hide_recent_previous=true&format=json" + (ids.Length > 0 ? "&location__ids=" + ids : "");
            string body;
            try { body = Get(url); }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                LaunchNote = r != null && (int)r.StatusCode == 429 ? "launch source is rate-limiting; showing the saved copy" : "launch source unreachable; showing the saved copy";
                return;
            }
            if (ParseLaunches(body, null))
                try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "launches.json"), body); } catch { }
        }

        private static bool ParseLaunches(string body, string note)
        {
            var list = new List<Launch>();
            foreach (var r in J.Parse(body)["results"].Items) list.Add(Launch.From(r));
            if (list.Count == 0) return false;
            list.Sort((a, b) => Nullable.Compare(a.SourceNet, b.SourceNet));
            ApplyHeadings(list);
            Launches = list; LaunchesAt = DateTime.UtcNow; LaunchNote = note ?? ""; Version++;
            return true;
        }

        /// <summary>
        /// Launch Library has no launch azimuth. The countdown site's index carries the one Next Spaceflight
        /// publishes, so it is borrowed from there when the same launch can be found; otherwise it stays
        /// an estimate from the target orbit.
        /// </summary>
        private static void LoadHeadings()
        {
            var d = J.Parse(Get(Settings.SiteBase() + "data/launches.json"));
            var map = new Dictionary<string, double>();
            foreach (var l in d["launches"].Items)
            {
                var net = l["net"].Time; var h = l["heading"].Num;
                if (net.HasValue && h.HasValue) map[HeadingKey(net.Value, l["pad"]["lat"].N(), l["pad"]["lon"].N())] = h.Value;
            }
            headings = map;
            var list = Launches; ApplyHeadings(list); Version++;
        }
        private static string HeadingKey(DateTime net, double lat, double lon) { return net.ToString("yyyyMMddHH") + "|" + Math.Round(lat, 1) + "|" + Math.Round(lon, 1); }
        private static void ApplyHeadings(List<Launch> list)
        {
            var map = headings;
            foreach (var l in list)
            {
                double h;
                if (!l.SourceNet.HasValue) continue;
                foreach (var dt in new[] { 0, -1, 1 })
                    if (map.TryGetValue(HeadingKey(l.SourceNet.Value.AddHours(dt), l.Lat, l.Lon), out h)) { l.Heading = h; l.HeadingEstimated = false; break; }
            }
        }

        // ---------------------------------------------------------------- weather

        private static double[] Arr(J a)
        {
            var o = new double[a.Count]; int i = 0;
            foreach (var x in a.Items) o[i++] = x.Num ?? double.NaN;
            return o;
        }

        private static void LoadWeather()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var lv = new StringBuilder();
            foreach (var p in Wx.Levels) lv.Append(",temperature_" + p + "hPa,relative_humidity_" + p + "hPa,wind_speed_" + p + "hPa,wind_direction_" + p + "hPa,geopotential_height_" + p + "hPa");
            var d = J.Parse(Get("https://api.open-meteo.com/v1/forecast?latitude=" + F(c.Lat) + "&longitude=" + F(c.Lon) +
                "&hourly=temperature_2m,dew_point_2m,relative_humidity_2m,pressure_msl,wind_speed_10m,wind_gusts_10m,wind_direction_10m,cloud_cover_low,cloud_cover_mid,cloud_cover_high,precipitation_probability,precipitation,cape,visibility" +
                lv + "&wind_speed_unit=kn&timeformat=unixtime&past_hours=6&forecast_days=4"));
            var h = d["hourly"]; if (h["time"].Count == 0) return;
            var w = new Wx
            {
                Fetched = DateTime.UtcNow, Lat = c.Lat, Lon = c.Lon, Time = Arr(h["time"]), Temp = Arr(h["temperature_2m"]), Dew = Arr(h["dew_point_2m"]), Hum = Arr(h["relative_humidity_2m"]),
                Msl = Arr(h["pressure_msl"]), Wind = Arr(h["wind_speed_10m"]), Gust = Arr(h["wind_gusts_10m"]), Dir = Arr(h["wind_direction_10m"]),
                Low = Arr(h["cloud_cover_low"]), Mid = Arr(h["cloud_cover_mid"]), High = Arr(h["cloud_cover_high"]), Pop = Arr(h["precipitation_probability"]),
                Rain = Arr(h["precipitation"]), Cape = Arr(h["cape"]), Vis = Arr(h["visibility"]),
            };
            int n = Wx.Levels.Length;
            w.LvT = new double[n][]; w.LvRh = new double[n][]; w.LvKt = new double[n][]; w.LvDir = new double[n][]; w.LvZ = new double[n][];
            for (int k = 0; k < n; k++)
            {
                var p = Wx.Levels[k];
                w.LvT[k] = Arr(h["temperature_" + p + "hPa"]); w.LvRh[k] = Arr(h["relative_humidity_" + p + "hPa"]); w.LvKt[k] = Arr(h["wind_speed_" + p + "hPa"]);
                w.LvDir[k] = Arr(h["wind_direction_" + p + "hPa"]); w.LvZ[k] = Arr(h["geopotential_height_" + p + "hPa"]);
            }
            Weather = w;
        }

        private static readonly int[] CloudLevels = { 1000, 950, 925, 900, 850, 800, 700, 600, 500, 400, 300, 250, 200, 150 };

        private static void LoadClouds()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var now = DateTime.UtcNow;
            // the cloud field at launch time when that is within the forecast, otherwise now
            var at = Settings.CloudTime != "now" && c.Net.HasValue && c.Net.Value > now && c.Net.Value < now.AddDays(10) ? c.Net.Value : now;
            var hr = new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0, DateTimeKind.Utc);
            int N = Clouds.N;
            double dLat = Clouds.Half / 110.57, dLon = Clouds.Half / (111.32 * Math.Cos(c.Lat * Math.PI / 180));
            var lats = new List<string>(); var lons = new List<string>();
            for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) { lats.Add(F(c.Lat - dLat + 2 * dLat * j / (N - 1))); lons.Add(F(c.Lon - dLon + 2 * dLon * i / (N - 1))); }
            var stamp = hr.ToString("yyyy-MM-ddTHH:mm");
            var d = J.Parse(Get("https://api.open-meteo.com/v1/forecast?latitude=" + string.Join(",", lats) + "&longitude=" + string.Join(",", lons) +
                "&hourly=" + string.Join(",", CloudLevels.Select(p => "cloud_cover_" + p + "hPa")) + "&start_hour=" + stamp + "&end_hour=" + stamp));
            if (!d.IsList || d.Count != N * N) return;
            var zs = CloudLevels.Select(p => 44.33 * (1 - Math.Pow(p / 1013.25, 0.1903))).ToArray();
            var o = new Clouds { Valid = hr, Lat = c.Lat, Lon = c.Lon };
            int col = 0;
            foreach (var pt in d.Items)
            {
                var v = CloudLevels.Select(p => pt["hourly"]["cloud_cover_" + p + "hPa"][0].N()).ToArray();
                for (int k = 0; k < Clouds.NZ; k++)
                {
                    double z = (k + 0.5) / Clouds.NZ * Clouds.Top, val;
                    int i = Array.FindIndex(zs, x => x > z);
                    if (i == 0) val = v[0];
                    else if (i < 0) val = v[v.Length - 1] * Math.Max(0, 1 - (z - zs[zs.Length - 1]) / 1.5);
                    else val = v[i - 1] + (v[i] - v[i - 1]) * (z - zs[i - 1]) / (zs[i] - zs[i - 1]);
                    float f = (float)(val / 100);
                    o.Cover[k * N * N + col] = f;
                    if (z < 2) o.MaxLow = Math.Max(o.MaxLow, f); else if (z < 6) o.MaxMid = Math.Max(o.MaxMid, f); else o.MaxHigh = Math.Max(o.MaxHigh, f);
                }
                col++;
            }
            Cloud = o;
        }

        // ---------------------------------------------------------------- aircraft and hazard areas

        private static void LoadPlanes()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var d = J.Parse(Get("https://api.adsb.lol/v2/lat/" + F(c.Lat, "0.000") + "/lon/" + F(c.Lon, "0.000") + "/dist/150"));
            if (!d["ac"].IsList) return;
            double now = d["now"].N() > 1e12 ? d["now"].N() / 1000 : Unix(DateTime.UtcNow);
            var list = new List<Plane>();
            foreach (var a in d["ac"].Items)
            {
                if (a["lat"].Num == null || a["hex"].Str == null) continue;
                var alt = a["alt_geom"].IsNull ? a["alt_baro"] : a["alt_geom"];
                list.Add(new Plane
                {
                    Id = a["hex"].Str, Name = (a["flight"].Str ?? a["r"].Str ?? a["hex"].Str).Trim(), Type = a["t"].Str ?? "", Cat = a["category"].Str ?? "",
                    Lat = a["lat"].N(), Lon = a["lon"].N(), AltFt = alt.Num ?? 0, Ground = alt.Str == "ground",
                    Kt = a["gs"].N(), Track = a["track"].N(), Fpm = (a["geom_rate"].IsNull ? a["baro_rate"] : a["geom_rate"]).N(), T = now - a["seen_pos"].N(),
                });
            }
            Planes = list; PlanesAt = DateTime.UtcNow; Version++;
        }

        private static void LoadAreas()
        {
            var d = J.Parse(Get(Settings.SiteBase() + "data/hazards.json?t=" + DateTime.UtcNow.Ticks));
            var list = new List<Area>();
            foreach (var a in d["areas"].Items)
            {
                var o = new Area { Kind = a["kind"].Str ?? "notam", Id = a["id"].Str ?? "", Title = a["title"].Str ?? "", FloorFt = a["floor_ft"].N(), CeilFt = a["ceil_ft"].N(), From = a["from"].Time, To = a["to"].Time };
                foreach (var r in a["rings"].Items) o.Rings.Add(r.Items.Select(p => new[] { p[0].N(), p[1].N() }).ToArray());
                if (o.Rings.Count > 0) list.Add(o);
            }
            Areas = list;
        }

        // ---------------------------------------------------------------- map and radar pictures

        private static void Box(Launch c, out double w, out double s, out double e, out double n)
        {
            double dLat = MapHalfKm / 110.57, dLon = MapHalfKm / (111.32 * Math.Cos(c.Lat * Math.PI / 180));
            w = c.Lon - dLon; e = c.Lon + dLon; s = c.Lat - dLat; n = c.Lat + dLat;
        }

        private static Bitmap Picture(string url)
        {
            using (var ms = new MemoryStream(Bytes(url))) using (var src = Image.FromStream(ms)) return new Bitmap(src); // a copy, so the stream can go
        }

        private static void LoadMap()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            double w, s, e, n; Box(c, out w, out s, out e, out n);
            int px = 2048, py = (int)Math.Round(px * (n - s) / (e - w));
            var img = Picture("https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/export?bbox=" + F(w, "0.####") + "," + F(s, "0.####") + "," + F(e, "0.####") + "," + F(n, "0.####") +
                "&bboxSR=4326&imageSR=4326&size=" + px + "," + py + "&format=png32&f=image");
            Map = new MapImage { Image = img, Lat = c.Lat, Lon = c.Lon, HalfKm = MapHalfKm, At = DateTime.UtcNow };
        }

        private static void LoadRadar()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            // NOAA's mosaic covers the United States; elsewhere there is no radar picture
            if (!(c.Location ?? "").Contains("USA")) { Radar = null; return; }
            double w, s, e, n; Box(c, out w, out s, out e, out n);
            int px = 1024, py = (int)Math.Round(px * (n - s) / (e - w));
            var img = Picture("https://opengeo.ncep.noaa.gov/geoserver/conus/conus_bref_qcd/ows?service=WMS&version=1.1.1&request=GetMap&layers=conus_bref_qcd&styles=&srs=EPSG:4326&bbox=" +
                F(w, "0.####") + "," + F(s, "0.####") + "," + F(e, "0.####") + "," + F(n, "0.####") + "&width=" + px + "&height=" + py + "&format=image/png&transparent=true");
            Radar = new MapImage { Image = img, Lat = c.Lat, Lon = c.Lon, HalfKm = MapHalfKm, At = DateTime.UtcNow };
        }
    }
}

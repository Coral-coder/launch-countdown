// More sources: National Weather Service (forecast, alerts, observations), sea state and tides,
// and space weather. All fetched directly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LaunchCountdownSaver
{
    internal sealed class Period { public string Name, Short, Detail, Wind; public double Temp; public bool Day; }
    internal sealed class Alert { public string Event, Headline, Severity; public DateTime? Ends; }
    internal sealed class Nws
    {
        public List<Period> Periods = new List<Period>(); public List<Alert> Alerts = new List<Alert>();
        public string Station, Sky, Office; public double TempC = double.NaN, DewC = double.NaN, WindKt = double.NaN, WindDir = double.NaN, GustKt = double.NaN, VisKm = double.NaN, Hpa = double.NaN;
        public DateTime? ObsAt; public double Lat; public bool Covered;
    }
    internal sealed class Tide { public DateTime At; public double Ft; public bool High; }
    internal sealed class Sea
    {
        public double Wave = double.NaN, WaveDir = double.NaN, WavePeriod = double.NaN, Swell = double.NaN, SwellPeriod = double.NaN, TempC = double.NaN, Lat;
        public double[] Time = new double[0], Waves = new double[0], Swells = new double[0];
        public List<Tide> Tides = new List<Tide>(); public string TideStation;
    }
    internal sealed class Space
    {
        public double Kp = double.NaN, WindKms = double.NaN, Bt = double.NaN, Bz = double.NaN, Flux = double.NaN; public string R = "—", S = "—", G = "—";
        public List<double[]> KpHistory = new List<double[]>(); // unix time, Kp
    }

    internal static partial class Store
    {
        public static volatile Nws Service; public static volatile Sea Ocean; public static volatile Space Sun;
        /// <summary>When each source last answered, and what went wrong if it did not.</summary>
        public static readonly Dictionary<string, string> Feeds = new Dictionary<string, string>();

        private static void LoadNws()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var o = new Nws { Lat = c.Lat };
            if (!(c.Location ?? "").Contains("USA")) { Service = o; return; } // the Weather Service covers the United States only
            o.Covered = true;
            var pt = J.Parse(Get("https://api.weather.gov/points/" + F(c.Lat, "0.####") + "," + F(c.Lon, "0.####")))["properties"];
            o.Office = pt["cwa"].Str;
            foreach (var p in J.Parse(Get(pt["forecast"].Str))["properties"]["periods"].Items)
                o.Periods.Add(new Period { Name = p["name"].Str, Short = p["shortForecast"].Str, Detail = p["detailedForecast"].Str, Temp = p["temperature"].N(double.NaN), Day = p["isDaytime"].Bool, Wind = p["windSpeed"].Str + " " + p["windDirection"].Str });
            foreach (var a in J.Parse(Get("https://api.weather.gov/alerts/active?point=" + F(c.Lat, "0.####") + "," + F(c.Lon, "0.####")))["features"].Items)
                o.Alerts.Add(new Alert { Event = a["properties"]["event"].Str, Headline = a["properties"]["headline"].Str, Severity = a["properties"]["severity"].Str, Ends = a["properties"]["ends"].Time ?? a["properties"]["expires"].Time });
            try
            {
                var st = J.Parse(Get(pt["observationStations"].Str))["features"][0]["properties"]["stationIdentifier"].Str;
                var ob = J.Parse(Get("https://api.weather.gov/stations/" + st + "/observations/latest"))["properties"];
                o.Station = st; o.Sky = ob["textDescription"].Str; o.ObsAt = ob["timestamp"].Time;
                o.TempC = ob["temperature"]["value"].N(double.NaN); o.DewC = ob["dewpoint"]["value"].N(double.NaN);
                o.WindKt = ob["windSpeed"]["value"].N(double.NaN) / 1.852; o.GustKt = ob["windGust"]["value"].N(double.NaN) / 1.852; o.WindDir = ob["windDirection"]["value"].N(double.NaN);
                o.VisKm = ob["visibility"]["value"].N(double.NaN) / 1000; o.Hpa = ob["barometricPressure"]["value"].N(double.NaN) / 100;
            }
            catch { }
            Service = o;
        }

        // NOAA tide stations near the launch sites that have one: name, latitude, longitude, station id
        private static readonly object[][] TideStations =
        {
            new object[] { "Port Canaveral (Trident Pier)", 28.4158, -80.5931, "8721604" }, new object[] { "Wachapreague, Virginia", 37.6078, -75.6858, "8631044" },
            new object[] { "Port Isabel, Texas", 26.0612, -97.2155, "8779770" }, new object[] { "Port San Luis, California", 35.1689, -120.7542, "9412110" },
            new object[] { "Kodiak Island, Alaska", 57.7300, -152.5139, "9457292" },
        };

        private static void LoadSea()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            var o = new Sea { Lat = c.Lat };
            // waves a little offshore along the launch direction, since the pad itself is on land
            double la, lo; Flight.Destination(c.Lat, c.Lon, c.Heading, 30, out la, out lo);
            try
            {
                var d = J.Parse(Get("https://marine-api.open-meteo.com/v1/marine?latitude=" + F(la) + "&longitude=" + F(lo) +
                    "&current=wave_height,wave_direction,wave_period,swell_wave_height,swell_wave_period,sea_surface_temperature&hourly=wave_height,swell_wave_height&timeformat=unixtime&forecast_days=3"));
                var cur = d["current"];
                o.Wave = cur["wave_height"].N(double.NaN); o.WaveDir = cur["wave_direction"].N(double.NaN); o.WavePeriod = cur["wave_period"].N(double.NaN);
                o.Swell = cur["swell_wave_height"].N(double.NaN); o.SwellPeriod = cur["swell_wave_period"].N(double.NaN); o.TempC = cur["sea_surface_temperature"].N(double.NaN);
                o.Time = Arr(d["hourly"]["time"]); o.Waves = Arr(d["hourly"]["wave_height"]); o.Swells = Arr(d["hourly"]["swell_wave_height"]);
            }
            catch (Exception e) { Problem("LoadSea waves", e); }
            var near = TideStations.Where(s => Flight.KmBetween(c.Lat, c.Lon, (double)s[1], (double)s[2]) < 200).FirstOrDefault();
            if (near != null)
            {
                try
                {
                    var d = J.Parse(Get("https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?product=predictions&application=LaunchCountdown&begin_date=" + DateTime.UtcNow.ToString("yyyyMMdd") +
                        "&range=60&datum=MLLW&station=" + near[3] + "&time_zone=gmt&units=english&interval=hilo&format=json"));
                    foreach (var p in d["predictions"].Items)
                    {
                        DateTime t;
                        if (DateTime.TryParseExact(p["t"].Str, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
                            o.Tides.Add(new Tide { At = t, Ft = p["v"].N(), High = p["type"].Str == "H" });
                    }
                    o.TideStation = (string)near[0];
                }
                catch (Exception e) { Problem("LoadSea tides", e); }
            }
            Ocean = o;
        }

        private static void LoadSpace()
        {
            var o = new Space(); const string B = "https://services.swpc.noaa.gov/products/";
            try
            {
                foreach (var row in J.Parse(Get(B + "noaa-planetary-k-index.json")).Items)
                {
                    // rows are either arrays (time, Kp, ...) under a header row, or objects
                    var t = row.IsList ? row[0].Time : row["time_tag"].Time; var k = row.IsList ? row[1].Num : row["Kp"].Num;
                    if (t.HasValue && k.HasValue) o.KpHistory.Add(new[] { Unix(t.Value), k.Value });
                }
                if (o.KpHistory.Count > 0) o.Kp = o.KpHistory[o.KpHistory.Count - 1][1];
            }
            catch (Exception e) { Problem("LoadSpace Kp", e); }
            try { var d = J.Parse(Get(B + "summary/solar-wind-speed.json")); o.WindKms = (d.IsList ? d[0] : d)["WindSpeed"].N(double.NaN); if (double.IsNaN(o.WindKms)) o.WindKms = (d.IsList ? d[0] : d)["proton_speed"].N(double.NaN); } catch { }
            try { var d = J.Parse(Get(B + "summary/solar-wind-mag-field.json")); var q = d.IsList ? d[0] : d; o.Bt = q["Bt"].N(q["bt"].N(double.NaN)); o.Bz = q["Bz"].N(q["bz_gsm"].N(double.NaN)); } catch { }
            try { var d = J.Parse(Get(B + "summary/10cm-flux.json")); var q = d.IsList ? d[0] : d; o.Flux = q["Flux"].N(q["flux"].N(double.NaN)); } catch { }
            try { var d = J.Parse(Get(B + "noaa-scales.json"))["0"]; o.R = "R" + (d["R"]["Scale"].Str ?? "0"); o.S = "S" + (d["S"]["Scale"].Str ?? "0"); o.G = "G" + (d["G"]["Scale"].Str ?? "0"); } catch { }
            Sun = o;
        }
    }
}

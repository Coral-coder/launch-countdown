// The nominal ascent profile and everything computed from it: position along the corridor,
// calculated telemetry through the forecast atmosphere, and the geometry used for traffic alerts.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LaunchCountdownSaver
{
    internal struct Telemetry
    {
        public double Alt, Downrange, V, Vv, Gamma, G, Mach, Q, TempC, P, Rho;
        public double? WindKt, WindDir, Head, Cross, Tw;
        public bool MaxQ; public string Env;
    }

    internal static class Flight
    {
        public const double R = 6371.0, CorridorKm = 500, End = 530;
        private const double D2R = Math.PI / 180;

        // Generic two-stage orbital ascent: seconds, altitude km, downrange km. A sketch of a typical
        // profile, not provider data; real trajectories differ by vehicle and mission.
        private static readonly double[][] Ascent =
        {
            new[] { 0, 0, 0.0 }, new[] { 20, 1.2, 0.02 }, new[] { 40, 4.5, 0.4 }, new[] { 60, 9.5, 1.8 }, new[] { 75, 14, 4.5 }, new[] { 100, 25, 13.0 },
            new[] { 125, 40, 32.0 }, new[] { 150, 62, 65.0 }, new[] { 175, 85, 115.0 }, new[] { 200, 105, 180.0 }, new[] { 250, 135, 330.0 }, new[] { 300, 155, 520.0 },
            new[] { 350, 167, 750.0 }, new[] { 400, 175, 1020.0 }, new[] { 450, 182, 1340.0 }, new[] { 500, 190, 1700.0 }, new[] { 530, 200, 1950.0 },
        };
        public static readonly Evt[] Events =
        {
            new Evt { T = 0, Name = "Liftoff" }, new Evt { T = 72, Name = "Max-Q" }, new Evt { T = 152, Name = "MECO" }, new Evt { T = 190, Name = "Kármán line" }, new Evt { T = 530, Name = "SECO" },
        };

        public static double CorridorHalf(double alongKm) { return 8 + 0.06 * alongKm; }

        /// <summary>Catmull-Rom through a table column, so position is a smooth function of flight time.</summary>
        private static double Spline(int col, double t)
        {
            int n = Ascent.Length, i = Array.FindIndex(Ascent, r => r[0] > t);
            if (i < 0) i = n - 1;
            i = Math.Max(1, i);
            Func<int, double> p = k => Ascent[Math.Min(n - 1, Math.Max(0, k))][col];
            double t0 = Ascent[i - 1][0], u = Math.Min(1, Math.Max(0, (t - t0) / (Ascent[i][0] - t0)));
            double a = p(i - 2), b = p(i - 1), c = p(i), d = p(i + 1);
            return Math.Max(0, 0.5 * (2 * b + (c - a) * u + (2 * a - 5 * b + 4 * c - d) * u * u + (3 * b - 3 * c + d - a) * u * u * u));
        }
        public static double Alt(double t) { return Spline(1, t); }
        public static double Downrange(double t) { return Spline(2, t); }

        public static void Destination(double lat, double lon, double bearing, double km, out double lat2, out double lon2)
        {
            double f1 = lat * D2R, l1 = lon * D2R, th = bearing * D2R, d = km / R;
            double f2 = Math.Asin(Math.Sin(f1) * Math.Cos(d) + Math.Cos(f1) * Math.Sin(d) * Math.Cos(th));
            double l2 = l1 + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(f1), Math.Cos(d) - Math.Sin(f1) * Math.Sin(f2));
            lat2 = f2 / D2R; lon2 = ((l2 / D2R + 540) % 360) - 180;
        }

        public static double KmBetween(double la1, double lo1, double la2, double lo2)
        {
            double x = (lo2 - lo1) * D2R * Math.Cos((la1 + la2) / 2 * D2R), y = (la2 - la1) * D2R;
            return Math.Sqrt(x * x + y * y) * R;
        }

        public static double Inclination(double lat, double azimuth) { return Math.Acos(Math.Cos(lat * D2R) * Math.Sin(azimuth * D2R)) / D2R; }

        /// <summary>A launch azimuth guessed from the target orbit, for when no published one is known.</summary>
        public static double EstimateHeading(double lat, string orbit, string name)
        {
            orbit = (orbit ?? "").ToUpperInvariant(); name = name ?? "";
            double incl;
            if (orbit == "SSO") incl = 97.6;
            else if (orbit == "PO" || orbit == "POLAR") incl = 90;
            else if (Regex.IsMatch(name, "Starlink", RegexOptions.IgnoreCase)) incl = Math.Abs(lat) > 33 ? 53 : 43;
            else if (Regex.IsMatch(name, @"ISS|Crew|Dragon|Cygnus|Progress|Soyuz MS|CRS", RegexOptions.IgnoreCase)) incl = 51.6;
            else if (orbit == "MEO") incl = 55;
            else return 90; // geostationary transfers, lunar and deep-space missions leave due east
            double s = Math.Cos(incl * D2R) / Math.Cos(lat * D2R);
            if (Math.Abs(s) > 1) return 90;
            double az = Math.Asin(s) / D2R;
            return incl > 90 ? 180 - az : az; // retrograde orbits go south, the rest north-east
        }

        /// <summary>True when a point lies in the surface footprint of the ascent corridor.</summary>
        public static bool InCorridor(Launch l, double lat, double lon)
        {
            double h = l.Heading * D2R;
            double dx = (lon - l.Lon) * D2R * Math.Cos(l.Lat * D2R) * R, dy = (lat - l.Lat) * D2R * R;
            double along = dx * Math.Sin(h) + dy * Math.Cos(h), cross = Math.Abs(dx * Math.Cos(h) - dy * Math.Sin(h));
            return along >= -3 && along <= CorridorKm && cross <= CorridorHalf(Math.Max(0, along));
        }

        public static bool InRing(double lat, double lon, double[][] ring)
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                double yi = ring[i][0], xi = ring[i][1], yj = ring[j][0], xj = ring[j][1];
                if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>light, bizjet, jet, heavy or heli, from the ADS-B category and type code.</summary>
        public static string AircraftKind(Plane v)
        {
            string t = (v.Type ?? "").ToUpperInvariant(), cat = v.Cat ?? "";
            if (cat == "A7" || Regex.IsMatch(t, @"^(H\d|EC\d|AS\d|B06|B40[79]|B41|B42|B43|R22|R44|R66|S6[14]|S7[06]|S92|UH|AH|CH|MH|MD[569]|A109|A119|A139|A169|AW|BK17|B505|EN[24]8)")) return "heli";
            if (cat == "A5" || Regex.IsMatch(t, @"^(B74|B77|B78|A33|A34|A35|A38|MD11|DC10|C17|C5M|K35|KC|B52|A124|IL76)")) return "heavy";
            if (Regex.IsMatch(t, @"^(C5[0-9]|C6[0-9]|C7[0-9]|C25|GL|G[1-6]\d\d|GLF|LJ|F2T|F9|FA|H25|HA4|PC24|E35|E55|E50|CL[36]|BE40|PRM)")) return "bizjet";
            if (Regex.IsMatch(cat, "^A[346]$") || Regex.IsMatch(t, @"^(B7[0-9]|B3[0-9]|A[23]\d|A1[49]|BCS|E1[3-9]|E[27]\d|CRJ|MD[18]|DC|F1[568]|F22|F35|T38)")) return "jet";
            if (v.Kt > 260 || v.AltFt > 24000) return "jet";
            return "light";
        }

        /// <summary>
        /// "Jolly" is the callsign of the Air Force rescue helicopters that support launches from inside the
        /// closed areas. They are allowed there, so they are drawn green and never raise an alert.
        /// </summary>
        public static bool IsJolly(Plane v)
        {
            return !v.Sea && (v.Name ?? "").TrimStart().StartsWith("JOLLY", StringComparison.OrdinalIgnoreCase) && AircraftKind(v) == "heli";
        }

        /// <summary>boat, cargo, tanker, liner or sail, from the AIS ship type when one was heard.</summary>
        public static string VesselKind(Plane v)
        {
            int t = v.ShipType;
            return t >= 70 && t <= 79 ? "cargo" : t >= 80 && t <= 89 ? "tanker" : t >= 60 && t <= 69 ? "liner" : t == 36 || t == 37 ? "sail" : "boat";
        }

        // ---------------------------------------------------------------- calculated telemetry

        private struct Air { public double T, P, Rho; public double? Kt, Dir; public string Src; }

        private static Air Atmosphere(double zKm, List<Level> lv, string src)
        {
            if (lv == null || lv.Count < 2)
            {
                double ts = zKm < 11 ? 15 - 6.5 * zKm : -56.5, ps = 1013.25 * Math.Exp(-zKm / 7.2);
                return new Air { T = ts, P = ps, Rho = ps * 100 / (287.05 * (ts + 273.15)), Src = "standard atmosphere" };
            }
            double z = zKm * 1000, T, p; double? kt = null, dir = null;
            var top = lv[lv.Count - 1];
            if (z >= top.Z) { T = top.T; p = top.P * Math.Exp(-(z - top.Z) / 6500); } // above the sounding: hold temperature, let pressure fall off
            else
            {
                int i = Math.Max(1, lv.FindIndex(l => l.Z > z));
                Level a = lv[i - 1], b = lv[i];
                double f = Math.Min(1, Math.Max(0, (z - a.Z) / (b.Z - a.Z)));
                T = a.T + (b.T - a.T) * f; p = a.P * Math.Pow(b.P / a.P, f);
                if (!double.IsNaN(a.Kt) && !double.IsNaN(b.Kt))
                {
                    // blend winds as vectors so a direction change through north does not swing the long way round
                    Func<Level, double> u = l => -l.Kt * Math.Sin(l.Dir * D2R), v = l => -l.Kt * Math.Cos(l.Dir * D2R);
                    double uu = u(a) + (u(b) - u(a)) * f, vv = v(a) + (v(b) - v(a)) * f;
                    kt = Math.Sqrt(uu * uu + vv * vv); dir = (Math.Atan2(-uu, -vv) / D2R + 360) % 360;
                }
            }
            return new Air { T = T, P = p, Rho = p * 100 / (287.05 * (T + 273.15)), Kt = kt, Dir = dir, Src = src };
        }

        private static void Vel(double t, out double vh, out double vv)
        {
            const double h = 1;
            if (t <= 0) { vh = vv = 0; return; }
            double span = t < h ? t + h : 2 * h;
            vh = (Spline(2, t + h) - Spline(2, Math.Max(0, t - h))) / span;
            vv = (Spline(1, t + h) - Spline(1, Math.Max(0, t - h))) / span;
        }

        /// <summary>
        /// Flight state at time t from the nominal profile, flown through the forecast atmosphere. Speeds are
        /// ground-relative; Mach and dynamic pressure use the airspeed after subtracting the wind at that height.
        /// </summary>
        public static Telemetry At(double t, Launch l, List<Level> sounding, string src)
        {
            double alt = Spline(1, t), vh, vv, h1, v1, h2, v2, hq, vq;
            Vel(t, out vh, out vv); Vel(Math.Max(0, t - 3), out h1, out v1); Vel(t + 3, out h2, out v2); Vel(t + 4, out hq, out vq);
            double v = Math.Sqrt(vh * vh + vv * vv), span = t < 3 ? t + 3 : 6;
            double ax = (h2 - h1) / span * 1000, ay = (v2 - v1) / span * 1000 + 9.81 * Math.Max(0, 1 - Math.Pow(vh / 7.8, 2));
            var A = Atmosphere(alt, sounding, src);
            double air = v; double? head = null, cross = null;
            if (A.Kt.HasValue)
            {
                double rel = (A.Dir.Value - l.Heading) * D2R, ms = A.Kt.Value * 0.5144;
                head = A.Kt.Value * Math.Cos(rel); cross = A.Kt.Value * Math.Sin(rel);
                double x = vh * 1000 + ms * Math.Cos(rel), y = ms * Math.Sin(rel), zz = vv * 1000;
                air = Math.Sqrt(x * x + y * y + zz * zz) / 1000;
            }
            double q = 0.5 * A.Rho * Math.Pow(air * 1000, 2);
            double qNext = 0.5 * Atmosphere(Spline(1, t + 4), sounding, src).Rho * (hq * hq + vq * vq) * 1e6;
            return new Telemetry
            {
                Alt = alt, Downrange = Spline(2, t), V = v, Vv = vv, Gamma = v > 0.001 ? Math.Atan2(vv, vh) / D2R : 90,
                G = Math.Sqrt(ax * ax + ay * ay) / 9.81, Mach = air * 1000 / Math.Sqrt(1.4 * 287.05 * (A.T + 273.15)), Q = q,
                MaxQ = t > 20 && q > 15000 && Math.Abs(q - qNext) / q < 0.08, TempC = A.T, P = A.P, Rho = A.Rho,
                WindKt = A.Kt, WindDir = A.Dir, Head = head, Cross = cross,
                Tw = l.ThrustKN.HasValue && l.MassT.HasValue && l.MassT > 0 ? l.ThrustKN / (l.MassT * 9.81) : null, Env = A.Src,
            };
        }
    }
}

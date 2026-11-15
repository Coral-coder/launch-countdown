// The 3D ascent corridor: base map and radar on the ground, cloud layers as a volume, the nominal
// trajectory and its corridor, live aircraft as small models at their real height, and hazard areas.
// Distances are kilometres with no vertical exaggeration: x east, y up, z south, the pad at the origin.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LaunchCountdownSaver
{
    internal sealed class Scene3D
    {
        public enum Mode { Orbit, Pad, Chase }
        public Mode View = Mode.Orbit;
        /// <summary>Readout numbers, refreshed every frame for whoever draws the telemetry beside the view.</summary>
        public int Aircraft, Vessels, Inside, Inbound, Notams, Notmars;

        private const double D2R = Math.PI / 180, TrackCapKm = 25, TrafficKm = 220, StaleMin = 20, ModelSize = 0.014;
        private static readonly Color Air = Color.FromArgb(180, 184, 188), Ship = Color.FromArgb(111, 227, 111), Jolly = Color.FromArgb(60, 220, 90), Notam = Color.FromArgb(255, 210, 31), Notmar = Color.FromArgb(255, 90, 54);

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int mapTex, radarTex; private MapImage mapSrc, radarSrc; private Clouds cloudSrc; private int[] cloudTex, volTex; private RadarVolume volSrc;
        private double last, camAz = 215, dist = 105, tx, ty, tz, ex, ey, ez, elOff, zoom = 1;
        private DateTime held = DateTime.MinValue;

        /// <summary>Turn the view by hand. It stays where you leave it for a minute, then drifts on.</summary>
        public void Drag(int dx, int dy) { camAz -= dx * 0.3; elOff = Math.Max(-18, Math.Min(70, elOff + dy * 0.2)); held = DateTime.UtcNow.AddSeconds(60); }
        public void Wheel(int delta) { zoom = Math.Max(0.15, Math.Min(8, zoom * Math.Pow(0.88, delta / 120.0))); }
        public void ResetView() { elOff = 0; zoom = 1; held = DateTime.MinValue; }
        private double[] mvp; private int W, H, vs = 1; private Launch l;
        private readonly Dictionary<string, double[]> shown = new Dictionary<string, double[]>();

        private sealed class Label { public int Tex, W, H, TW, TH; }
        private struct Tag { public string Text; public double X, Y, Z; public Color C; public float Size; }
        private readonly Dictionary<string, Label> labels = new Dictionary<string, Label>();
        private readonly List<Tag> tags = new List<Tag>();

        // ---------------------------------------------------------------- geometry helpers

        private void World(double lat, double lon, out double x, out double z)
        {
            x = Flight.R * (lon - l.Lon) * D2R * Math.Cos(l.Lat * D2R); z = -Flight.R * (lat - l.Lat) * D2R;
        }

        private void FlightPos(double t, out double x, out double y, out double z)
        {
            double la, lo; Flight.Destination(l.Lat, l.Lon, l.Heading, Flight.Downrange(t), out la, out lo);
            World(la, lo, out x, out z); y = Flight.Alt(t) * vs;
        }

        private static void Line(double x1, double y1, double z1, double x2, double y2, double z2) { GL.glVertex3d(x1, y1, z1); GL.glVertex3d(x2, y2, z2); }

        private void Text(string text, double x, double y, double z, Color c, float size = 13) { tags.Add(new Tag { Text = text, X = x, Y = y, Z = z, C = c, Size = size }); }

        // ---------------------------------------------------------------- frame

        public void Draw(int w, int h)
        {
            W = w; H = h; l = Store.Current; vs = Settings.VerticalScale;
            var th = Theme.Cur;
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.25, now - last); last = now;
            GL.glViewport(0, 0, w, h);
            GL.glClearColor(0.022f, 0.026f, 0.034f, 1);
            GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
            if (l == null || !l.HasPad) return;
            Store.TrafficWanted = DateTime.UtcNow;
            double? t = l.Net.HasValue ? (DateTime.UtcNow - l.Net.Value).TotalSeconds : (double?)null;
            bool flying = t.HasValue && t >= 0 && t <= Flight.End;

            Camera(t, flying, dt);
            GL.glEnable(GL.BLEND); GL.glBlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            GL.glEnable(GL.LINE_SMOOTH); GL.glHint(GL.LINE_SMOOTH_HINT, GL.NICEST);
            GL.glDisable(GL.CULL_FACE);
            tags.Clear();

            Ground(th);
            Areas(th, t);
            GL.glEnable(GL.DEPTH_TEST); GL.glDepthFunc(GL.LEQUAL); GL.glDepthMask(1);
            if (Settings.Aircraft || Settings.Vessels) Traffic(t, dt, now); else Aircraft = Vessels = Inside = Inbound = 0;
            GL.glDepthMask(0); GL.glDisable(GL.DEPTH_TEST);
            if (Settings.Clouds) CloudVolume();
            if (Settings.RadarVolume) RadarSlices();
            if (Settings.Wind) Wind(th);
            Corridor(th, t, flying);
            GL.glDepthMask(1);
            Labels();
        }

        private void Camera(double? t, bool flying, double dt)
        {
            bool hold = DateTime.UtcNow < held;
            double wantDist, el, hx = Math.Sin(l.Heading * D2R), hz = -Math.Cos(l.Heading * D2R), gx, gy, gz;
            if (View == Mode.Chase && flying)
            {
                // follow the vehicle, pulling back as it speeds up so the view always shows where it is going
                var s = Flight.At(t.Value, l, null, null);
                FlightPos(t.Value, out gx, out gy, out gz);
                wantDist = 14 + 42 * s.V + 0.35 * s.Alt; el = 12;
                double want = l.Heading + 118 + 12 * Math.Sin(t.Value / 25);
                if (!hold) camAz += (((want - camAz) % 360 + 540) % 360 - 180) * Math.Min(1, dt * 1.5);
            }
            else if (t.HasValue && t > Flight.End && t < 900)
            {
                FlightPos(260, out gx, out gy, out gz); gy = 70; wantDist = 620; el = 24; if (!hold) camAz += dt * 1.5;
            }
            else if (View == Mode.Orbit)
            {
                gx = hx * 18; gy = 5; gz = hz * 18; wantDist = 105; el = 20; if (!hold) camAz += dt * 2.5;
            }
            else { gx = hx * 6; gy = 4; gz = hz * 6; wantDist = 55; el = 16; if (!hold) camAz += dt * 2.0; }

            el = Math.Max(2, Math.Min(88, el + elOff)); wantDist *= zoom;
            double k = Math.Min(1, dt * (zoom != 1 ? 6 : 1.2));
            if (last < 0.5 || dt <= 0) k = 1;
            dist += (wantDist - dist) * k; tx += (gx - tx) * Math.Min(1, k * 2.5); ty += (gy - ty) * Math.Min(1, k * 2.5); tz += (gz - tz) * Math.Min(1, k * 2.5);
            if (View == Mode.Chase && flying) { tx = gx; ty = gy; tz = gz; }
            double a = camAz * D2R, e = el * D2R;
            ex = tx + dist * Math.Cos(e) * Math.Sin(a); ey = ty + dist * Math.Sin(e); ez = tz - dist * Math.Cos(e) * Math.Cos(a);
            var proj = Mat.Perspective(42, (double)W / H, Math.Max(0.05, dist * 0.02), 9000);
            var view = Mat.LookAt(ex, ey, ez, tx, ty, tz);
            mvp = Mat.Mul(proj, view);
            GL.glMatrixMode(GL.PROJECTION); GL.glLoadMatrixd(proj);
            GL.glMatrixMode(GL.MODELVIEW); GL.glLoadMatrixd(view);
        }

        // ---------------------------------------------------------------- ground, map and radar

        private static byte EdgeFade(int x, int y, int n, double scale, byte a)
        {
            int d = Math.Min(Math.Min(x, y), Math.Min(n - 1 - x, n - 1 - y));
            return (byte)(a * scale * Math.Min(1, d / (n * 0.07)));
        }

        private void Ground(Theme th)
        {
            var map = Store.Map; var radar = Store.Radar;
            if (map != mapSrc) { GL.Free(ref mapTex); mapSrc = map; if (map != null) mapTex = GL.Texture(map.Image, 2048, 2048, (x, y, a) => EdgeFade(x, y, 2048, 1, 255)); }
            if (radar != radarSrc) { GL.Free(ref radarTex); radarSrc = radar; if (radar != null) radarTex = GL.Texture(radar.Image, 1024, 1024, (x, y, a) => EdgeFade(x, y, 1024, 0.8, a)); }

            GL.glDisable(GL.DEPTH_TEST); GL.glDepthMask(0);
            GL.glColor4f(0.075f, 0.08f, 0.088f, 1);
            Quad(4000, -0.05, false);
            if (mapTex != 0 && mapSrc.Lat == l.Lat && mapSrc.Lon == l.Lon) { GL.glBindTexture(GL.TEXTURE_2D, mapTex); GL.glColor4f(1, 1, 1, 1); Quad(mapSrc.HalfKm, 0, true); }
            if (radarTex != 0 && radarSrc.Lat == l.Lat && radarSrc.Lon == l.Lon) { GL.glBindTexture(GL.TEXTURE_2D, radarTex); GL.glColor4f(1, 1, 1, 1); Quad(radarSrc.HalfKm, 0.05, true); }

            // range rings, so distances read at a glance
            GL.glLineWidth(1);
            foreach (var r in new[] { 50, 100, 200 })
            {
                GL.glColor4f(1, 1, 1, 0.10f);
                GL.glBegin(GL.LINE_LOOP);
                for (int i = 0; i < 96; i++) GL.glVertex3d(r * Math.Sin(i * Math.PI / 48), 0.1, -r * Math.Cos(i * Math.PI / 48));
                GL.glEnd();
                Text(r + " km", 0, 0.1, -r, Color.FromArgb(120, 130, 140), 10);
            }
            Text(l.PadName ?? "Pad", 0, 0.6, 0, th.Ink, 13);
        }

        private static void Quad(double half, double y, bool textured)
        {
            if (textured) GL.glEnable(GL.TEXTURE_2D);
            GL.glBegin(GL.QUADS);
            GL.glTexCoord2d(0, 0); GL.glVertex3d(-half, y, -half); // texture row 0 is north
            GL.glTexCoord2d(1, 0); GL.glVertex3d(half, y, -half);
            GL.glTexCoord2d(1, 1); GL.glVertex3d(half, y, half);
            GL.glTexCoord2d(0, 1); GL.glVertex3d(-half, y, half);
            GL.glEnd();
            if (textured) GL.glDisable(GL.TEXTURE_2D);
        }

        // ---------------------------------------------------------------- cloud volume

        private void CloudVolume()
        {
            var c = Store.Cloud;
            if (c != cloudSrc)
            {
                if (cloudTex != null) for (int i = 0; i < cloudTex.Length; i++) GL.Free(ref cloudTex[i]);
                cloudSrc = c; cloudTex = null;
                if (c != null)
                {
                    const int S = 32; int N = Clouds.N;
                    cloudTex = new int[Clouds.NZ];
                    for (int k = 0; k < Clouds.NZ; k++)
                    {
                        var px = new byte[S * S * 4];
                        for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
                        {
                            // bilinear from the forecast grid; row 0 of the texture is the northern edge
                            double fx = x / (S - 1.0) * (N - 1), fy = (1 - y / (S - 1.0)) * (N - 1);
                            int i0 = Math.Min(N - 2, (int)fx), j0 = Math.Min(N - 2, (int)fy); double u = fx - i0, v = fy - j0;
                            Func<int, int, double> g = (i, j) => c.Cover[k * N * N + j * N + i];
                            double cover = (g(i0, j0) * (1 - u) + g(i0 + 1, j0) * u) * (1 - v) + (g(i0, j0 + 1) * (1 - u) + g(i0 + 1, j0 + 1) * u) * v;
                            int edge = Math.Min(Math.Min(x, y), Math.Min(S - 1 - x, S - 1 - y));
                            double a = Math.Pow(Math.Max(0, cover), 1.4) * 0.34 * Math.Min(1, edge / 4.0);
                            int o = (y * S + x) * 4;
                            px[o] = 228; px[o + 1] = 234; px[o + 2] = 242; px[o + 3] = (byte)(255 * Math.Min(1, a));
                        }
                        cloudTex[k] = GL.Texture(px, S, S);
                    }
                }
            }
            if (cloudTex == null || cloudSrc.Lat != l.Lat || cloudSrc.Lon != l.Lon) return;
            // furthest layer first, so nearer cloud covers what is behind it
            var order = new List<int>();
            for (int k = 0; k < Clouds.NZ; k++) order.Add(k);
            Func<int, double> height = k => (k + 0.5) / Clouds.NZ * Clouds.Top * vs;
            order.Sort((a, b) => Math.Abs(height(b) - ey).CompareTo(Math.Abs(height(a) - ey)));
            GL.glColor4f(1, 1, 1, 1);
            foreach (int k in order) { GL.glBindTexture(GL.TEXTURE_2D, cloudTex[k]); Quad(Clouds.Half, height(k), true); }
        }

        // ---------------------------------------------------------------- radar volume

        /// <summary>Reflectivity colour: faint blue for light echoes through green, yellow and red to magenta.</summary>
        private static void DbzColor(double dbz, out byte r, out byte g, out byte b, out byte a)
        {
            double[][] stops = { new[] { 5.0, 70, 130, 200 }, new[] { 18.0, 40, 170, 230 }, new[] { 28.0, 40, 200, 70 }, new[] { 38.0, 240, 220, 40 }, new[] { 48.0, 250, 130, 30 }, new[] { 56.0, 235, 40, 40 }, new[] { 66.0, 230, 60, 220 } };
            int i = 0; while (i < stops.Length - 2 && dbz > stops[i + 1][0]) i++;
            double f = Math.Max(0, Math.Min(1, (dbz - stops[i][0]) / (stops[i + 1][0] - stops[i][0])));
            r = (byte)(stops[i][1] + (stops[i + 1][1] - stops[i][1]) * f); g = (byte)(stops[i][2] + (stops[i + 1][2] - stops[i][2]) * f); b = (byte)(stops[i][3] + (stops[i + 1][3] - stops[i][3]) * f);
            a = (byte)(255 * Math.Max(0, Math.Min(0.6, 0.05 + (dbz - 5) / 45 * 0.55)));
        }

        private void RadarSlices()
        {
            var v = Store.Volume;
            if (v != volSrc)
            {
                if (volTex != null) for (int i = 0; i < volTex.Length; i++) GL.Free(ref volTex[i]);
                volSrc = v; volTex = null;
                if (v != null)
                {
                    int n = RadarVolume.N; volTex = new int[RadarVolume.NZ];
                    for (int k = 0; k < RadarVolume.NZ; k++)
                    {
                        var px = new byte[n * n * 4]; bool any = false;
                        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                        {
                            byte raw = v.Data[x + n * ((n - 1 - y) + n * k)]; // row 0 of the texture is the northern edge
                            if (raw == 0) continue;
                            double dbz = raw / 2.0 - 32; if (dbz < 5) continue;
                            int o = (y * n + x) * 4; DbzColor(dbz, out px[o], out px[o + 1], out px[o + 2], out px[o + 3]); any = true;
                        }
                        volTex[k] = any ? GL.Texture(px, n, n) : 0;
                    }
                }
            }
            if (volTex == null || volSrc.Lat != l.Lat || volSrc.Lon != l.Lon) return;
            var order = new List<int>();
            for (int k = 0; k < RadarVolume.NZ; k++) if (volTex[k] != 0) order.Add(k);
            Func<int, double> height = k => (k + 0.5) / RadarVolume.NZ * RadarVolume.Top * vs;
            order.Sort((a, b) => Math.Abs(height(b) - ey).CompareTo(Math.Abs(height(a) - ey)));
            GL.glColor4f(1, 1, 1, 1);
            foreach (int k in order) { GL.glBindTexture(GL.TEXTURE_2D, volTex[k]); Quad(RadarVolume.Half, height(k), true); }
        }

        // ---------------------------------------------------------------- wind

        /// <summary>The forecast wind at each level as an arrow pointing downwind, with its speed. Off unless asked for.</summary>
        private void Wind(Theme th)
        {
            var w = Store.Weather; if (w == null || w.Lat != l.Lat) return;
            var c = Color.FromArgb(127, 178, 245); string src;
            var levels = Winds.For(l, out src); double lastY = -9;
            // a column beside the pad, clear of the corridor
            double ox = Math.Sin((l.Heading - 90) * D2R) * 14, oz = -Math.Cos((l.Heading - 90) * D2R) * 14;
            GL.glLineWidth(2); GL.Color(c, 0.9);
            foreach (var p in levels)
            {
                if (double.IsNaN(p.Kt) || p.Z > 17000 || p.Z / 1000 * vs - lastY < 0.9 * vs) continue; // about one arrow a kilometre
                lastY = p.Z / 1000 * vs;
                double y = p.Z / 1000 * vs, len = 1.5 + p.Kt * 0.12, dx = -Math.Sin(p.Dir * D2R), dz = Math.Cos(p.Dir * D2R); // downwind
                double x2 = ox + dx * len, z2 = oz + dz * len, hx = -dz, hz = dx;
                GL.glBegin(GL.LINES);
                Line(ox, y, oz, x2, y, z2);
                Line(x2, y, z2, x2 - dx * 1.2 + hx * 0.6, y, z2 - dz * 1.2 + hz * 0.6); Line(x2, y, z2, x2 - dx * 1.2 - hx * 0.6, y, z2 - dz * 1.2 - hz * 0.6);
                GL.glEnd();
                Text(p.Kt.ToString("0") + " kt", x2, y, z2, c, 10);
            }
            GL.Color(c, 0.3); GL.glLineWidth(1);
            GL.glBegin(GL.LINES); Line(ox, 0, oz, ox, 17 * vs, oz); GL.glEnd();
        }

        // ---------------------------------------------------------------- corridor

        private void Corridor(Theme th, double? t, bool flying)
        {
            var c = th.Track; double x, y, z, px = 0, py = 0, pz = 0;
            // the corridor itself: rings around the nominal path, widening downrange
            GL.glLineWidth(1);
            var ring = new List<double[]>();
            for (double s = 0; s <= Flight.End; s += s < 200 ? 10 : 30)
            {
                FlightPos(s, out x, out y, out z);
                double x2, y2, z2; FlightPos(s + 1, out x2, out y2, out z2);
                double dx = x2 - x, dy = y2 - y, dz = z2 - z, dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dl < 1e-6) { dx = 0; dy = 1; dz = 0; dl = 1; }
                dx /= dl; dy /= dl; dz /= dl;
                // two unit vectors across the direction of travel
                double ax = -dz, ay = 0, az = dx, al = Math.Sqrt(ax * ax + az * az);
                if (al < 1e-6) { ax = 1; az = 0; al = 1; }
                ax /= al; az /= al;
                double bx = dy * az - dz * ay, by = dz * ax - dx * az, bz = dx * ay - dy * ax;
                double r = 0.6 + 0.045 * Flight.Downrange(s) + 0.05 * Flight.Alt(s);
                var pts = new double[16 * 3];
                for (int i = 0; i < 16; i++)
                {
                    double ca = Math.Cos(i * Math.PI / 8) * r, sa = Math.Sin(i * Math.PI / 8) * r;
                    pts[i * 3] = x + ax * ca + bx * sa; pts[i * 3 + 1] = Math.Max(0, y + ay * ca + by * sa); pts[i * 3 + 2] = z + az * ca + bz * sa;
                }
                ring.Add(pts);
            }
            for (int n = 0; n < ring.Count; n++)
            {
                GL.Color(c, 0.22);
                GL.glBegin(GL.LINE_LOOP);
                for (int i = 0; i < 16; i++) GL.glVertex3d(ring[n][i * 3], ring[n][i * 3 + 1], ring[n][i * 3 + 2]);
                GL.glEnd();
                if (n == 0) continue;
                GL.Color(c, 0.045);
                GL.glBegin(GL.TRIANGLE_STRIP);
                for (int i = 0; i <= 16; i++)
                {
                    int j = (i % 16) * 3;
                    GL.glVertex3d(ring[n - 1][j], ring[n - 1][j + 1], ring[n - 1][j + 2]); GL.glVertex3d(ring[n][j], ring[n][j + 1], ring[n][j + 2]);
                }
                GL.glEnd();
            }

            // the nominal path: bright where it has been flown
            GL.glLineWidth(2.5f);
            GL.glBegin(GL.LINE_STRIP);
            for (double s = 0; s <= Flight.End; s += 2)
            {
                FlightPos(s, out x, out y, out z);
                GL.Color(flying && s <= t.Value ? Color.White : c, flying && s <= t.Value ? 1 : 0.9);
                GL.glVertex3d(x, y, z);
            }
            GL.glEnd();
            // ground track under it
            GL.glLineWidth(1); GL.Color(c, 0.35);
            GL.glBegin(GL.LINE_STRIP);
            for (double s = 0; s <= Flight.End; s += 5) { FlightPos(s, out x, out y, out z); GL.glVertex3d(x, 0.12, z); }
            GL.glEnd();

            foreach (var e in Flight.Events)
            {
                if (e.T <= 0) continue;
                FlightPos(e.T, out x, out y, out z);
                Text(e.Name + "  " + Flight.Alt(e.T).ToString("0") + " km", x, y, z, th.Ink2, 11);
                GL.glPointSize(5); GL.Color(c, 1);
                GL.glBegin(GL.POINTS); GL.glVertex3d(x, y, z); GL.glEnd();
            }

            // the vehicle
            double vt = flying ? t.Value : 0;
            FlightPos(vt, out px, out py, out pz);
            GL.glEnable(GL.POINT_SMOOTH);
            GL.glLineWidth(1); GL.glColor4f(1, 1, 1, 0.5f);
            GL.glBegin(GL.LINES); Line(px, py, pz, px, 0, pz); GL.glEnd();
            double pulse = 0.5 + 0.5 * Math.Sin(clock.Elapsed.TotalSeconds * 5);
            GL.glPointSize((float)(H / 1080.0 * (flying ? 16 + 6 * pulse : 10))); GL.Color(c, 0.35);
            GL.glBegin(GL.POINTS); GL.glVertex3d(px, py, pz); GL.glEnd();
            GL.glPointSize((float)(H / 1080.0 * 7)); GL.glColor4f(1, 1, 1, 1);
            GL.glBegin(GL.POINTS); GL.glVertex3d(px, py, pz); GL.glEnd();
            GL.glDisable(GL.POINT_SMOOTH);
        }

        // ---------------------------------------------------------------- hazard areas

        private List<Area> live = new List<Area>();

        private void Areas(Theme th, double? t)
        {
            var now = DateTime.UtcNow; live = new List<Area>(); Notams = Notmars = 0;
            foreach (var a in Settings.Hazards ? Store.Areas : new List<Area>())
            {
                if (a.To.HasValue && a.To.Value < now) continue;
                if (Flight.KmBetween(l.Lat, l.Lon, a.Rings[0][0][0], a.Rings[0][0][1]) > 1500) continue;
                bool active = !a.From.HasValue || (a.From.Value - now).TotalHours < 2;
                if (active) live.Add(a);
                bool sea = a.Kind == "notmar"; if (sea) Notmars++; else Notams++;
                var c = sea ? Notmar : Notam; double top = sea ? 0 : Math.Min(20, a.CeilFt * 0.0003048) * vs;
                foreach (var ring in a.Rings)
                {
                    GL.glLineWidth(active ? 2 : 1); GL.Color(c, active ? 0.9 : 0.4);
                    GL.glBegin(GL.LINE_LOOP);
                    foreach (var p in ring) { double x, z; World(p[0], p[1], out x, out z); GL.glVertex3d(x, 0.15, z); }
                    GL.glEnd();
                    if (top <= 0) continue;
                    GL.glBegin(GL.LINE_LOOP);
                    foreach (var p in ring) { double x, z; World(p[0], p[1], out x, out z); GL.glVertex3d(x, top, z); }
                    GL.glEnd();
                    GL.Color(c, active ? 0.07 : 0.03);
                    GL.glBegin(GL.TRIANGLE_STRIP);
                    for (int i = 0; i <= ring.Length; i++) { double x, z; var p = ring[i % ring.Length]; World(p[0], p[1], out x, out z); GL.glVertex3d(x, 0.15, z); GL.glVertex3d(x, top, z); }
                    GL.glEnd();
                }
                double lx, lz; World(a.Rings[0][0][0], a.Rings[0][0][1], out lx, out lz);
                Text((sea ? "NOTMAR " : "NOTAM ") + a.Id, lx, Math.Max(0.3, top), lz, c, 10);
            }

            // the corridor's footprint on the surface, which the traffic alerts use
            GL.glLineWidth(1); GL.Color(th.Track, 0.35);
            foreach (int side in new[] { -1, 1 })
            {
                GL.glBegin(GL.LINE_STRIP);
                for (double d = 0; d <= Flight.CorridorKm; d += 25)
                {
                    double la, lo, la2, lo2, x, z;
                    Flight.Destination(l.Lat, l.Lon, l.Heading, d, out la, out lo);
                    Flight.Destination(la, lo, l.Heading + 90 * side, Flight.CorridorHalf(d), out la2, out lo2);
                    World(la2, lo2, out x, out z); GL.glVertex3d(x, 0.12, z);
                }
                GL.glEnd();
            }
        }

        private bool InArea(List<Area> areas, double lat, double lon, double altFt, DateTime at)
        {
            foreach (var a in areas)
            {
                if (a.From.HasValue && a.From.Value > at.AddHours(2)) continue;
                if (a.To.HasValue && a.To.Value < at) continue;
                if (a.Kind != "notmar" && a.CeilFt > 0 && (altFt < a.FloorFt || altFt > a.CeilFt)) continue;
                foreach (var r in a.Rings) if (Flight.InRing(lat, lon, r)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- aircraft

        private struct Tri { public float Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz, Shade; }
        private static Dictionary<string, Tri[]> models; private static Tri[] rotor;

        private static void Box(List<Tri> o, double cx, double cy, double cz, double sx, double sy, double sz)
        {
            double x0 = cx - sx / 2, x1 = cx + sx / 2, y0 = cy - sy / 2, y1 = cy + sy / 2, z0 = cz - sz / 2, z1 = cz + sz / 2;
            Face(o, x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1, 1.0);   // top
            Face(o, x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1, 0.45);  // bottom
            Face(o, x0, y0, z0, x0, y1, z0, x0, y1, z1, x0, y0, z1, 0.72);  // left
            Face(o, x1, y0, z0, x1, y1, z0, x1, y1, z1, x1, y0, z1, 0.82);  // right
            Face(o, x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0, 0.9);   // front
            Face(o, x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, 0.6);   // back
        }
        private static void Face(List<Tri> o, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, double dx, double dy, double dz, double shade)
        {
            o.Add(new Tri { Ax = (float)ax, Ay = (float)ay, Az = (float)az, Bx = (float)bx, By = (float)by, Bz = (float)bz, Cx = (float)cx, Cy = (float)cy, Cz = (float)cz, Shade = (float)shade });
            o.Add(new Tri { Ax = (float)ax, Ay = (float)ay, Az = (float)az, Bx = (float)cx, By = (float)cy, Bz = (float)cz, Cx = (float)dx, Cy = (float)dy, Cz = (float)dz, Shade = (float)shade });
        }
        /// <summary>A pair of mirrored flat surfaces (wings, tailplanes): root chord from z0 to z1, tip chord from z2 to z3.</summary>
        private static void Wings(List<Tri> o, double root, double span, double y, double z0, double z1, double z2, double z3, double dihedral = 0)
        {
            foreach (int s in new[] { -1, 1 })
                Face(o, s * root, y, z0, s * span, y + dihedral, z2, s * span, y + dihedral, z3, s * root, y, z1, 0.95);
        }

        private static void Models()
        {
            if (models != null) return;
            models = new Dictionary<string, Tri[]>();
            var m = new List<Tri>(); // light: straight high wing, propeller
            Box(m, 0, 0, 0, 0.2, 0.22, 1.5); Box(m, 0, 0, -0.85, 0.14, 0.14, 0.2); Box(m, 0, 0.14, -0.2, 2.3, 0.035, 0.36);
            Box(m, 0, 0.02, 0.68, 0.8, 0.03, 0.22); Box(m, 0, 0.2, 0.7, 0.035, 0.36, 0.26); Box(m, 0, 0, -0.97, 0.56, 0.03, 0.03);
            models["light"] = m.ToArray();
            m = new List<Tri>(); // airliner: swept wings, two engines under them
            Box(m, 0, 0, 0, 0.24, 0.24, 1.9); Box(m, 0, 0, -1.03, 0.16, 0.16, 0.16);
            Wings(m, 0.12, 1.15, -0.04, -0.3, 0.18, 0.28, 0.5, 0.06); Wings(m, 0.1, 0.46, 0.04, 0.7, 0.95, 0.9, 1.02);
            Box(m, 0, 0.26, 0.8, 0.035, 0.42, 0.3); Box(m, -0.42, -0.14, -0.08, 0.13, 0.13, 0.36); Box(m, 0.42, -0.14, -0.08, 0.13, 0.13, 0.36);
            models["jet"] = m.ToArray();
            m = new List<Tri>(); // heavy: longer, four engines
            Box(m, 0, 0, 0, 0.27, 0.27, 2.1); Box(m, 0, 0, -1.13, 0.18, 0.18, 0.16);
            Wings(m, 0.13, 1.3, -0.04, -0.35, 0.22, 0.32, 0.56, 0.07); Wings(m, 0.1, 0.52, 0.04, 0.78, 1.04, 0.98, 1.1);
            Box(m, 0, 0.29, 0.88, 0.035, 0.46, 0.32);
            foreach (double x in new[] { -0.8, -0.42, 0.42, 0.8 }) Box(m, x, -0.14, Math.Abs(x) > 0.5 ? 0.12 : -0.08, 0.12, 0.12, 0.34);
            models["heavy"] = m.ToArray();
            m = new List<Tri>(); // business jet: engines at the tail, T-tail
            Box(m, 0, 0, 0, 0.2, 0.2, 1.6); Box(m, 0, 0, -0.87, 0.13, 0.13, 0.14);
            Wings(m, 0.1, 0.95, -0.05, -0.1, 0.25, 0.3, 0.45, 0.05); Box(m, 0, 0.24, 0.66, 0.035, 0.4, 0.26); Wings(m, 0.0, 0.4, 0.44, 0.6, 0.8, 0.74, 0.84);
            Box(m, -0.2, 0.06, 0.42, 0.11, 0.11, 0.3); Box(m, 0.2, 0.06, 0.42, 0.11, 0.11, 0.3);
            models["bizjet"] = m.ToArray();
            m = new List<Tri>(); // helicopter body; the rotor is drawn separately so it can turn
            Box(m, 0, 0, -0.2, 0.34, 0.36, 0.8); Box(m, 0, -0.02, -0.66, 0.24, 0.24, 0.14); Box(m, 0, 0.06, 0.62, 0.08, 0.08, 0.95);
            Box(m, 0, 0.18, 1.06, 0.03, 0.3, 0.14); Box(m, 0, 0.22, -0.2, 0.06, 0.1, 0.06); Box(m, -0.18, -0.24, -0.2, 0.03, 0.03, 0.8); Box(m, 0.18, -0.24, -0.2, 0.03, 0.03, 0.8);
            models["heli"] = m.ToArray();
            m = new List<Tri>(); Box(m, 0, 0.29, -0.2, 2.1, 0.02, 0.1); Box(m, 0, 0.29, -0.2, 0.1, 0.02, 2.1);
            rotor = m.ToArray();
            m = new List<Tri>(); // small boat: hull and cabin
            Box(m, 0, 0.1, 0, 0.5, 0.2, 1.5); Box(m, 0, 0.1, -0.85, 0.3, 0.2, 0.25); Box(m, 0, 0.32, 0.15, 0.36, 0.24, 0.5);
            models["boat"] = m.ToArray();
            m = new List<Tri>(); // cargo ship: bridge at the stern, containers forward of it
            Box(m, 0, 0.11, 0, 0.5, 0.22, 2.0); Box(m, 0, 0.42, 0.78, 0.44, 0.4, 0.3); Box(m, 0, 0.32, -0.15, 0.4, 0.2, 1.3);
            models["cargo"] = m.ToArray();
            m = new List<Tri>(); // tanker: long flat deck with a pipe run, bridge at the stern
            Box(m, 0, 0.1, 0, 0.56, 0.2, 2.1); Box(m, 0, 0.4, 0.82, 0.46, 0.4, 0.3); Box(m, 0, 0.24, -0.15, 0.08, 0.08, 1.4);
            models["tanker"] = m.ToArray();
            m = new List<Tri>(); // passenger ship: tall superstructure and a funnel
            Box(m, 0, 0.12, 0, 0.5, 0.24, 2.0); Box(m, 0, 0.38, 0.05, 0.42, 0.28, 1.4); Box(m, 0, 0.6, 0.3, 0.14, 0.2, 0.2);
            models["liner"] = m.ToArray();
            m = new List<Tri>(); // sailing boat: mast and sail
            Box(m, 0, 0.07, 0, 0.3, 0.14, 1.2); Box(m, 0, 0.7, -0.1, 0.03, 1.2, 0.03);
            Face(m, 0, 0.2, -0.08, 0, 1.25, -0.08, 0, 0.2, 0.55, 0, 0.2, 0.55, 0.95);
            models["sail"] = m.ToArray();
        }

        private static void Model(Tri[] tris, double x, double y, double z, double heading, double s, Color c, double spin = double.NaN)
        {
            double a = heading * D2R, ca = Math.Cos(a), sa = Math.Sin(a);
            if (!double.IsNaN(spin))
            {
                // turn about the rotor mast, which sits a little ahead of the model's origin
                double cs = Math.Cos(spin), sn = Math.Sin(spin);
                var o = new Tri[tris.Length];
                for (int i = 0; i < tris.Length; i++)
                {
                    var q = tris[i]; var r = q; double zz;
                    zz = q.Az + 0.2; r.Ax = (float)(q.Ax * cs - zz * sn); r.Az = (float)(q.Ax * sn + zz * cs - 0.2);
                    zz = q.Bz + 0.2; r.Bx = (float)(q.Bx * cs - zz * sn); r.Bz = (float)(q.Bx * sn + zz * cs - 0.2);
                    zz = q.Cz + 0.2; r.Cx = (float)(q.Cx * cs - zz * sn); r.Cz = (float)(q.Cx * sn + zz * cs - 0.2);
                    o[i] = r;
                }
                tris = o;
            }
            foreach (var q in tris)
            {
                GL.glColor4f(c.R / 255f * q.Shade, c.G / 255f * q.Shade, c.B / 255f * q.Shade, 1);
                GL.glVertex3d(x + (q.Ax * ca - q.Az * sa) * s, y + q.Ay * s, z + (q.Ax * sa + q.Az * ca) * s);
                GL.glVertex3d(x + (q.Bx * ca - q.Bz * sa) * s, y + q.By * s, z + (q.Bx * sa + q.Bz * ca) * s);
                GL.glVertex3d(x + (q.Cx * ca - q.Cz * sa) * s, y + q.Cy * s, z + (q.Cx * sa + q.Cz * ca) * s);
            }
        }

        private void Traffic(double? t, double dt, double now)
        {
            Models();
            var planes = new List<Plane>(); double unix = Store.Unix(DateTime.UtcNow);
            if (Settings.Aircraft) planes.AddRange(Store.Planes);
            if (Settings.Vessels) planes.AddRange(Store.Vessels);
            double? toT0 = t.HasValue ? -t.Value / 60 : (double?)null; // minutes until T-0
            bool corridorHot = toT0.HasValue && toT0 <= 120 && toT0 >= -15;
            var t0 = l.Net ?? DateTime.UtcNow;
            bool flash = ((int)(now * 4) & 1) == 0;
            Aircraft = Vessels = Inside = Inbound = 0;
            var seen = new HashSet<string>();
            var lines = new List<double[]>(); // x1 y1 z1 x2 y2 z2 r g b a

            GL.glBegin(GL.TRIANGLES);
            foreach (var v in planes)
            {
                double age = unix - v.T;
                if (v.Ground || age > (v.Sea ? 3600 : StaleMin * 60) || Flight.KmBetween(l.Lat, l.Lon, v.Lat, v.Lon) > TrafficKm) continue;
                // aircraft are kept to those near the launch airspace, so the picture is not buried in airline traffic
                if (!v.Sea && Settings.AircraftMiles > 0 && Flight.KmToNotam(l, v.Lat, v.Lon, Store.Areas) > Settings.AircraftMiles * 1.609344) continue;
                double kmPerMin = v.Kt * 1.852 / 60, la, lo, x, z;
                Flight.Destination(v.Lat, v.Lon, v.Track, kmPerMin * age / 60, out la, out lo);
                double altFt = v.Sea ? 0 : Math.Max(0, v.AltFt + v.Fpm * age / 60), y = v.Sea ? 0.04 : altFt * 0.0003048 * vs;
                World(la, lo, out x, out z);

                // ease towards a corrected position instead of jumping when a new report arrives
                double[] p;
                if (shown.TryGetValue(v.Id, out p) && Math.Abs(p[0] - x) + Math.Abs(p[2] - z) < 30)
                {
                    double k = 1 - Math.Exp(-dt * 4);
                    p[0] += (x - p[0]) * k; p[1] += (y - p[1]) * k; p[2] += (z - p[2]) * k; x = p[0]; y = p[1]; z = p[2];
                }
                else shown[v.Id] = new[] { x, y, z };
                seen.Add(v.Id);

                // a straight-line forecast only means something a short way ahead: three hours for a ship, twenty minutes for an aircraft
                bool forecast = toT0.HasValue && toT0 > 0 && toT0 <= (v.Sea ? 180 : 20), cleared = Flight.IsJolly(v);
                int state = 0;
                if (cleared) state = 0;
                else if (InArea(live, la, lo, altFt, DateTime.UtcNow) || (corridorHot && Flight.InCorridor(l, la, lo))) state = 2;
                else if (forecast)
                {
                    double fla, flo; Flight.Destination(la, lo, v.Track, kmPerMin * toT0.Value, out fla, out flo);
                    if (Flight.InCorridor(l, fla, flo) || InArea(Store.Areas, fla, flo, Math.Max(0, altFt + v.Fpm * toT0.Value), t0)) state = 1;
                }
                if (v.Sea) Vessels++; else Aircraft++; if (state == 2) Inside++; else if (state == 1) Inbound++;
                Color c = state == 2 ? (flash ? Color.FromArgb(255, 40, 40) : Color.FromArgb(255, 220, 30)) : state == 1 ? (flash ? Color.FromArgb(255, 40, 40) : Color.FromArgb(110, 20, 20))   : v.Sea ? Ship : cleared ? Jolly : Air;

                string kind = v.Sea ? Flight.VesselKind(v) : Flight.AircraftKind(v);
                double camD = Math.Sqrt((x - ex) * (x - ex) + (y - ey) * (y - ey) + (z - ez) * (z - ez));
                double s = ModelSize * camD * (kind == "heavy" || kind == "cargo" || kind == "tanker" || kind == "liner" ? 1.5 : kind == "jet" ? 1.15 : kind == "light" || kind == "sail" ? 0.8 : 0.85) * (state > 0 ? 1.4 : 1);
                Model(models[kind], x, y, z, v.Track, s, c);
                if (kind == "heli") Model(rotor, x, y, z, v.Track, s, c, now * 14);

                // height line, and the projected track: where it will be in ten minutes, kept short for fast aircraft
                if (!v.Sea) lines.Add(new[] { x, y, z, x, 0, z, c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.28 });
                double horizon = kmPerMin > 0.05 ? Math.Min(10, TrackCapKm / kmPerMin) : 0;
                if (horizon > 0)
                {
                    double hx = Math.Sin(v.Track * D2R), hz = -Math.Cos(v.Track * D2R), len = kmPerMin * horizon, climb = v.Fpm * horizon * 0.0003048 * vs;
                    double x2 = x + hx * len, z2 = z + hz * len, y2 = Math.Max(0, y + climb);
                    lines.Add(new[] { x, y, z, x2, y2, z2, c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.75 });
                    double tick = s * 0.9, pxv = -hz, pzv = hx; // across the track
                    double fx = horizon >= 5 ? 5 / horizon : 1; // a tick at five minutes, or at the end of a short track
                    double mx = x + (x2 - x) * fx, my = y + (y2 - y) * fx, mz = z + (z2 - z) * fx;
                    lines.Add(new[] { mx - pxv * tick, my, mz - pzv * tick, mx + pxv * tick, my, mz + pzv * tick, c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.9 });
                    if (horizon >= 10)
                    {
                        // an X at ten minutes
                        double d = tick * 0.75;
                        lines.Add(new[] { x2 - (pxv + hx) * d, y2, z2 - (pzv + hz) * d, x2 + (pxv + hx) * d, y2, z2 + (pzv + hz) * d, c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.9 });
                        lines.Add(new[] { x2 - (pxv - hx) * d, y2, z2 - (pzv - hz) * d, x2 + (pxv - hx) * d, y2, z2 + (pzv - hz) * d, c.R / 255.0, c.G / 255.0, c.B / 255.0, 0.9 });
                    }
                }
                if (state > 0) Text(v.Name + (state == 2 ? "  INSIDE" : "  INBOUND"), x, y + s * 1.2, z, c, 11);
            }
            GL.glEnd();

            GL.glLineWidth(1);
            GL.glBegin(GL.LINES);
            foreach (var q in lines) { GL.glColor4f((float)q[6], (float)q[7], (float)q[8], (float)q[9]); Line(q[0], q[1], q[2], q[3], q[4], q[5]); }
            GL.glEnd();

            if (shown.Count > seen.Count + 200)
                foreach (var id in new List<string>(shown.Keys)) if (!seen.Contains(id)) shown.Remove(id);
        }

        // ---------------------------------------------------------------- text

        private static int Pot(int v) { int p = 1; while (p < v) p <<= 1; return p; }

        private Label MakeLabel(string text, Color c, float px)
        {
            using (var f = new Font(Theme.Cur.Font, px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                Size sz;
                using (var tmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(tmp)) sz = Size.Ceiling(g.MeasureString(text, f));
                int w = sz.Width + 6, h = sz.Height + 4, tw = Pot(w), th = Pot(h);
                using (var b = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent);
                        g.TextRenderingHint = TextRenderingHint.AntiAlias; g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var dark = new SolidBrush(Color.FromArgb(215, 0, 0, 0)))
                            foreach (var o in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1), new Point(1, 1), new Point(-1, -1) }) g.DrawString(text, f, dark, 3 + o.X, 2 + o.Y);
                        using (var ink = new SolidBrush(c)) g.DrawString(text, f, ink, 3, 2);
                    }
                    return new Label { Tex = GL.Texture(b, tw, th), W = w, H = h, TW = tw, TH = th };
                }
            }
        }

        private void Labels()
        {
            GL.glMatrixMode(GL.PROJECTION); GL.glLoadIdentity(); GL.glOrtho(0, W, H, 0, -1, 1);
            GL.glMatrixMode(GL.MODELVIEW); GL.glLoadIdentity();
            GL.glDisable(GL.DEPTH_TEST); GL.glEnable(GL.TEXTURE_2D); GL.glColor4f(1, 1, 1, 1);
            if (labels.Count > 400) { foreach (var o in labels.Values) { int id = o.Tex; GL.Free(ref id); } labels.Clear(); }
            float scale = Math.Max(0.6f, H / 1080f);
            foreach (var tg in tags)
            {
                double sx, sy;
                if (!Mat.Project(mvp, tg.X, tg.Y, tg.Z, W, H, out sx, out sy) || sx < -200 || sx > W + 200 || sy < -50 || sy > H + 50) continue;
                float px = (float)Math.Round(tg.Size * 1.25f * scale);
                string key = tg.Text + "|" + tg.C.ToArgb() + "|" + px;
                Label lb;
                if (!labels.TryGetValue(key, out lb)) labels[key] = lb = MakeLabel(tg.Text, tg.C, px);
                double x0 = Math.Round(sx + 6 * scale), y0 = Math.Round(sy - lb.H / 2.0);
                GL.glBindTexture(GL.TEXTURE_2D, lb.Tex);
                GL.glBegin(GL.QUADS);
                GL.glTexCoord2d(0, 0); GL.glVertex2d(x0, y0);
                GL.glTexCoord2d(1, 0); GL.glVertex2d(x0 + lb.TW, y0);
                GL.glTexCoord2d(1, 1); GL.glVertex2d(x0 + lb.TW, y0 + lb.TH);
                GL.glTexCoord2d(0, 1); GL.glVertex2d(x0, y0 + lb.TH);
                GL.glEnd();
            }
            GL.glDisable(GL.TEXTURE_2D);
        }
    }
}

// Volumetric radar: download the newest NEXRAD Level II volume for the radar nearest the pad,
// decode every reflectivity sweep and resample the tilts onto a 3D grid around the pad.
//
// Archive II layout: 24-byte volume header, then records, each a 4-byte big-endian length followed
// by a bzip2 stream. Inside, every message has a 12-byte pad and a 16-byte header; type 31 (digital
// radar data) is variable length, all other types sit in 2432-byte frames.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LaunchCountdownSaver
{
    /// <summary>bzip2 decompression (Windows has no built-in one).</summary>
    internal static class Bz2
    {
        private sealed class Bits
        {
            private readonly byte[] d; private int pos, end; private uint buf; private int n;
            public Bits(byte[] data, int start, int length) { d = data; pos = start; end = start + length; }
            public int Read(int count)
            {
                int r = 0;
                while (count > 0)
                {
                    if (n == 0) { if (pos >= end) throw new EndOfStreamException(); buf = d[pos++]; n = 8; }
                    int take = Math.Min(n, count);
                    r = (r << take) | (int)((buf >> (n - take)) & ((1u << take) - 1));
                    n -= take; count -= take;
                }
                return r;
            }
        }

        public static byte[] Decompress(byte[] data, int start, int length)
        {
            var b = new Bits(data, start, length);
            if (b.Read(8) != 'B' || b.Read(8) != 'Z' || b.Read(8) != 'h') throw new InvalidDataException("not bzip2");
            int blockSize = (b.Read(8) - '0') * 100000;
            var o = new MemoryStream(length * 4);
            var tt = new int[blockSize]; var block = new byte[blockSize];
            while (true)
            {
                int m1 = b.Read(24), m2 = b.Read(24);
                if (m1 == 0x177245 && m2 == 0x385090) break; // end of stream
                if (m1 != 0x314159 || m2 != 0x265359) throw new InvalidDataException("bad block");
                b.Read(32); // block checksum
                if (b.Read(1) != 0) throw new InvalidDataException("randomised blocks are not supported");
                int origPtr = b.Read(24);

                // which byte values occur
                var seq = new byte[256]; int nInUse = 0, used = b.Read(16);
                for (int i = 0; i < 16; i++)
                    if ((used & (0x8000 >> i)) != 0) { int bits = b.Read(16); for (int j = 0; j < 16; j++) if ((bits & (0x8000 >> j)) != 0) seq[nInUse++] = (byte)(i * 16 + j); }
                int alpha = nInUse + 2, nGroups = b.Read(3), nSel = b.Read(15);
                var sel = new byte[nSel]; var mtf = new byte[6];
                for (int i = 0; i < nGroups; i++) mtf[i] = (byte)i;
                for (int i = 0; i < nSel; i++)
                {
                    int j = 0; while (b.Read(1) == 1) j++;
                    byte v = mtf[j]; for (; j > 0; j--) mtf[j] = mtf[j - 1]; mtf[0] = v; sel[i] = v;
                }

                // Huffman tables
                var limit = new int[nGroups][]; var bas = new int[nGroups][]; var perm = new int[nGroups][]; var minLen = new int[nGroups];
                for (int t = 0; t < nGroups; t++)
                {
                    var len = new int[alpha]; int cur = b.Read(5);
                    for (int i = 0; i < alpha; i++) { while (b.Read(1) == 1) cur += b.Read(1) == 0 ? 1 : -1; len[i] = cur; }
                    int lo = len.Min(), hi = len.Max(); minLen[t] = lo;
                    limit[t] = new int[hi + 2]; bas[t] = new int[hi + 2]; perm[t] = new int[alpha];
                    int pp = 0;
                    for (int l = lo; l <= hi; l++) for (int i = 0; i < alpha; i++) if (len[i] == l) perm[t][pp++] = i;
                    var count = new int[hi + 2];
                    for (int i = 0; i < alpha; i++) count[len[i]]++;
                    int code = 0, idx = 0;
                    for (int l = lo; l <= hi; l++) { bas[t][l] = idx - code; code += count[l]; idx += count[l]; limit[t][l] = code - 1; code <<= 1; }
                }

                // symbols: run lengths, move-to-front
                var counts = new int[256]; var order = new byte[256];
                for (int i = 0; i < 256; i++) order[i] = (byte)i;
                int n = 0, run = 0, runBit = 1, group = -1, left = 0, tbl = 0, eob = alpha - 1;
                while (true)
                {
                    if (left == 0) { group++; left = 50; tbl = sel[group]; }
                    left--;
                    int l = minLen[tbl], code = b.Read(l);
                    while (code > limit[tbl][l]) { l++; code = (code << 1) | b.Read(1); }
                    int sym = perm[tbl][code + bas[tbl][l]];
                    if (sym <= 1) { run += runBit << sym; runBit <<= 1; continue; } // RUNA adds 1, RUNB adds 2, doubling each time
                    if (run > 0)
                    {
                        byte v = seq[order[0]];
                        if (n + run > blockSize) throw new InvalidDataException("block overflow");
                        counts[v] += run; while (run-- > 0) block[n++] = v;
                        run = 0; runBit = 1;
                    }
                    if (sym == eob) break;
                    int j = sym - 1; byte mv = order[j];
                    for (; j > 0; j--) order[j] = order[j - 1];
                    order[0] = mv;
                    byte c = seq[mv];
                    if (n >= blockSize) throw new InvalidDataException("block overflow");
                    counts[c]++; block[n++] = c;
                }

                // undo the Burrows-Wheeler transform
                int sum = 0; var cf = new int[256];
                for (int i = 0; i < 256; i++) { cf[i] = sum; sum += counts[i]; }
                for (int i = 0; i < n; i++) tt[cf[block[i]]++] = i;
                // and the initial run-length coding: four equal bytes are followed by a repeat count
                int p = tt[origPtr], same = 0, prev = -1;
                for (int i = 0; i < n; i++)
                {
                    byte c = block[p]; p = tt[p];
                    if (same == 4) { for (int k = 0; k < c; k++) o.WriteByte((byte)prev); same = 0; prev = -1; continue; }
                    if (c == prev) same++; else { same = 1; prev = c; }
                    o.WriteByte(c);
                }
            }
            return o.ToArray();
        }
    }

    internal sealed class RadarVolume
    {
        public const int N = 128, NZ = 36; public const double Half = 150, Top = 18;
        public byte[] Data; // 0 = no echo, otherwise dBZ = value / 2 - 32; index i + N * (j + N * k), j south to north
        public string Id, Key; public DateTime Time; public double Lat, Lon, MaxDbz, MaxKm, EchoTop; public int Tilts;
    }

    internal static partial class Store
    {
        public static volatile RadarVolume Volume;
        public static volatile string VolumeNote = "radar volume loading…";

        // WSR-88D radars near launch sites: id, latitude, longitude
        private static readonly object[][] Radars =
        {
            new object[] { "KMLB", 28.113, -80.654 }, new object[] { "KVBX", 34.838, -120.398 }, new object[] { "KBRO", 25.916, -97.419 }, new object[] { "KDOX", 38.826, -75.440 },
            new object[] { "KAKQ", 36.984, -77.007 }, new object[] { "KHDX", 33.077, -106.120 }, new object[] { "KEPZ", 31.873, -106.698 }, new object[] { "KMAF", 31.943, -102.189 },
        };
        private const string Bucket = "https://unidata-nexrad-level2.s3.amazonaws.com";
        private const double RE = 6371.0 * 4 / 3, Missing = -32; // effective earth radius for standard refraction; "no echo"

        private static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }
        private static float F32(byte[] b, int o) { return BitConverter.ToSingle(new[] { b[o + 3], b[o + 2], b[o + 1], b[o] }, 0); }

        private sealed class Radial { public float Az, El, First, Step, Scale, Offset; public int ElNum, Gates, At; public byte[] Rec; }
        private sealed class Sweep { public double El; public List<Radial> Rs; public int[] Look; }

        private static void LoadVolume()
        {
            var c = Current; if (c == null || !c.HasPad) return;
            if (!Settings.RadarVolume) { Volume = null; VolumeNote = "radar volume off"; return; }
            var near = Radars.OrderBy(r => Flight.KmBetween(c.Lat, c.Lon, (double)r[1], (double)r[2])).First();
            if (Flight.KmBetween(c.Lat, c.Lon, (double)near[1], (double)near[2]) > 230) { Volume = null; VolumeNote = "no NEXRAD radar covers this launch site"; return; }
            string key = null;
            foreach (int back in new[] { 0, 1 }) // around 00Z the newest volume is still under yesterday's prefix
            {
                var d = DateTime.UtcNow.AddDays(-back);
                var xml = Get(Bucket + "/?list-type=2&prefix=" + d.ToString("yyyy/MM/dd") + "/" + near[0] + "/");
                var keys = Regex.Matches(xml, "<Key>([^<]+)</Key>").Cast<Match>().Select(m => m.Groups[1].Value).Where(k => !k.EndsWith("_MDM")).ToList();
                if (keys.Count > 0) { key = keys[keys.Count - 1]; break; }
            }
            if (key == null) throw new Exception("no volumes listed");
            var old = Volume;
            if (old != null && old.Key == key && old.Lat == c.Lat && old.Lon == c.Lon) return;
            if (old == null) VolumeNote = "radar volume downloading and decoding…";
            var buf = Bytes(Bucket + "/" + key);
            if (buf.Length < 24 || buf[0] != 'A' || buf[1] != 'R' || buf[2] != '2' || buf[3] != 'V') throw new Exception("not a Level II volume");
            var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(U32(buf, 12) - 1).AddMilliseconds(U32(buf, 16));

            // every record is its own bzip2 stream
            var radials = new List<Radial>(); double sLat = double.NaN, sLon = 0, sAlt = 0;
            for (int o = 24; o + 4 <= buf.Length;)
            {
                int len = Math.Abs((int)U32(buf, o)); o += 4;
                if (len == 0 || o + len > buf.Length) break;
                var rec = Bz2.Decompress(buf, o, len); o += len;
                for (int q = 0; q + 28 <= rec.Length;)
                {
                    int size = U16(rec, q + 12) * 2, type = rec[q + 15];
                    if (type != 31) { q += 2432; continue; }
                    if (size == 0) break;
                    int h = q + 28; // start of the message 31 data header
                    if (h + 68 > rec.Length) break;
                    float az = F32(rec, h + 12), el = F32(rec, h + 24); int elNum = rec[h + 22], blocks = U16(rec, h + 30);
                    for (int b = 0; b < blocks && b < 10; b++)
                    {
                        int p = h + (int)U32(rec, h + 32 + b * 4);
                        if (p + 28 > rec.Length || p < 0) continue;
                        if (rec[p] == 'R' && rec[p + 1] == 'V' && rec[p + 2] == 'O' && rec[p + 3] == 'L' && double.IsNaN(sLat))
                        { sLat = F32(rec, p + 8); sLon = F32(rec, p + 12); sAlt = ((short)U16(rec, p + 16) + U16(rec, p + 18)) / 1000.0; }
                        if (!(rec[p] == 'D' && rec[p + 1] == 'R' && rec[p + 2] == 'E' && rec[p + 3] == 'F')) continue;
                        int gates = U16(rec, p + 8);
                        if (rec[p + 19] != 8 || p + 28 + gates > rec.Length) continue;
                        radials.Add(new Radial { Az = az, El = el, ElNum = elNum, Gates = gates, First = U16(rec, p + 10) / 1000f, Step = U16(rec, p + 12) / 1000f, Scale = F32(rec, p + 20), Offset = F32(rec, p + 24), Rec = rec, At = p + 28 });
                    }
                    q += 12 + size;
                }
            }
            if (double.IsNaN(sLat) || radials.Count == 0) throw new Exception("no reflectivity data in volume");

            // group radials into sweeps, keeping the newest sweep at each elevation angle (the lowest is repeated)
            var byAngle = new SortedDictionary<int, Sweep>();
            foreach (var grp in radials.GroupBy(r => r.ElNum))
            {
                var rs = grp.ToList(); if (rs.Count < 180) continue;
                var look = Enumerable.Repeat(-1, 720).ToArray(); // half-degree azimuth lookup, gaps filled from the nearest radial
                for (int i = 0; i < rs.Count; i++) look[(int)Math.Floor(((rs[i].Az % 360) + 360) % 360 * 2) % 720] = i;
                for (int pass = 0; pass < 3; pass++) for (int k = 0; k < 720; k++) if (look[k] < 0) look[k] = look[(k + 719) % 720] >= 0 ? look[(k + 719) % 720] : look[(k + 1) % 720];
                double el = rs.Average(r => r.El);
                byAngle[(int)Math.Round(el * 10)] = new Sweep { El = el, Rs = rs, Look = look };
            }
            var sweeps = byAngle.Values.ToList();
            if (sweeps.Count == 0) throw new Exception("no complete sweeps in volume");
            var els = sweeps.Select(s => s.El).ToArray();

            Func<Sweep, int, double, double> sample = (s, azIdx, range) =>
            {
                int ri = s.Look[azIdx]; if (ri < 0) return Missing;
                var r = s.Rs[ri]; int g = (int)Math.Round((range - r.First) / r.Step);
                if (g < 0 || g >= r.Gates) return Missing;
                int raw = r.Rec[r.At + g];
                return raw < 2 ? Missing : (raw - r.Offset) / r.Scale; // 0 = below threshold, 1 = range folded
            };

            // resample: each cell finds the two tilts that bracket its elevation angle and blends between them
            int n = RadarVolume.N, nz = RadarVolume.NZ; double half = RadarVolume.Half, top = RadarVolume.Top, kd = Math.PI / 180;
            double dx = (c.Lon - sLon) * kd * 6371 * Math.Cos(sLat * kd), dy = (c.Lat - sLat) * kd * 6371; // the pad relative to the radar
            var vol = new RadarVolume { Data = new byte[n * n * nz], Id = (string)near[0], Key = key, Time = time, Lat = c.Lat, Lon = c.Lon, Tilts = sweeps.Count, MaxDbz = -99 };
            const double Beam = 0.6; double lo = els[0] - Beam, hi = els[els.Length - 1] + Beam;
            for (int j = 0; j < n; j++)
            {
                double y = dy - half + (j + 0.5) / n * 2 * half;
                for (int i = 0; i < n; i++)
                {
                    double x = dx - half + (i + 0.5) / n * 2 * half, s = Math.Sqrt(x * x + y * y);
                    if (s < 1 || s > 460) continue;
                    int azIdx = (int)Math.Floor(((Math.Atan2(x, y) / kd + 360) % 360) * 2) % 720;
                    double th = s / RE, cosT = Math.Cos(th), sinT = Math.Sin(th);
                    for (int k = 0; k < nz; k++)
                    {
                        double z = (k + 0.5) / nz * top - sAlt, R = RE + z, range = Math.Sqrt(RE * RE + R * R - 2 * RE * R * cosT);
                        double el = Math.Atan2(R * cosT - RE, R * sinT) / kd;
                        if (el < lo || el > hi) continue;
                        int a = 0; while (a < els.Length - 1 && els[a + 1] <= el) a++;
                        double v;
                        if (el <= els[0] || a == els.Length - 1) v = sample(sweeps[a], azIdx, range);
                        else
                        {
                            double f = (el - els[a]) / (els[a + 1] - els[a]), v0 = sample(sweeps[a], azIdx, range), v1 = sample(sweeps[a + 1], azIdx, range);
                            v = v0 + (v1 - v0) * f;
                            // between tilts more than a beam apart, an echo on only one side fades out instead of smearing across
                            if ((v0 == Missing) != (v1 == Missing) && Math.Abs(f - (v0 == Missing ? 1 : 0)) * (els[a + 1] - els[a]) > Beam) v = Missing;
                        }
                        if (v <= -31) continue;
                        vol.Data[i + n * (j + n * k)] = (byte)Math.Max(1, Math.Min(255, Math.Round((v + 32) * 2)));
                        double zk = (k + 0.5) / nz * top;
                        if (v > vol.MaxDbz) { vol.MaxDbz = v; vol.MaxKm = zk; }
                        if (v >= 18) vol.EchoTop = Math.Max(vol.EchoTop, zk);
                    }
                }
            }
            Volume = vol; VolumeNote = "";
        }
    }
}

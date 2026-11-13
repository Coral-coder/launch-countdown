// Web Worker: download a NEXRAD Level II volume, decode every reflectivity sweep and resample
// the tilts onto a Cartesian grid for volume rendering.
//
// Archive II layout: 24-byte volume header, then LDM records, each a 4-byte big-endian length
// followed by a bzip2 stream. Inside, every message has a 12-byte CTM pad and a 16-byte header;
// type 31 (digital radar data) is variable length, all other types sit in 2432-byte frames.

/* global bz2 */
// bz2 (SheetJS, MIT) attaches itself to `window`, which a worker has to provide.
if (typeof importScripts === 'function') { self.window = self; importScripts('https://unpkg.com/bz2@1.0.1/index.js'); }

const RE = 6371 * 4 / 3; // effective earth radius for standard refraction, km
const MISSING = -32;     // dBZ stand-in for "no echo" when blending tilts

function decompress(buf, inflate) {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength), out = [];
  let o = 24;
  while (o + 4 <= buf.length) {
    const len = Math.abs(dv.getInt32(o)); o += 4;
    if (!len || o + len > buf.length) break;
    out.push(inflate(buf.subarray(o, o + len)));
    o += len;
  }
  return out;
}

/** Pull the reflectivity moment out of every type-31 radial. */
function readRadials(records) {
  const radials = [];
  let site = null;
  for (const rec of records) {
    const dv = new DataView(rec.buffer, rec.byteOffset, rec.byteLength);
    let o = 0;
    while (o + 28 <= rec.length) {
      const size = dv.getUint16(o + 12) * 2, type = rec[o + 15];
      if (type !== 31) { o += 2432; continue; }
      if (!size) break;
      const h = o + 28; // start of the message 31 data header
      if (h + 68 > rec.length) break;
      const az = dv.getFloat32(h + 12), elNum = rec[h + 22], el = dv.getFloat32(h + 24), nBlocks = dv.getUint16(h + 30);
      for (let b = 0; b < nBlocks && b < 10; b++) {
        const p = h + dv.getUint32(h + 32 + b * 4);
        if (p + 28 > rec.length) continue;
        const name = String.fromCharCode(rec[p], rec[p + 1], rec[p + 2], rec[p + 3]);
        if (name === 'RVOL' && !site) site = { lat: dv.getFloat32(p + 8), lon: dv.getFloat32(p + 12), alt: (dv.getInt16(p + 16) + dv.getUint16(p + 18)) / 1000, vcp: dv.getUint16(p + 40) };
        if (name !== 'DREF') continue;
        const gates = dv.getUint16(p + 8), first = dv.getUint16(p + 10) / 1000, step = dv.getUint16(p + 12) / 1000;
        const word = rec[p + 19], scale = dv.getFloat32(p + 20), offset = dv.getFloat32(p + 24);
        if (word !== 8 || p + 28 + gates > rec.length) continue;
        radials.push({ az, el, elNum, gates, first, step, scale, offset, data: rec.subarray(p + 28, p + 28 + gates) });
      }
      o += 12 + size;
    }
  }
  return { radials, site };
}

/** Group radials into sweeps, keeping the newest sweep at each elevation angle (SAILS repeats the lowest). */
function buildSweeps(radials) {
  const byNum = new Map();
  for (const r of radials) { if (!byNum.has(r.elNum)) byNum.set(r.elNum, []); byNum.get(r.elNum).push(r); }
  const byAngle = new Map();
  for (const rs of byNum.values()) {
    if (rs.length < 180) continue;
    const el = rs.reduce((a, r) => a + r.el, 0) / rs.length, gates = Math.max(...rs.map((r) => r.gates));
    // half-degree azimuth lookup, gaps filled from the nearest radial
    const look = new Int16Array(720).fill(-1);
    rs.forEach((r, i) => { look[Math.floor(((r.az % 360) + 360) % 360 * 2) % 720] = i; });
    for (let pass = 0; pass < 3; pass++) for (let k = 0; k < 720; k++) if (look[k] < 0) look[k] = look[(k + 719) % 720] >= 0 ? look[(k + 719) % 720] : look[(k + 1) % 720];
    byAngle.set(Math.round(el * 10), { el, sin: Math.sin(el * Math.PI / 180), cos: Math.cos(el * Math.PI / 180), rs, look, gates, first: rs[0].first, step: rs[0].step, scale: rs[0].scale, offset: rs[0].offset });
  }
  return [...byAngle.values()].sort((a, b) => a.el - b.el);
}

function sample(s, azIdx, range) {
  const r = s.rs[s.look[azIdx]];
  if (!r) return MISSING;
  const g = Math.round((range - r.first) / r.step);
  if (g < 0 || g >= r.gates) return MISSING;
  const raw = r.data[g];
  return raw < 2 ? MISSING : (raw - r.offset) / r.scale; // 0 = below threshold, 1 = range folded
}

/**
 * Resample onto a grid centred `dx` km east and `dy` km north of the radar.
 * Each voxel looks up the two tilts that bracket its elevation angle and blends between them.
 * Output bytes: 0 = no echo, otherwise dBZ = byte / 2 - 32.
 */
function grid(sweeps, site, g) {
  const { n, nz, half, top, dx, dy } = g, out = new Uint8Array(n * n * nz), els = sweeps.map((s) => s.el);
  const BEAM = 0.6, lo = els[0] - BEAM, hi = els[els.length - 1] + BEAM;
  let maxDbz = -99, maxKm = 0, echoTop = 0;
  for (let j = 0; j < n; j++) {
    const y = dy - half + (j + 0.5) / n * 2 * half;
    for (let i = 0; i < n; i++) {
      const x = dx - half + (i + 0.5) / n * 2 * half, s = Math.hypot(x, y);
      if (s < 1 || s > 460) continue;
      const azIdx = Math.floor(((Math.atan2(x, y) * 180 / Math.PI + 360) % 360) * 2) % 720;
      const th = s / RE, cosT = Math.cos(th), sinT = Math.sin(th);
      for (let k = 0; k < nz; k++) {
        const z = (k + 0.5) / nz * top - site.alt;
        // geometry on a 4/3 earth: slant range and elevation angle of the point as seen from the radar
        const R = RE + z, range = Math.sqrt(RE * RE + R * R - 2 * RE * R * cosT);
        const el = Math.atan2(R * cosT - RE, R * sinT) * 180 / Math.PI;
        if (el < lo || el > hi) continue;
        let a = 0; while (a < els.length - 1 && els[a + 1] <= el) a++;
        let v;
        if (el <= els[0] || a === els.length - 1) v = sample(sweeps[a], azIdx, range);
        else {
          const f = (el - els[a]) / (els[a + 1] - els[a]), v0 = sample(sweeps[a], azIdx, range), v1 = sample(sweeps[a + 1], azIdx, range);
          // between tilts more than a beam apart, an echo on only one side fades out instead of smearing across
          v = v0 + (v1 - v0) * f;
          if ((v0 === MISSING) !== (v1 === MISSING) && Math.abs(f - (v0 === MISSING ? 1 : 0)) * (els[a + 1] - els[a]) > BEAM) v = MISSING;
        }
        if (v <= -31) continue;
        out[i + n * (j + n * k)] = Math.max(1, Math.min(255, Math.round((v + 32) * 2)));
        if (v > maxDbz) { maxDbz = v; maxKm = (k + 0.5) / nz * top; }
        if (v >= 18) echoTop = Math.max(echoTop, (k + 0.5) / nz * top);
      }
    }
  }
  return { out, maxDbz, maxKm, echoTop };
}

function process(buf, g, inflate) {
  const head = String.fromCharCode(...buf.subarray(0, 24));
  if (!head.startsWith('AR2V')) throw new Error('not a Level II volume');
  const dv = new DataView(buf.buffer, buf.byteOffset, 24), time = (dv.getUint32(12) - 1) * 864e5 + dv.getUint32(16);
  const { radials, site } = readRadials(decompress(buf, inflate));
  if (!site || !radials.length) throw new Error('no reflectivity data in volume');
  const sweeps = buildSweeps(radials);
  if (!sweeps.length) throw new Error('no complete sweeps in volume');
  // where the grid centre (the pad) sits relative to the radar, km
  const k = Math.PI / 180;
  g.dx = (g.lon - site.lon) * k * 6371 * Math.cos(site.lat * k); g.dy = (g.lat - site.lat) * k * 6371;
  const r = grid(sweeps, site, g);
  return { data: r.out, n: g.n, nz: g.nz, time, site, tilts: sweeps.map((s) => +s.el.toFixed(1)), maxDbz: r.maxDbz, maxKm: r.maxKm, echoTop: r.echoTop, radials: radials.length };
}

if (typeof importScripts === 'function') {
  self.onmessage = async (e) => {
    const { url, grid: g, id } = e.data;
    try {
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const vol = process(new Uint8Array(await res.arrayBuffer()), g, (b) => bz2.decompress(b));
      self.postMessage({ id, ok: true, vol }, [vol.data.buffer]);
    } catch (err) { self.postMessage({ id, ok: false, error: String(err.message || err) }); }
  };
} else if (typeof module !== 'undefined') module.exports = { process };

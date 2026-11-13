// Shared helpers: DOM, formatting, fetch with feed status, geodesy.

export const $ = (id) => document.getElementById(id);

export function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v);
  }
  for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid);
  return el;
}

export const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export const num = (v, d = 0) => (v == null || Number.isNaN(+v) ? '—' : (+v).toFixed(d));
export const pad2 = (n) => String(n).padStart(2, '0');

const fmtUTC = new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'medium', hourCycle: 'h23' });
const fmtET = new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short' });
const fmtLocal = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short' });
export const utc = (ms) => (ms == null ? '—' : fmtUTC.format(ms) + ' UTC');
export const eastern = (ms) => (ms == null ? '—' : fmtET.format(ms));
export const local = (ms) => (ms == null ? '—' : fmtLocal.format(ms));
/** Wall-clock time in a named IANA zone (the pad's own time); falls back to UTC if the zone is unknown. */
export function zoned(ms, tz) {
  if (ms == null) return '—';
  try { return new Intl.DateTimeFormat('en-US', { timeZone: tz || 'UTC', weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short' }).format(ms); }
  catch { return utc(ms); }
}
export const hhmmZ = (ms) => { const d = new Date(ms); return `${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}Z`; };
export const dayHourZ = (ms) => { const d = new Date(ms); return `${d.getUTCMonth() + 1}/${d.getUTCDate()} ${pad2(d.getUTCHours())}Z`; };
export const parseT = (s) => { if (!s) return null; const t = Date.parse(String(s).replace(/(\.\d{3})\d+/, '$1')); return Number.isNaN(t) ? null : t; };

export function ago(ms, now = Date.now()) {
  if (ms == null) return '';
  const s = Math.round((now - ms) / 1000);
  if (s < 90) return `${s}s ago`;
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 172800) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

/** Split a signed duration into clock fields. */
export function parts(ms) {
  const a = Math.abs(ms);
  return { neg: ms < 0, d: Math.floor(a / 864e5), h: Math.floor(a / 36e5) % 24, m: Math.floor(a / 6e4) % 60, s: Math.floor(a / 1e3) % 60, ms: Math.floor(a % 1e3) };
}
export function dur(ms) {
  const p = parts(ms);
  return (p.d ? `${p.d}d ` : '') + `${pad2(p.h)}:${pad2(p.m)}:${pad2(p.s)}`;
}

const DIRS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
export const compass = (deg) => (deg == null ? '—' : DIRS[Math.round(((deg % 360) + 360) % 360 / 22.5) % 16]);
export const cToF = (c) => (c == null ? null : c * 9 / 5 + 32);
export const msToKt = (v) => (v == null ? null : v * 1.943844);
export const kmhToKt = (v) => (v == null ? null : v / 1.852);

// ---- feeds: every remote source reports its health into the Raw Data section
export const feeds = new Map(); // name -> {state, at, url, error, data}
const feedListeners = new Set();
export const onFeeds = (fn) => feedListeners.add(fn);
function setFeed(name, patch) {
  feeds.set(name, { ...(feeds.get(name) || {}), ...patch });
  feedListeners.forEach((fn) => fn());
}

// Open-Meteo sheds load when hit with a burst, so its calls go one at a time; every source gets retries.
let omQueue = Promise.resolve();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function fetchRetry(url, headers) {
  let last;
  for (let i = 0; i < 4; i++) {
    if (i) await sleep(1500 * 2 ** (i - 1));
    try {
      const r = await fetch(url, { headers, cache: 'no-store' });
      if (r.ok) return await r.json();
      last = new Error(`HTTP ${r.status}`);
      if (r.status < 500 && r.status !== 429) break;
    } catch (e) { last = e; } // network or CORS failure (error responses often lack CORS headers)
  }
  throw last;
}

export async function getJSON(name, url, { keep = true, headers } = {}) {
  setFeed(name, { state: feeds.get(name)?.data ? 'ok' : 'wait', url });
  try {
    let job;
    if (url.includes('api.open-meteo.com')) {
      job = omQueue.then(() => fetchRetry(url, headers));
      omQueue = job.catch(() => {}).then(() => sleep(250));
    } else job = fetchRetry(url, headers);
    const data = await job;
    setFeed(name, { state: 'ok', at: Date.now(), error: null, data: keep ? data : undefined });
    return data;
  } catch (e) {
    setFeed(name, { state: 'err', error: String(e.message || e) });
    throw e;
  }
}

// ---- geodesy (spherical earth is plenty for a ground-track sketch)
const R = 6371;
const rad = (d) => d * Math.PI / 180, deg = (r) => r * 180 / Math.PI;
export function destination(lat, lon, bearing, km) {
  const φ1 = rad(lat), λ1 = rad(lon), θ = rad(bearing), δ = km / R;
  const φ2 = Math.asin(Math.sin(φ1) * Math.cos(δ) + Math.cos(φ1) * Math.sin(δ) * Math.cos(θ));
  const λ2 = λ1 + Math.atan2(Math.sin(θ) * Math.sin(δ) * Math.cos(φ1), Math.cos(δ) - Math.sin(φ1) * Math.sin(φ2));
  return [deg(φ2), ((deg(λ2) + 540) % 360) - 180];
}
/** Orbit inclination reached by launching due `azimuth` from `lat` (non-rotating approximation). */
export const inclination = (lat, azimuth) => deg(Math.acos(Math.cos(rad(lat)) * Math.sin(rad(azimuth))));

/** Sun elevation (degrees) for a place and time; NOAA low-precision formula. */
export function sunElevation(lat, lon, ms) {
  const d = ms / 864e5 - 10957.5; // days since J2000
  const g = rad((357.529 + 0.98560028 * d) % 360), q = (280.459 + 0.98564736 * d) % 360;
  const L = rad(q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g));
  const e = rad(23.439 - 0.00000036 * d);
  const ra = Math.atan2(Math.cos(e) * Math.sin(L), Math.cos(L)), dec = Math.asin(Math.sin(e) * Math.sin(L));
  const gmst = (18.697374558 + 24.06570982441908 * d) % 24;
  const ha = rad(gmst * 15 + lon) - ra;
  return deg(Math.asin(Math.sin(rad(lat)) * Math.sin(dec) + Math.cos(rad(lat)) * Math.cos(dec) * Math.cos(ha)));
}

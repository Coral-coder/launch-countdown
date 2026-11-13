// Canvas charts: hourly time series, skew-T log-p diagram, hodograph.

import { h, $, dayHourZ, num, compass } from './util.js';

// Chart chrome follows the page theme: each colour is read from the matching CSS token when drawn.
const FALLBACK = { ink: '#f0efec', ink2: '#c3c2b7', muted: '#898781', grid: '#2c2c2a', axis: '#383835', surface: '#1a1a19', raised: '#232321', warn: '#fab219' };
let tokens = null;
export function refreshTheme() { tokens = null; }
const C = new Proxy({}, { get(_, k) {
  if (!tokens) { const cs = getComputedStyle(document.documentElement); tokens = {}; for (const n of Object.keys(FALLBACK)) tokens[n] = cs.getPropertyValue('--' + n).trim() || FALLBACK[n]; }
  return tokens[k];
} });
// Categorical slots (dark-surface steps), assigned in fixed order per chart.
export const SERIES = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#9085e9'];

function fit(canvas) {
  const dpr = window.devicePixelRatio || 1, r = canvas.getBoundingClientRect();
  const w = Math.max(200, Math.round(r.width)), ht = Math.max(120, Math.round(r.height));
  if (canvas.width !== w * dpr || canvas.height !== ht * dpr) { canvas.width = w * dpr; canvas.height = ht * dpr; }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, ht);
  return { ctx, w, ht };
}

function niceStep(span, target = 4) {
  const raw = span / target, mag = 10 ** Math.floor(Math.log10(raw)), n = raw / mag;
  return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
}

const tip = () => $('tip');
function showTip(html, x, y) {
  const t = tip();
  t.innerHTML = html; t.hidden = false;
  const r = t.getBoundingClientRect();
  t.style.left = Math.min(window.innerWidth - r.width - 8, x + 14) + 'px';
  t.style.top = Math.max(8, y - r.height - 10) + 'px';
}
const hideTip = () => { tip().hidden = true; };

/**
 * One measure, one y-axis, up to a handful of series.
 * spec: {title, unit, x:[ms], series:[{label, data:[]}], markers:[{x, label}], zero:boolean, digits}
 */
export function lineChart(spec) {
  const canvas = h('canvas');
  const multi = spec.series.length > 1;
  const root = h('div', { class: 'chart' },
    h('h3', {}, spec.title, ' ', h('span', { class: 'muted' }, spec.unit || '')),
    h('div', { class: 'lg' }, multi ? spec.series.map((s, i) => h('span', {}, h('i', { style: `background:${SERIES[i]}` }), s.label)) : null),
    canvas);
  let hover = null;

  const draw = () => {
    const { ctx, w, ht } = fit(canvas);
    const m = { l: 40, r: 10, t: 14, b: 22 };
    const xs = spec.x, x0 = xs[0], x1 = xs[xs.length - 1];
    const vals = spec.series.flatMap((s) => s.data).filter((v) => v != null);
    if (!vals.length || x1 === x0) { ctx.fillStyle = C.muted; ctx.font = '12px system-ui'; ctx.fillText('No data', m.l, ht / 2); return; }
    let lo = Math.min(...vals), hi = Math.max(...vals);
    if (spec.zero) lo = Math.min(0, lo);
    if (spec.max != null) hi = Math.max(hi, spec.max);
    if (hi === lo) hi = lo + 1;
    const step = niceStep(hi - lo); lo = Math.floor(lo / step) * step; hi = Math.ceil(hi / step) * step;
    const X = (t) => m.l + (t - x0) / (x1 - x0) * (w - m.l - m.r), Y = (v) => ht - m.b - (v - lo) / (hi - lo) * (ht - m.t - m.b);

    ctx.font = '11px system-ui'; ctx.lineWidth = 1; ctx.textBaseline = 'middle';
    for (let v = lo; v <= hi + step / 2; v += step) {
      ctx.strokeStyle = C.grid; ctx.beginPath(); ctx.moveTo(m.l, Y(v)); ctx.lineTo(w - m.r, Y(v)); ctx.stroke();
      ctx.fillStyle = C.muted; ctx.textAlign = 'right'; ctx.fillText(+v.toFixed(2), m.l - 6, Y(v));
    }
    // x ticks at UTC midnights (or every 6 h on short spans)
    const span = x1 - x0, tick = span > 4 * 864e5 ? 864e5 : span > 36 * 36e5 ? 12 * 36e5 : 6 * 36e5;
    ctx.textAlign = 'center'; ctx.textBaseline = 'top';
    for (let t = Math.ceil(x0 / tick) * tick; t <= x1; t += tick) {
      ctx.strokeStyle = C.grid; ctx.beginPath(); ctx.moveTo(X(t), m.t); ctx.lineTo(X(t), ht - m.b); ctx.stroke();
      ctx.fillStyle = C.muted; ctx.fillText(dayHourZ(t), X(t), ht - m.b + 5);
    }
    ctx.strokeStyle = C.axis; ctx.beginPath(); ctx.moveTo(m.l, ht - m.b); ctx.lineTo(w - m.r, ht - m.b); ctx.stroke();

    for (const mk of spec.markers || []) {
      if (mk.x < x0 || mk.x > x1) continue;
      ctx.strokeStyle = C.warn; ctx.setLineDash([4, 3]); ctx.beginPath(); ctx.moveTo(X(mk.x), m.t); ctx.lineTo(X(mk.x), ht - m.b); ctx.stroke(); ctx.setLineDash([]);
      ctx.fillStyle = C.ink2; ctx.textAlign = X(mk.x) > w - 50 ? 'right' : 'left'; ctx.textBaseline = 'top';
      ctx.fillText(mk.label, X(mk.x) + (ctx.textAlign === 'left' ? 4 : -4), 1);
    }

    spec.series.forEach((s, i) => {
      ctx.strokeStyle = SERIES[i]; ctx.lineWidth = 2; ctx.lineJoin = 'round'; ctx.beginPath();
      let pen = false;
      s.data.forEach((v, k) => { if (v == null) { pen = false; return; } pen ? ctx.lineTo(X(xs[k]), Y(v)) : ctx.moveTo(X(xs[k]), Y(v)); pen = true; });
      ctx.stroke();
    });

    if (hover != null) {
      ctx.strokeStyle = C.muted; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(X(xs[hover]), m.t); ctx.lineTo(X(xs[hover]), ht - m.b); ctx.stroke();
      spec.series.forEach((s, i) => {
        const v = s.data[hover]; if (v == null) return;
        ctx.fillStyle = SERIES[i]; ctx.strokeStyle = C.surface; ctx.lineWidth = 2;
        ctx.beginPath(); ctx.arc(X(xs[hover]), Y(v), 4, 0, 7); ctx.fill(); ctx.stroke();
      });
    }
    canvas._X = X;
  };

  canvas.addEventListener('pointermove', (e) => {
    const r = canvas.getBoundingClientRect(), px = e.clientX - r.left;
    const xs = spec.x, f = (px - 40) / (r.width - 50), t = xs[0] + f * (xs[xs.length - 1] - xs[0]);
    let best = 0; xs.forEach((x, i) => { if (Math.abs(x - t) < Math.abs(xs[best] - t)) best = i; });
    hover = best; draw();
    showTip(`<div class="muted">${dayHourZ(xs[best])}</div>` + spec.series.map((s, i) =>
      `<div><i style="background:${SERIES[i]}"></i>${multi ? s.label + ' ' : ''}<b>${num(s.data[best], spec.digits ?? 0)}</b> ${spec.unit || ''}</div>`).join(''), e.clientX, e.clientY);
  });
  canvas.addEventListener('pointerleave', () => { hover = null; hideTip(); draw(); });
  new ResizeObserver(draw).observe(canvas);
  return root;
}

// ---------------------------------------------------------------- sounding

const T_COL = '#e66767', TD_COL = '#199e70';
const P_BOT = 1050, P_TOP = 100, T_MIN = -40, T_MAX = 50;

/** profile: [{p, z, t, td, dir, kt}] ordered surface -> top. */
export function skewT(canvas, profile, title) {
  const W = 640, H = 620, dpr = window.devicePixelRatio || 1;
  canvas.width = W * dpr; canvas.height = H * dpr;
  const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
  const m = { l: 46, r: 96, t: 28, b: 30 }, pw = W - m.l - m.r, ph = H - m.t - m.b;
  const yOf = (p) => m.t + ph * (Math.log(p / P_TOP) / Math.log(P_BOT / P_TOP));
  const X = (t, p) => m.l + (t - T_MIN) / (T_MAX - T_MIN) * pw + (m.t + ph - yOf(p)) * 0.9; // 0.9 ≈ 42° skew

  ctx.save(); ctx.beginPath(); ctx.rect(m.l, m.t, pw, ph); ctx.clip();
  ctx.lineWidth = 1; ctx.font = '11px system-ui';
  for (let t = -120; t <= 50; t += 10) { // isotherms
    ctx.strokeStyle = t === 0 ? '#4a6f9c' : C.axis; ctx.beginPath(); ctx.moveTo(X(t, P_BOT), yOf(P_BOT)); ctx.lineTo(X(t, P_TOP), yOf(P_TOP)); ctx.stroke();
  }
  ctx.strokeStyle = '#3a3226'; // dry adiabats
  for (let th = -30; th <= 170; th += 20) {
    ctx.beginPath();
    for (let p = P_BOT; p >= P_TOP; p -= 25) { const t = (th + 273.15) * (p / 1000) ** 0.2857 - 273.15; p === P_BOT ? ctx.moveTo(X(t, p), yOf(p)) : ctx.lineTo(X(t, p), yOf(p)); }
    ctx.stroke();
  }
  const line = (key, color) => {
    ctx.strokeStyle = color; ctx.lineWidth = 2.5; ctx.lineJoin = 'round'; ctx.beginPath(); let pen = false;
    for (const l of profile) { if (l[key] == null || l.p < P_TOP) continue; pen ? ctx.lineTo(X(l[key], l.p), yOf(l.p)) : ctx.moveTo(X(l[key], l.p), yOf(l.p)); pen = true; }
    ctx.stroke();
  };
  line('td', TD_COL); line('t', T_COL);
  ctx.restore();

  ctx.textBaseline = 'middle'; ctx.font = '11px system-ui';
  for (const p of [1000, 925, 850, 700, 500, 400, 300, 250, 200, 150, 100]) {
    ctx.strokeStyle = C.axis; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(m.l, yOf(p)); ctx.lineTo(m.l + pw, yOf(p)); ctx.stroke();
    ctx.fillStyle = C.muted; ctx.textAlign = 'right'; ctx.fillText(p, m.l - 6, yOf(p));
  }
  ctx.textAlign = 'center'; ctx.textBaseline = 'top';
  for (let t = T_MIN; t <= T_MAX; t += 10) ctx.fillText(`${t}°`, X(t, P_BOT), m.t + ph + 6);
  ctx.strokeStyle = C.axis; ctx.strokeRect(m.l, m.t, pw, ph);

  // winds in the right margin: arrow pointing the way the wind blows, plus speed
  ctx.textAlign = 'left'; ctx.textBaseline = 'middle';
  let lastY = Infinity;
  for (const l of profile) {
    if (l.kt == null || l.dir == null || l.p < P_TOP || l.p > P_BOT) continue;
    const y = yOf(l.p); if (lastY - y < 15) continue; lastY = y;
    const cx = m.l + pw + 18, a = (l.dir + 180) * Math.PI / 180, len = 9;
    const dx = Math.sin(a) * len, dy = -Math.cos(a) * len;
    ctx.strokeStyle = C.ink2; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.moveTo(cx - dx, y - dy); ctx.lineTo(cx + dx, y + dy); ctx.stroke();
    ctx.beginPath(); ctx.arc(cx + dx, y + dy, 2.2, 0, 7); ctx.fillStyle = C.ink2; ctx.fill();
    ctx.fillStyle = l.kt >= 100 ? C.warn : C.ink2; ctx.fillText(`${Math.round(l.kt)} kt`, cx + 16, y);
  }
  // legend + title (ink for text, swatches carry the colour)
  ctx.textAlign = 'left'; ctx.textBaseline = 'middle'; ctx.font = '12px system-ui';
  ctx.fillStyle = C.ink; ctx.fillText(title || '', m.l, 13);
  const lx = m.l + pw - 190;
  [[T_COL, 'Temperature'], [TD_COL, 'Dew point']].forEach(([c, t], i) => {
    ctx.fillStyle = c; ctx.fillRect(lx + i * 100, 11, 14, 3); ctx.fillStyle = C.ink2; ctx.fillText(t, lx + i * 100 + 19, 13);
  });
  ctx.fillStyle = C.muted; ctx.fillText('hPa', 4, 13); ctx.fillText('wind', m.l + pw + 10, 13);
}

const HODO_BANDS = [[0, 3, '#3987e5', '0–3 km'], [3, 6, '#199e70', '3–6 km'], [6, 9, '#c98500', '6–9 km'], [9, 99, '#d55181', '9 km+']];

export function hodograph(canvas, profile) {
  const S = 320, dpr = window.devicePixelRatio || 1;
  canvas.width = S * dpr; canvas.height = S * dpr;
  const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, S, S);
  const pts = profile.filter((l) => l.kt != null && l.dir != null && l.z != null && l.z < 16000);
  const maxKt = Math.max(40, ...pts.map((l) => l.kt));
  const ring = maxKt > 120 ? 40 : 20, R = Math.ceil(maxKt / ring) * ring, c = S / 2, k = (S / 2 - 22) / R;
  ctx.font = '10px system-ui'; ctx.textAlign = 'left'; ctx.textBaseline = 'bottom';
  for (let r = ring; r <= R; r += ring) {
    ctx.strokeStyle = C.grid; ctx.lineWidth = 1; ctx.beginPath(); ctx.arc(c, c, r * k, 0, 7); ctx.stroke();
    ctx.fillStyle = C.muted; ctx.fillText(`${r}`, c + 3, c - r * k - 1);
  }
  ctx.strokeStyle = C.axis; ctx.beginPath(); ctx.moveTo(c, 8); ctx.lineTo(c, S - 8); ctx.moveTo(8, c); ctx.lineTo(S - 8, c); ctx.stroke();
  // u/v components: the vector the air moves along
  const xy = (l) => { const a = (l.dir + 180) * Math.PI / 180; return [c + Math.sin(a) * l.kt * k, c - Math.cos(a) * l.kt * k]; };
  ctx.lineWidth = 2; ctx.lineJoin = 'round';
  for (let i = 1; i < pts.length; i++) {
    const zkm = pts[i].z / 1000, band = HODO_BANDS.find((b) => zkm >= b[0] && zkm < b[1]) || HODO_BANDS[3];
    ctx.strokeStyle = band[2]; ctx.beginPath(); ctx.moveTo(...xy(pts[i - 1])); ctx.lineTo(...xy(pts[i])); ctx.stroke();
  }
  ctx.textBaseline = 'middle';
  HODO_BANDS.forEach((b, i) => { ctx.fillStyle = b[2]; ctx.fillRect(8, 12 + i * 14, 10, 3); ctx.fillStyle = C.ink2; ctx.fillText(b[3], 22, 13 + i * 14); });
  ctx.fillStyle = C.muted; ctx.textAlign = 'right'; ctx.fillText('hodograph · kt', S - 8, S - 10);
}

/** Derived numbers used by the stats tiles and the commit-criteria screen. */
export function soundingStats(profile) {
  const at = (key, target) => { // height where `key` first crosses `target` going up
    for (let i = 1; i < profile.length; i++) {
      const a = profile[i - 1], b = profile[i];
      if (a[key] == null || b[key] == null || a.z == null || b.z == null) continue;
      if ((a[key] - target) * (b[key] - target) <= 0 && a[key] !== b[key]) return a.z + (target - a[key]) / (b[key] - a[key]) * (b.z - a.z);
    }
    return null;
  };
  const winds = profile.filter((l) => l.kt != null && l.dir != null && l.z != null);
  const uv = (l) => { const a = l.dir * Math.PI / 180; return [-l.kt * Math.sin(a), -l.kt * Math.cos(a)]; };
  let maxWind = null, maxShear = null;
  winds.forEach((l, i) => {
    if (!maxWind || l.kt > maxWind.kt) maxWind = l;
    if (i && l.z - winds[i - 1].z > 150) {
      const [u1, v1] = uv(winds[i - 1]), [u2, v2] = uv(l);
      const per1000ft = Math.hypot(u2 - u1, v2 - v1) / ((l.z - winds[i - 1].z) * 3.28084 / 1000);
      if (!maxShear || per1000ft > maxShear.v) maxShear = { v: per1000ft, z: (l.z + winds[i - 1].z) / 2 };
    }
  });
  const sfc = winds[0], six = winds.find((l) => l.z - (sfc?.z ?? 0) >= 6000);
  const bulk = sfc && six ? Math.hypot(uv(six)[0] - uv(sfc)[0], uv(six)[1] - uv(sfc)[1]) : null;
  const cold = profile.filter((l) => l.t != null).reduce((a, b) => (!a || b.t < a.t ? b : a), null);
  return { maxWind, maxShear, bulk06: bulk, z0: at('t', 0), zm10: at('t', -10), zm20: at('t', -20), coldest: cold };
}

export function soundingTable(table, profile) {
  table.innerHTML = '';
  table.append(h('tr', {}, ['hPa', 'Height m', 'ft', 'Temp °C', 'Dew pt °C', 'Wind', 'kt'].map((t, i) => h('th', { class: i ? 'num' : '' }, t))));
  for (const l of profile) {
    if (l.t == null && l.kt == null) continue;
    table.append(h('tr', {}, h('td', {}, num(l.p)), h('td', { class: 'num' }, num(l.z)), h('td', { class: 'num' }, l.z == null ? '—' : Math.round(l.z * 3.28084).toLocaleString()),
      h('td', { class: 'num' }, num(l.t, 1)), h('td', { class: 'num' }, num(l.td, 1)),
      h('td', { class: 'num' }, l.dir == null ? '—' : `${Math.round(l.dir)}° ${compass(l.dir)}`), h('td', { class: 'num' }, num(l.kt))));
  }
}

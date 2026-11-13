// Weather: every live feed for the selected pad and T-0, the tiles/charts that show them,
// and the unofficial commit-criteria screen built on top.

import { $, h, num, getJSON, compass, cToF, kmhToKt, msToKt, utc, hhmmZ, dayHourZ, ago, destination, sunElevation, esc } from './util.js';
import { lineChart, skewT, hodograph, soundingStats, soundingTable } from './charts.js';
import { now } from './clock.js';

const OM = 'https://api.open-meteo.com/v1/forecast';
const NWS = 'https://api.weather.gov';
const IEM = 'https://mesonet.agron.iastate.edu';
const COOPS = 'https://api.tidesandcurrents.noaa.gov/api/prod/datagetter';
const SWPC = 'https://services.swpc.noaa.gov';

const STATIONS = [['KXMR', 'Cape Canaveral Skid Strip'], ['KTTS', 'KSC Shuttle Landing Facility'], ['KCOF', 'Patrick SFB'], ['KTIX', 'Titusville'], ['KMLB', 'Melbourne']];
const RAOBS = [['KTBW', 'Tampa Bay'], ['KJAX', 'Jacksonville'], ['KMFL', 'Miami']];
const LEVELS = [1000, 975, 950, 925, 900, 850, 800, 700, 600, 500, 400, 300, 250, 200, 150, 100, 70, 50, 30];
export const WIND_LEVELS = [['10m', 'Surface (10 m)'], ['80m', '80 m'], ['180m', '180 m'], ['925hPa', '925 hPa · 2,500 ft'], ['850hPa', '850 hPa · 5,000 ft'],
  ['700hPa', '700 hPa · 10,000 ft'], ['500hPa', '500 hPa · 18,000 ft'], ['300hPa', '300 hPa · 30,000 ft'], ['250hPa', '250 hPa · 34,000 ft'], ['200hPa', '200 hPa · 39,000 ft']];

const HOURLY = ['temperature_2m', 'dew_point_2m', 'relative_humidity_2m', 'apparent_temperature', 'precipitation_probability', 'precipitation', 'weather_code',
  'pressure_msl', 'cloud_cover', 'cloud_cover_low', 'cloud_cover_mid', 'cloud_cover_high', 'visibility', 'wind_speed_10m', 'wind_speed_80m', 'wind_speed_180m',
  'wind_direction_10m', 'wind_direction_80m', 'wind_direction_180m', 'wind_gusts_10m', 'cape', 'lifted_index', 'convective_inhibition', 'freezing_level_height',
  'boundary_layer_height', 'shortwave_radiation', 'uv_index', 'is_day'];

const WMO = { 0: 'Clear', 1: 'Mainly clear', 2: 'Partly cloudy', 3: 'Overcast', 45: 'Fog', 48: 'Rime fog', 51: 'Light drizzle', 53: 'Drizzle', 55: 'Heavy drizzle',
  61: 'Light rain', 63: 'Rain', 65: 'Heavy rain', 66: 'Freezing rain', 67: 'Freezing rain', 71: 'Light snow', 73: 'Snow', 75: 'Heavy snow', 80: 'Light showers',
  81: 'Showers', 82: 'Violent showers', 95: 'Thunderstorm', 96: 'Thunderstorm, hail', 99: 'Thunderstorm, heavy hail' };

/** Everything fetched, keyed by feed; also shown verbatim in Raw Data. */
export const wx = { target: null, fc: null, profiles: {}, obs: {}, sea: null, tide: {}, space: {}, alerts: null, lcc: [] };
let chartHours = 72;
const listeners = new Set();
export const onWx = (fn) => listeners.add(fn);
const changed = () => listeners.forEach((fn) => fn());

const stat = (k, v, s) => h('div', { class: 'stat' }, h('div', { class: 'k' }, k), h('div', { class: 'v', html: v }), s ? h('div', { class: 's' }, s) : null);
const wind = (dir, kt) => (kt == null ? '—' : `${dir == null ? 'VRB' : String(Math.round(dir)).padStart(3, '0') + '°'} ${Math.round(kt)} <small>kt ${compass(dir)}</small>`);
const isoHour = (ms) => new Date(Math.floor(ms / 36e5) * 36e5).toISOString().slice(0, 16);

/** Interpolate an hourly series to an instant (nearest-neighbour for angles and codes). */
function at(hourly, key, ms, nearest = false) {
  const t = hourly.time, s = ms / 1000, a = hourly[key];
  if (!a || s < t[0] || s > t[t.length - 1]) return null;
  let i = Math.min(t.length - 2, Math.max(0, Math.floor((s - t[0]) / 3600)));
  const f = (s - t[i]) / (t[i + 1] - t[i]);
  if (nearest || /direction|code|is_day/.test(key)) return a[f < 0.5 ? i : i + 1];
  return a[i] == null || a[i + 1] == null ? a[i] ?? a[i + 1] : a[i] + f * (a[i + 1] - a[i]);
}

// ---------------------------------------------------------------- target

export function setTarget(target) {
  const moved = !wx.target || wx.target.lat !== target.lat || wx.target.lon !== target.lon;
  const retimed = !wx.target || wx.target.t0 !== target.t0;
  wx.target = target;
  if (moved) { loadForecast(); loadObs(); loadNws(); loadSea(); } else { renderForecast(); }
  if (moved || retimed) loadProfiles();
  renderLcc();
}

export function start() {
  const src = $('sndSource');
  src.append(h('option', { value: 't0' }, 'Model profile over the pad — at T-0'), h('option', { value: 'now' }, 'Model profile over the pad — now'),
    ...RAOBS.map(([id, name]) => h('option', { value: id }, `Observed balloon — ${name} (${id})`)));
  src.addEventListener('change', renderSounding);
  $('chartRange').addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    chartHours = b.dataset.h === 't0' ? 't0' : +b.dataset.h;
    [...$('chartRange').children].forEach((x) => x.classList.toggle('on', x === b));
    renderCharts();
  });
  loadTide(); loadSpace(); loadRaobs(); loadAfd();
  const every = (min, fn) => setInterval(() => wx.target && fn(), min * 6e4);
  every(15, loadForecast); every(2, loadObs); every(5, loadNws); every(15, loadSea); every(30, loadProfiles);
  setInterval(loadTide, 6 * 6e4); setInterval(loadSpace, 5 * 6e4); setInterval(loadRaobs, 30 * 6e4); setInterval(loadAfd, 30 * 6e4);
}

/** Is T-0 firm and inside what the model covers? */
function t0Usable() {
  const { target: t, fc } = wx;
  if (!t || t.tbd || !fc) return false;
  const tt = fc.hourly.time;
  return t.t0 / 1000 >= tt[0] && t.t0 / 1000 <= tt[tt.length - 1];
}

// ---------------------------------------------------------------- pad forecast

async function loadForecast() {
  const { lat, lon } = wx.target;
  const q = `latitude=${lat}&longitude=${lon}&hourly=${HOURLY.join(',')}&current=${HOURLY.filter((k) => !/uv_index|boundary|freezing|lifted|inhibition|probability|radiation|visibility|cape/.test(k)).join(',')}` +
    '&daily=sunrise,sunset,uv_index_max&temperature_unit=fahrenheit&wind_speed_unit=kn&precipitation_unit=inch&timeformat=unixtime&past_days=1&forecast_days=16';
  try { wx.fc = await getJSON('Open-Meteo pad forecast', `${OM}?${q}`); } catch { return; }
  renderForecast();
}

function renderForecast() {
  const { fc, target: t } = wx;
  if (!fc || !t) return;
  const H = fc.hourly, c = fc.current, tNow = now();

  const tiles = (ms, cur) => {
    const g = (k, d = 0) => num(cur && cur[k] != null ? cur[k] : at(H, k, ms), d);
    const dir = (k) => (cur && cur[k] != null ? cur[k] : at(H, k, ms));
    const el = sunElevation(t.lat, t.lon, ms);
    return [
      stat('Sky', esc(WMO[cur?.weather_code ?? at(H, 'weather_code', ms)] ?? '—'), `Sun ${el >= 0 ? '' : '−'}${Math.abs(el).toFixed(0)}° ${el > 0 ? 'above' : 'below'} horizon`),
      stat('Temperature', `${g('temperature_2m')}<small> °F</small>`, `Feels ${g('apparent_temperature')} · dew pt ${g('dew_point_2m')} °F`),
      stat('Humidity', `${g('relative_humidity_2m')}<small> %</small>`),
      stat('Wind 10 m', wind(dir('wind_direction_10m'), cur?.wind_speed_10m ?? at(H, 'wind_speed_10m', ms)), `Gusts ${g('wind_gusts_10m')} kt`),
      stat('Wind 80 m', wind(dir('wind_direction_80m'), cur?.wind_speed_80m ?? at(H, 'wind_speed_80m', ms))),
      stat('Wind 180 m', wind(dir('wind_direction_180m'), cur?.wind_speed_180m ?? at(H, 'wind_speed_180m', ms))),
      stat('Cloud cover', `${g('cloud_cover')}<small> %</small>`, `Low ${g('cloud_cover_low')} · mid ${g('cloud_cover_mid')} · high ${g('cloud_cover_high')} %`),
      stat('Rain chance', `${num(at(H, 'precipitation_probability', ms))}<small> %</small>`, `${g('precipitation', 2)} in this hour`),
      stat('Visibility', `${num(at(H, 'visibility', ms) / 5280, 1)}<small> mi</small>`),
      stat('Pressure', `${g('pressure_msl', 1)}<small> hPa</small>`, `${num((cur?.pressure_msl ?? at(H, 'pressure_msl', ms)) * 0.02953, 2)} inHg`),
      stat('CAPE', `${num(at(H, 'cape', ms))}<small> J/kg</small>`, `Lifted index ${num(at(H, 'lifted_index', ms), 1)} · CIN ${num(at(H, 'convective_inhibition', ms))}`),
      stat('Freezing level', `${num(at(H, 'freezing_level_height', ms) / 1000, 1)}<small> kft</small>`, `Boundary layer ${num(at(H, 'boundary_layer_height', ms))} ft`),
      stat('Solar / UV', `${num(at(H, 'shortwave_radiation', ms))}<small> W/m²</small>`, `UV index ${num(at(H, 'uv_index', ms), 1)}`),
    ];
  };

  $('nowWx').replaceChildren(...tiles(tNow, c));
  $('nowStamp').textContent = `model analysis · ${hhmmZ(c.time * 1000)}`;
  if (t0Usable()) {
    $('t0Wx').replaceChildren(...tiles(t.t0, null));
    $('t0Stamp').textContent = `${utc(t.t0)} · Open-Meteo best-match model`;
  } else {
    $('t0Wx').replaceChildren(h('div', { class: 'note' }, t.tbd ? 'No firm T-0 has been published for this launch yet, so there is nothing to forecast against.'
      : t.t0 < tNow ? 'T-0 has passed.' : 'T-0 is beyond the 16-day model horizon. This fills in as the launch gets closer.'));
    $('t0Stamp').textContent = '';
  }
  const day = fc.daily, di = day.time.findIndex((d) => d * 1000 > (t0Usable() ? t.t0 : tNow) - 864e5);
  if (di >= 0) $('t0Wx').append(stat('Sunrise / sunset', `${hhmmZ(day.sunrise[di] * 1000)}<small> / </small>${hhmmZ(day.sunset[di] * 1000)}`, `UV max ${num(day.uv_index_max[di], 1)}`));
  renderCharts(); renderLcc(); changed();
}

function renderCharts() {
  const { fc, target: t } = wx;
  if (!fc) return;
  const H = fc.hourly, tNow = now();
  let a = tNow - 6 * 36e5, b = tNow + (chartHours === 't0' ? 0 : chartHours) * 36e5;
  if (chartHours === 't0') { if (!t0Usable()) { a = tNow - 6 * 36e5; b = tNow + 24 * 36e5; } else { a = t.t0 - 18 * 36e5; b = t.t0 + 18 * 36e5; } }
  const idx = H.time.map((s, i) => (s * 1000 >= a && s * 1000 <= b ? i : -1)).filter((i) => i >= 0);
  if (idx.length < 2) return;
  const x = idx.map((i) => H.time[i] * 1000), col = (k, f = (v) => v) => idx.map((i) => (H[k][i] == null ? null : f(H[k][i])));
  const markers = [{ x: tNow, label: 'now' }];
  if (!t.tbd) markers.push({ x: t.t0, label: 'T-0' });
  const S = (label, k, f) => ({ label, data: col(k, f) });
  $('charts').replaceChildren(...[
    { title: 'Temperature', unit: '°F', series: [S('Air', 'temperature_2m'), S('Dew point', 'dew_point_2m')] },
    { title: 'Wind speed', unit: 'kt', zero: true, series: [S('10 m', 'wind_speed_10m'), S('Gust', 'wind_gusts_10m'), S('80 m', 'wind_speed_80m'), S('180 m', 'wind_speed_180m')] },
    { title: 'Cloud cover', unit: '%', zero: true, max: 100, series: [S('Low', 'cloud_cover_low'), S('Mid', 'cloud_cover_mid'), S('High', 'cloud_cover_high')] },
    { title: 'Chance of rain', unit: '%', zero: true, max: 100, series: [S('Probability', 'precipitation_probability')] },
    { title: 'Convective energy (CAPE)', unit: 'J/kg', zero: true, series: [S('CAPE', 'cape')] },
    { title: 'Sea-level pressure', unit: 'hPa', digits: 1, series: [S('Pressure', 'pressure_msl')] },
    { title: 'Freezing level', unit: 'kft', digits: 1, series: [S('0 °C height', 'freezing_level_height', (v) => v / 1000)] },
    { title: 'Visibility', unit: 'mi', zero: true, digits: 1, series: [S('Visibility', 'visibility', (v) => v / 5280)] },
  ].map((spec) => lineChart({ ...spec, x, markers })));
}

// ---------------------------------------------------------------- soundings

async function loadProfiles() {
  const { lat, lon, t0, tbd } = wx.target, tNow = now();
  const vars = LEVELS.flatMap((p) => ['temperature', 'relative_humidity', 'wind_speed', 'wind_direction', 'geopotential_height'].map((v) => `${v}_${p}hPa`));
  const get = async (key, ms) => {
    const hr = isoHour(ms);
    try {
      const d = await getJSON(`Open-Meteo profile (${key})`, `${OM}?latitude=${lat}&longitude=${lon}&hourly=${vars.join(',')},temperature_2m,dew_point_2m,wind_speed_10m,wind_direction_10m,surface_pressure&wind_speed_unit=kn&start_hour=${hr}&end_hour=${hr}`);
      const H = d.hourly, v = (k) => H[k]?.[0];
      const levels = LEVELS.map((p) => {
        const t = v(`temperature_${p}hPa`), rh = v(`relative_humidity_${p}hPa`);
        // Magnus formula: dew point from temperature and relative humidity
        const g = t == null || rh == null ? null : Math.log(Math.max(rh, 0.5) / 100) + 17.625 * t / (243.04 + t);
        return { p, z: v(`geopotential_height_${p}hPa`), t, td: g == null ? null : 243.04 * g / (17.625 - g), dir: v(`wind_direction_${p}hPa`), kt: v(`wind_speed_${p}hPa`) };
      }).filter((l) => l.p < (v('surface_pressure') ?? 1100) && l.z != null);
      levels.unshift({ p: v('surface_pressure'), z: d.elevation ?? 0, t: v('temperature_2m'), td: v('dew_point_2m'), dir: v('wind_direction_10m'), kt: v('wind_speed_10m') });
      wx.profiles[key] = { valid: Date.parse(H.time[0] + 'Z'), label: 'Model profile over the pad', levels };
    } catch { delete wx.profiles[key]; }
  };
  const horizon = tNow + 15.5 * 864e5;
  delete wx.profiles.t0;
  await Promise.all([get('now', tNow), !tbd && t0 > tNow - 36e5 && t0 < horizon ? get('t0', t0) : null]);
  renderSounding(); renderLcc(); changed();
}

async function loadRaobs() {
  // Balloons go up at 00Z and 12Z; data lands about 90 minutes later.
  const latest = Math.floor((now() - 1.5 * 36e5) / (12 * 36e5)) * 12 * 36e5;
  await Promise.all(RAOBS.map(async ([id, name]) => {
    for (const ms of [latest, latest - 12 * 36e5]) {
      const ts = new Date(ms).toISOString().replace(/[-:T]/g, '').slice(0, 12);
      try {
        const d = await getJSON(`IEM sounding ${id}`, `${IEM}/json/raob.py?ts=${ts}&station=${id}`);
        const p = d.profiles?.[0];
        if (!p?.profile?.length) continue;
        wx.profiles[id] = { valid: Date.parse(p.valid), label: `Observed balloon, ${name}`,
          levels: p.profile.filter((l) => l.pres != null && l.hght != null).map((l) => ({ p: l.pres, z: l.hght, t: l.tmpc, td: l.dwpc, dir: l.drct, kt: l.sknt })) };
        return;
      } catch { /* try the previous launch */ }
    }
  }));
  renderSounding(); changed();
}

function renderSounding() {
  const sel = $('sndSource');
  let key = sel.value;
  if (!wx.profiles[key] && key === 't0' && wx.profiles.now) key = 'now';
  const p = wx.profiles[key];
  if (!p) { $('sndStamp').textContent = 'Not available yet.'; return; }
  $('sndStamp').textContent = `${p.label} · valid ${utc(p.valid)}` + (key !== sel.value ? ' (T-0 not in model range — showing now)' : '') +
    (RAOBS.some((r) => r[0] === key) ? ' · the Cape\'s own range balloons are not published' : '');
  skewT($('skewt'), p.levels, `${p.label} · ${dayHourZ(p.valid)}`);
  hodograph($('hodo'), p.levels);
  soundingTable($('sndTable'), p.levels);
  const s = soundingStats(p.levels), kft = (m) => (m == null ? '—' : (m * 3.28084 / 1000).toFixed(1));
  $('sndStats').replaceChildren(
    stat('Max wind', s.maxWind ? `${Math.round(s.maxWind.kt)}<small> kt</small>` : '—', s.maxWind ? `${Math.round(s.maxWind.dir)}° at ${kft(s.maxWind.z)} kft` : ''),
    stat('Max shear', s.maxShear ? `${s.maxShear.v.toFixed(1)}<small> kt/kft</small>` : '—', s.maxShear ? `near ${kft(s.maxShear.z)} kft` : ''),
    stat('0–6 km shear', s.bulk06 == null ? '—' : `${Math.round(s.bulk06)}<small> kt</small>`),
    stat('0 °C level', `${kft(s.z0)}<small> kft</small>`), stat('−10 °C level', `${kft(s.zm10)}<small> kft</small>`), stat('−20 °C level', `${kft(s.zm20)}<small> kft</small>`),
    stat('Coldest', s.coldest ? `${s.coldest.t.toFixed(0)}<small> °C</small>` : '—', s.coldest ? `${kft(s.coldest.z)} kft` : ''));
}

// ---------------------------------------------------------------- observations, NWS

async function loadObs() {
  await Promise.all(STATIONS.map(async ([id]) => {
    try { wx.obs[id] = (await getJSON(`NWS obs ${id}`, `${NWS}/stations/${id}/observations/latest`)).properties; } catch { /* keep last */ }
  }));
  const v = (o) => o?.value;
  const tbl = $('obsTable');
  tbl.replaceChildren(h('tr', {}, ['Station', 'Observed', 'Sky', 'Temp °F', 'Dew pt °F', 'RH %', 'Wind', 'Gust kt', 'Vis mi', 'Pressure inHg', 'Cloud layers', 'METAR'].map((t) => h('th', {}, t))));
  for (const [id, name] of STATIONS) {
    const o = wx.obs[id];
    if (!o) { tbl.append(h('tr', {}, h('td', {}, `${id} · ${name}`), h('td', { colspan: 11, class: 'muted' }, 'no report'))); continue; }
    const clouds = (o.cloudLayers || []).map((c) => `${c.amount}${v(c.base) != null ? ' ' + Math.round(v(c.base) * 3.28084 / 100) * 100 + ' ft' : ''}`).join(', ');
    const kt = kmhToKt(v(o.windSpeed));
    tbl.append(h('tr', {}, h('td', {}, h('b', {}, id), ` · ${name}`), h('td', {}, `${hhmmZ(Date.parse(o.timestamp))} (${ago(Date.parse(o.timestamp), now())})`),
      h('td', {}, o.textDescription || '—'), h('td', { class: 'num' }, num(cToF(v(o.temperature)))), h('td', { class: 'num' }, num(cToF(v(o.dewpoint)))),
      h('td', { class: 'num' }, num(v(o.relativeHumidity))), h('td', { class: 'num' }, kt == null ? '—' : kt < 1 ? 'Calm' : `${num(v(o.windDirection))}° ${compass(v(o.windDirection))} ${num(kt)} kt`),
      h('td', { class: 'num' }, num(kmhToKt(v(o.windGust)))), h('td', { class: 'num' }, num(v(o.visibility) / 1609.34, 1)),
      h('td', { class: 'num' }, v(o.barometricPressure) ? (v(o.barometricPressure) * 0.0002953).toFixed(2) : '—'), h('td', {}, clouds || '—'),
      h('td', { class: 'wrap muted' }, o.rawMessage || '—')));
  }
  renderLcc(); changed();
}

async function loadNws() {
  const { lat, lon } = wx.target, pt = `${lat.toFixed(4)},${lon.toFixed(4)}`;
  getJSON('NWS alerts', `${NWS}/alerts/active?point=${pt}`).then((d) => {
    wx.alerts = d.features.map((f) => f.properties);
    $('alerts').replaceChildren(...(wx.alerts.length ? wx.alerts.map((a) => h('div', { class: 'alert' + (/Extreme|Severe/.test(a.severity) ? ' sev' : '') },
      h('b', {}, a.event), ' ', h('span', { class: 'muted' }, `${a.severity} · until ${utc(Date.parse(a.ends || a.expires))}`), h('p', {}, a.headline || ''),
      h('details', {}, h('summary', {}, 'Full text'), h('p', {}, a.description || ''))))
      : [h('div', { class: 'note' }, 'No active NWS watches, warnings or advisories for the pad.')]));
    renderLcc(); changed();
  }).catch(() => {});
  try {
    const p = await getJSON('NWS gridpoint', `${NWS}/points/${pt}`);
    const f = await getJSON('NWS forecast', p.properties.forecast);
    wx.nws = f.properties.periods;
    $('nwsForecast').replaceChildren(...wx.nws.map((x) => h('div', { class: 'period' }, h('b', {}, `${x.name} · ${x.temperature}°${x.temperatureUnit} · ${x.windSpeed} ${x.windDirection}`), h('span', {}, x.detailedForecast))));
  } catch { /* shown in feed status */ }
}

async function loadAfd() {
  try {
    const list = await getJSON('NWS forecast discussion list', `${NWS}/products/types/AFD/locations/MLB`, { keep: false });
    const p = await getJSON('NWS forecast discussion', `${NWS}/products/${list['@graph'][0].id}`);
    $('afd').textContent = p.productText.trim();
    $('afdStamp').textContent = `issued ${utc(Date.parse(p.issuanceTime))}`;
  } catch { /* feed status */ }
}

// ---------------------------------------------------------------- sea, tide, space

async function loadSea() {
  const { lat, lon, heading, t0, tbd } = wx.target;
  const [la, lo] = destination(lat, lon, heading ?? 90, 60);
  const vars = 'wave_height,wave_direction,wave_period,swell_wave_height,swell_wave_direction,swell_wave_period,wind_wave_height,sea_surface_temperature,ocean_current_velocity,ocean_current_direction';
  try {
    wx.sea = await getJSON('Open-Meteo marine', `https://marine-api.open-meteo.com/v1/marine?latitude=${la.toFixed(3)}&longitude=${lo.toFixed(3)}&current=${vars}&hourly=wave_height,swell_wave_height,wave_period&timeformat=unixtime&forecast_days=8`);
  } catch { return; }
  const c = wx.sea.current, ft = (m) => num(m * 3.28084, 1);
  const wT0 = tbd ? null : at(wx.sea.hourly, 'wave_height', t0);
  $('seaWhere').textContent = `60 km downrange (${la.toFixed(2)}°, ${lo.toFixed(2)}°)`;
  $('sea').replaceChildren(
    stat('Wave height', `${ft(c.wave_height)}<small> ft</small>`, `${num(c.wave_height, 1)} m · ${num(c.wave_period, 1)} s from ${compass(c.wave_direction)}`),
    stat('Swell', `${ft(c.swell_wave_height)}<small> ft</small>`, `${num(c.swell_wave_period, 1)} s from ${compass(c.swell_wave_direction)}`),
    stat('Wind waves', `${ft(c.wind_wave_height)}<small> ft</small>`),
    stat('Waves at T-0', wT0 == null ? '—' : `${ft(wT0)}<small> ft</small>`, wT0 == null ? 'outside forecast' : `${num(wT0, 1)} m`),
    stat('Sea temp', `${num(cToF(c.sea_surface_temperature))}<small> °F</small>`),
    stat('Current', `${num(kmhToKt(c.ocean_current_velocity), 1)}<small> kt</small>`, `toward ${compass(c.ocean_current_direction)}`));
  renderLcc(); changed();
}

async function loadTide() {
  const base = `${COOPS}?station=8721604&units=english&time_zone=gmt&format=json`;
  const one = async (product, extra = 'date=latest') => {
    try { const d = await getJSON(`NOAA tide station: ${product}`, `${base}&product=${product}&${extra}`); wx.tide[product] = d.data || d.predictions; } catch { /* station sensor may be offline */ }
  };
  const day = new Date(now()).toISOString().slice(0, 10).replace(/-/g, '');
  await Promise.all([one('water_level', 'date=latest&datum=MLLW'), one('predictions', `begin_date=${day}&range=48&datum=MLLW&interval=hilo`),
    one('wind'), one('air_temperature'), one('water_temperature'), one('air_pressure')]);
  const T = wx.tide, last = (k) => T[k]?.[T[k].length - 1];
  const next = (T.predictions || []).map((p) => ({ ...p, ms: Date.parse(p.t.replace(' ', 'T') + 'Z') })).filter((p) => p.ms > now()).slice(0, 3);
  $('tide').replaceChildren(
    stat('Water level', `${num(last('water_level')?.v, 2)}<small> ft MLLW</small>`),
    ...next.map((p) => stat(p.type === 'H' ? 'High tide' : 'Low tide', `${hhmmZ(p.ms)}`, `${num(p.v, 1)} ft · ${dayHourZ(p.ms).split(' ')[0]}`)),
    stat('Pier wind', wind(+last('wind')?.d, +last('wind')?.s), `Gust ${num(last('wind')?.g)} kt`),
    stat('Air / water', `${num(last('air_temperature')?.v)}<small> / </small>${num(last('water_temperature')?.v)}<small> °F</small>`),
    stat('Pressure', `${num(last('air_pressure')?.v, 1)}<small> hPa</small>`));
  changed();
}

async function loadSpace() {
  const S = wx.space;
  const get = async (key, path) => { try { S[key] = await getJSON(`SWPC ${key}`, `${SWPC}/${path}`); } catch { /* keep last */ } };
  await Promise.all([get('scales', 'products/noaa-scales.json'), get('kp', 'products/noaa-planetary-k-index.json'), get('wind', 'products/summary/solar-wind-speed.json'),
    get('mag', 'products/summary/solar-wind-mag-field.json'), get('f107', 'products/summary/10cm-flux.json'), get('xray', 'json/goes/primary/xrays-6-hour.json'),
    get('protons', 'json/goes/primary/integral-protons-6-hour.json')]);
  const sc = S.scales?.['0'] || {}, first = (a) => (Array.isArray(a) ? a[0] : a) || {};
  const kpRow = S.kp?.[S.kp.length - 1], kp = Array.isArray(kpRow) ? +kpRow[1] : kpRow?.Kp;
  const x = (S.xray || []).filter((r) => r.energy === '0.1-0.8nm').pop();
  const cls = (f) => { if (!f) return '—'; const e = Math.floor(Math.log10(f)), L = { '-8': 'A', '-7': 'B', '-6': 'C', '-5': 'M', '-4': 'X' }[e] || (e < -8 ? 'A' : 'X'); return `${L}${(f / 10 ** e).toFixed(1)}`; };
  const pr = (S.protons || []).filter((r) => r.energy === '>=10 MeV').pop();
  $('space').replaceChildren(
    stat('Radio blackout', `R${sc.R?.Scale ?? '—'}`, sc.R?.Text), stat('Radiation storm', `S${sc.S?.Scale ?? '—'}`, sc.S?.Text), stat('Geomagnetic', `G${sc.G?.Scale ?? '—'}`, sc.G?.Text),
    stat('Planetary Kp', num(kp, 2)), stat('X-ray flux', cls(x?.flux), 'GOES long band'), stat('Protons ≥10 MeV', `${num(pr?.flux, 2)}<small> pfu</small>`),
    stat('Solar wind', `${num(first(S.wind).proton_speed)}<small> km/s</small>`), stat('IMF Bt / Bz', `${num(first(S.mag).bt)}<small> / </small>${num(first(S.mag).bz_gsm)}<small> nT</small>`),
    stat('F10.7 flux', `${num(first(S.f107).flux)}<small> sfu</small>`));
  renderLcc(); changed();
}

// ---------------------------------------------------------------- wind field for the map

const gridCache = new Map();
export async function windGrid(level, when, bounds) {
  const { t0, tbd } = wx.target || {};
  const ms = when === 't0' && !tbd && t0 > now() && t0 < now() + 15 * 864e5 ? t0 : now();
  const hr = isoHour(ms), key = `${level}|${hr}`;
  if (gridCache.has(key)) return gridCache.get(key);
  const N = 8, lats = [], lons = [];
  for (let i = 0; i < N; i++) for (let j = 0; j < N; j++) {
    lats.push((bounds.s + (bounds.n - bounds.s) * i / (N - 1)).toFixed(2)); lons.push((bounds.w + (bounds.e - bounds.w) * j / (N - 1)).toFixed(2));
  }
  const d = await getJSON(`Open-Meteo wind field (${level})`, `${OM}?latitude=${lats.join(',')}&longitude=${lons.join(',')}&hourly=wind_speed_${level},wind_direction_${level}&wind_speed_unit=kn&start_hour=${hr}&end_hour=${hr}`, { keep: false });
  const grid = { N, bounds, valid: Date.parse(hr + 'Z'), level, u: [], v: [], kt: [] };
  d.forEach((p) => {
    const kt = p.hourly[`wind_speed_${level}`][0] ?? 0, a = (p.hourly[`wind_direction_${level}`][0] ?? 0) * Math.PI / 180;
    grid.kt.push(kt); grid.u.push(-kt * Math.sin(a)); grid.v.push(-kt * Math.cos(a));
  });
  gridCache.set(key, grid);
  return grid;
}

// ---------------------------------------------------------------- commit-criteria screen

/**
 * Rule-of-thumb screening against the public weather launch commit criteria themes.
 * Thresholds are generic; each vehicle has its own limits and the range uses field mills,
 * lightning networks and aircraft that are not public. Treat as a heads-up only.
 */
function evaluate() {
  const { fc, target: t, sea, space, alerts, obs } = wx;
  const rules = [], add = (name, state, value, detail) => rules.push({ name, state, value, detail });
  const lvl = (v, caution, nogo) => (v == null ? 'na' : v >= nogo ? 'nogo' : v >= caution ? 'caution' : 'go');
  const use = t0Usable(), ms = use ? t.t0 : now(), when = use ? 'at T-0' : 'now';
  if (fc) {
    const H = fc.hourly, g = (k) => at(H, k, ms);
    const gust = g('wind_gusts_10m'), w180 = g('wind_speed_180m');
    add('Ground winds', lvl(Math.max(gust ?? 0, w180 ?? 0), 24, 33), `${num(g('wind_speed_10m'))} kt, gusts ${num(gust)} kt`, `Peak near the pad ${when}; 180 m wind ${num(w180)} kt. Typical vehicle limits fall around 30 kt.`);
    const pp = g('precipitation_probability'), pr = g('precipitation');
    add('Precipitation in flight path', pr > 0.01 || pp >= 60 ? 'nogo' : pp >= 30 ? 'caution' : 'go', `${num(pp)}% chance`, `${num(pr, 2)} in forecast in the T-0 hour.`);
    const cape = g('cape'), li = g('lifted_index');
    add('Lightning / thunderstorm potential', cape >= 1500 && pp >= 40 ? 'nogo' : cape >= 800 || li <= -3 ? 'caution' : 'go', `CAPE ${num(cape)} J/kg`, `Lifted index ${num(li, 1)}. Storms within 10 nmi stop a count for 30 minutes after the last strike.`);
    const low = g('cloud_cover_low'), mid = g('cloud_cover_mid'), fz = g('freezing_level_height');
    add('Cumulus clouds', low >= 50 && cape >= 500 ? 'caution' : 'go', `Low cloud ${num(low)}%`, `Cumulus with tops above the ${num(fz / 1000, 1)} kft freezing level are a constraint.`);
    add('Thick cloud layer', mid >= 80 && g('cloud_cover') >= 90 ? 'nogo' : mid >= 50 ? 'caution' : 'go', `Mid cloud ${num(mid)}%`, 'Layers thicker than 4,500 ft that reach into freezing temperatures are a constraint.');
    add('Anvil / high cloud', g('cloud_cover_high') >= 70 && cape >= 500 ? 'caution' : 'go', `High cloud ${num(g('cloud_cover_high'))}%`, 'Attached or detached thunderstorm anvils are a constraint; plain cirrus is not.');
    const vis = g('visibility') / 5280;
    add('Visibility / ceiling', lvl(-vis, -5, -1), `${num(vis, 1)} mi`, 'Range optics and, for some vehicles, recovery need visual tracking.');
  }
  const prof = wx.profiles.t0 || wx.profiles.now;
  if (prof) {
    const s = soundingStats(prof.levels);
    add('Upper-level winds', lvl(s.maxWind?.kt, 100, 140), s.maxWind ? `${Math.round(s.maxWind.kt)} kt at ${(s.maxWind.z * 3.28084 / 1000).toFixed(0)} kft` : '—', `Jet maximum in the model profile (${wx.profiles.t0 ? 'T-0' : 'now'}). Loads depend on shear, which the range measures by balloon.`);
    add('Wind shear aloft', lvl(s.maxShear?.v, 6, 10), s.maxShear ? `${s.maxShear.v.toFixed(1)} kt per 1,000 ft` : '—', s.maxShear ? `Strongest layer near ${(s.maxShear.z * 3.28084 / 1000).toFixed(0)} kft.` : '');
  }
  if (sea) {
    const wv = (use && at(sea.hourly, 'wave_height', t.t0)) || sea.current.wave_height;
    add('Downrange sea state', lvl(wv, 2.5, 4), `${num(wv * 3.28084, 1)} ft waves`, 'Matters for booster and fairing recovery and for crew abort zones.');
  }
  if (space.scales) {
    const sc = space.scales['0'], S = +sc.S?.Scale || 0, G = +sc.G?.Scale || 0;
    add('Space weather', S >= 2 || G >= 4 ? 'nogo' : S >= 1 || G >= 3 ? 'caution' : 'go', `R${sc.R?.Scale ?? 0} S${S} G${G}`, 'Solar proton events can upset avionics; some missions carry a radiation constraint.');
  }
  if (alerts) add('NWS alerts for the pad', alerts.some((a) => /Warning/.test(a.event)) ? 'nogo' : alerts.length ? 'caution' : 'go', alerts.length ? alerts.map((a) => a.event).join(', ') : 'None active', '');
  const xmr = obs.KXMR || obs.KTTS;
  if (xmr) {
    const ts = /TS|LTG/.test(xmr.rawMessage || '') || /thunder/i.test(xmr.textDescription || '');
    add('Thunder reported on the range', ts ? 'nogo' : 'go', xmr.textDescription || '—', `Latest METAR: ${xmr.rawMessage || 'n/a'}`);
  }
  if (t?.goPct != null) add('Official weather forecast', t.goPct >= 70 ? 'go' : t.goPct >= 40 ? 'caution' : 'nogo', `${t.goPct}% favorable`, 'Probability of acceptable weather published with the launch listing.');
  return rules;
}

const TAG = { go: '✓ GO', caution: '! WATCH', nogo: '✕ CONCERN', na: 'NO DATA' };
export function renderLcc() {
  if (!wx.target) return;
  wx.lcc = evaluate();
  $('lcc').replaceChildren(...(wx.lcc.length ? wx.lcc.map((r) => h('div', { class: `rule ${r.state}` },
    h('div', { class: 't' }, h('span', {}, r.name), h('span', { class: 'tag' }, TAG[r.state])), h('div', {}, r.value), h('div', { class: 'd' }, r.detail)))
    : [h('div', { class: 'note' }, 'Waiting for weather feeds…')]));
}

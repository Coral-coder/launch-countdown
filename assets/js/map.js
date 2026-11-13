// Leaflet map: NEXRAD Level II / III radar, satellite, alerts, launch ground track and an animated wind field.
/* global L */

import { $, h, destination, inclination, getJSON, hhmmZ, utc, esc } from './util.js';
import { windGrid, WIND_LEVELS, wx } from './wx.js';

const GEO = 'https://opengeo.ncep.noaa.gov/geoserver';
const IEM = 'https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0';
const FIELD = { s: 25.5, n: 31.5, w: -84.5, e: -76 }; // area covered by the wind field
const TRACK = '#fab219';

let map, layers = {}, radarLayers = [], launchGroup, padGroup, alertLayer, windLayer, loop = null;

const wms = (path, layer, opts = {}) => L.tileLayer.wms(`${GEO}/${path}/ows`, { layers: layer, format: 'image/png', transparent: true, opacity: 0.75, attribution: 'NOAA/NWS', ...opts });
const ridge = (product) => L.tileLayer(`${IEM}/ridge::MLB-${product}-0/{z}/{x}/{y}.png?_={t}`, { t: Date.now(), opacity: 0.75, attribution: 'Iowa Environmental Mesonet' });

export function initMap() {
  map = L.map('map', { center: [28.56, -80.58], zoom: 8, zoomSnap: 0.5, worldCopyJump: true });
  const esri = 'https://server.arcgisonline.com/ArcGIS/rest/services';
  const dark = L.layerGroup([
    L.tileLayer(`${esri}/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}`, { attribution: 'Esri, HERE, Garmin, © OpenStreetMap', maxNativeZoom: 16, maxZoom: 18 }),
    L.tileLayer(`${esri}/Canvas/World_Dark_Gray_Reference/MapServer/tile/{z}/{y}/{x}`, { maxNativeZoom: 16, maxZoom: 18, pane: 'shadowPane' }),
  ]).addTo(map);
  const sat = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', { attribution: 'Esri, Maxar', maxZoom: 18 });
  const osm = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { attribution: '© OpenStreetMap', maxZoom: 19 });

  layers = {
    'Radar L2 · KMLB super-res reflectivity': wms('kmlb', 'kmlb_sr_bref').addTo(map),
    'Radar L2 · KMLB super-res velocity': wms('kmlb', 'kmlb_sr_bvel'),
    'Radar L3 · KMLB base reflectivity (N0B)': ridge('N0B'),
    'Radar L3 · KMLB base velocity (N0U)': ridge('N0U'),
    'Radar L3 · KMLB storm-relative velocity (N0S)': ridge('N0S'),
    'Radar L3 · KMLB hydrometeor class (HHC)': wms('kmlb', 'kmlb_bdhc'),
    'Radar L3 · KMLB 1-hour rainfall (OHA)': wms('kmlb', 'kmlb_boha'),
    'Radar L3 · KMLB storm-total rainfall (DSA)': wms('kmlb', 'kmlb_bdsa'),
    'Radar · MRMS national composite': wms('conus/conus_bref_qcd', 'conus_bref_qcd'),
    'Satellite · GOES-East GeoColor': L.tileLayer('https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/GOES-East_ABI_GeoColor/default/default/GoogleMapsCompatible_Level7/{z}/{y}/{x}.png?_={t}', { t: Date.now(), maxNativeZoom: 7, opacity: 0.8, attribution: 'NASA GIBS' }),
    'Satellite · GOES-East infrared': L.tileLayer(`${IEM}/goes-east-ir-4km-900913/{z}/{x}/{y}.png?_={t}`, { t: Date.now(), opacity: 0.6, attribution: 'IEM' }),
    'Satellite · GOES-East visible': L.tileLayer(`${IEM}/goes-east-vis-1km-900913/{z}/{x}/{y}.png?_={t}`, { t: Date.now(), opacity: 0.7, attribution: 'IEM' }),
  };
  radarLayers = Object.values(layers);
  alertLayer = L.geoJSON(null, { style: (f) => ({ color: /Warning/.test(f.properties.event) ? '#d03b3b' : '#fab219', weight: 2, fillOpacity: 0.08 }),
    onEachFeature: (f, l) => l.bindPopup(`<b>${esc(f.properties.event)}</b><br>${esc(f.properties.headline || '')}`) }).addTo(map);
  launchGroup = L.layerGroup().addTo(map);
  padGroup = L.layerGroup().addTo(map);
  windLayer = new WindLayer().addTo(map);

  L.control.layers({ Dark: dark, 'Satellite imagery': sat, Streets: osm },
    { ...layers, 'NWS alert polygons': alertLayer, 'Wind field': windLayer, 'Ground track & range rings': launchGroup, 'Launch pads': padGroup }, { collapsed: true }).addTo(map);
  L.control.scale({ imperial: true }).addTo(map);

  const lv = $('windLevel');
  WIND_LEVELS.forEach(([v, label]) => lv.append(h('option', { value: v }, label)));
  lv.value = '10m';
  lv.addEventListener('change', refreshWind);
  $('windTime').addEventListener('change', refreshWind);
  $('radarOpacity').addEventListener('input', (e) => radarLayers.forEach((l) => l.setOpacity(+e.target.value)));
  $('btnLoop').addEventListener('click', toggleLoop);

  setInterval(refreshRadar, 2 * 6e4);
  setInterval(refreshWind, 15 * 6e4);
  setInterval(loadAlerts, 5 * 6e4);
  loadAlerts(); stamp();
  $('mapLegend').replaceChildren(
    h('span', {}, h('i', { style: `background:${TRACK}` }), 'Ground track from the published launch azimuth'),
    h('span', {}, h('i', { style: 'background:#c3c2b7' }), '5 and 10 nmi lightning rings around the pad'),
    h('span', {}, h('i', { style: 'background:#7fb2f5' }), 'Wind field streaks (longer and brighter = faster)'),
    h('span', {}, 'Use the layer button (top right of the map) for velocity, Level III products, satellite and rainfall.'));
}

const stamp = () => { $('mapStamp').textContent = `radar refreshed ${hhmmZ(Date.now())}`; };

function refreshRadar() {
  for (const l of radarLayers) {
    if (!map.hasLayer(l)) continue;
    if (l.wmsParams) l.setParams({ _: Date.now() }, false); else { l.options.t = Date.now(); l.redraw(); }
  }
  stamp();
}

async function loadAlerts() {
  try {
    const d = await getJSON('NWS alerts (Florida polygons)', 'https://api.weather.gov/alerts/active?area=FL', { keep: false });
    alertLayer.clearLayers();
    alertLayer.addData({ type: 'FeatureCollection', features: d.features.filter((f) => f.geometry) });
  } catch { /* feed status shows the failure */ }
}

// ---- radar loop (RainViewer mosaic, last two hours)
async function toggleLoop() {
  const btn = $('btnLoop');
  if (loop) {
    clearInterval(loop.timer); loop.frames.forEach((f) => map.removeLayer(f.layer)); loop = null;
    btn.textContent = '▶ Loop radar (2 h)'; $('loopStamp').textContent = '';
    return;
  }
  try {
    const d = await getJSON('RainViewer radar frames', 'https://api.rainviewer.com/public/weather-maps.json', { keep: false });
    const frames = d.radar.past.map((f) => ({ time: f.time * 1000,
      layer: L.tileLayer(`${d.host}${f.path}/256/{z}/{x}/{y}/4/1_1.png`, { opacity: 0, maxNativeZoom: 7, attribution: 'RainViewer' }).addTo(map) }));
    let i = 0;
    const step = () => {
      frames.forEach((f, k) => f.layer.setOpacity(k === i ? +$('radarOpacity').value : 0));
      $('loopStamp').textContent = `mosaic frame ${utc(frames[i].time)}`;
      i = (i + 1) % frames.length;
    };
    loop = { frames, timer: setInterval(step, 500) };
    step(); btn.textContent = '■ Stop loop';
  } catch { $('loopStamp').textContent = 'Radar loop unavailable right now.'; }
}

// ---- launch overlays
export function showLaunch(launch, all) {
  launchGroup.clearLayers(); padGroup.clearLayers();
  const seen = new Set();
  for (const l of all) {
    const p = l.pad; if (p.lat == null || seen.has(p.name)) continue; seen.add(p.name);
    const sel = p.name === launch.pad.name;
    L.circleMarker([p.lat, p.lon], { radius: sel ? 7 : 4, color: sel ? TRACK : '#c3c2b7', weight: 2, fillColor: '#0d0d0d', fillOpacity: 1 })
      .bindTooltip(p.name, { permanent: true, direction: 'right', className: 'pad-label', offset: [6, 0] })
      .bindPopup(`<b>${esc(p.name)}</b><br>${esc(p.location || '')}<br>${p.lat.toFixed(5)}, ${p.lon.toFixed(5)}`).addTo(padGroup);
  }
  const { lat, lon } = launch.pad;
  if (lat == null) return;
  for (const nmi of [5, 10]) {
    L.circle([lat, lon], { radius: nmi * 1852, color: '#c3c2b7', weight: 1, dashArray: '4 4', fill: false, interactive: false }).addTo(launchGroup);
  }
  if (launch.heading != null) {
    const pts = []; for (let km = 0; km <= 3000; km += 25) pts.push(destination(lat, lon, launch.heading, km));
    L.polyline(pts, { color: TRACK, weight: 3, opacity: 0.9 })
      .bindPopup(`<b>${esc(launch.name)}</b><br>Launch azimuth ${launch.heading}° · about ${inclination(lat, launch.heading).toFixed(1)}° inclination<br><span>Straight great-circle sketch from the published azimuth, not a flown trajectory.</span>`).addTo(launchGroup);
    for (const km of [100, 250, 500, 1000, 2000]) {
      L.circleMarker(destination(lat, lon, launch.heading, km), { radius: 3, color: TRACK, fillColor: TRACK, fillOpacity: 1, weight: 1 })
        .bindTooltip(`${km} km`, { permanent: true, direction: 'top', className: 'pad-label' }).addTo(launchGroup);
    }
  }
  for (const r of launch.nsf?.recoveries || []) {
    if (r.latitude == null || r.longitude == null) continue;
    L.marker([r.latitude, r.longitude]).bindPopup(`<b>${esc(r.landing_zone?.name || 'Landing')}</b><br>${esc(r.status || '')}`).addTo(launchGroup);
  }
  map.setView([lat, lon], Math.max(map.getZoom(), 8));
  refreshWind();
}

// ---- animated wind field
async function refreshWind() {
  if (!wx.target) return;
  try {
    const g = await windGrid($('windLevel').value, $('windTime').value, FIELD);
    windLayer.setGrid(g);
    const max = Math.max(...g.kt), mean = g.kt.reduce((a, b) => a + b, 0) / g.kt.length;
    $('windLegend').textContent = `wind valid ${utc(g.valid)} · mean ${mean.toFixed(0)} kt · max ${max.toFixed(0)} kt`;
  } catch { $('windLegend').textContent = 'Wind field unavailable right now.'; }
}

const WindLayer = L.Layer.extend({
  onAdd(m) {
    this._map = m;
    this.canvas = L.DomUtil.create('canvas', 'wind-canvas', m.getContainer());
    Object.assign(this.canvas.style, { position: 'absolute', inset: '0', zIndex: 450 });
    this.reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
    m.on('move zoom resize', this.reset, this);
    this.reset();
    this.raf = requestAnimationFrame(() => this.frame());
  },
  onRemove(m) {
    cancelAnimationFrame(this.raf); m.off('move zoom resize', this.reset, this); this.canvas.remove();
  },
  setGrid(g) { this.grid = g; this.reset(); },
  reset() {
    const s = this._map.getSize(), dpr = window.devicePixelRatio || 1;
    this.canvas.width = s.x * dpr; this.canvas.height = s.y * dpr;
    this.canvas.style.width = s.x + 'px'; this.canvas.style.height = s.y + 'px';
    this.ctx = this.canvas.getContext('2d'); this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.parts = Array.from({ length: Math.min(900, Math.round(s.x * s.y / 900)) }, () => this.spawn({}));
    if (this.reduced) this.arrows();
  },
  spawn(p) {
    const b = this._map.getBounds(), g = FIELD;
    const s = Math.max(b.getSouth(), g.s), n = Math.min(b.getNorth(), g.n), w = Math.max(b.getWest(), g.w), e = Math.min(b.getEast(), g.e);
    p.lat = s + Math.random() * (n - s); p.lon = w + Math.random() * (e - w); p.age = Math.random() * 80 | 0;
    return p;
  },
  /** Bilinear wind (u east, v north, kt) at a point, or null outside the field. */
  sample(lat, lon) {
    const g = this.grid, b = g.bounds, N = g.N;
    const fy = (lat - b.s) / (b.n - b.s) * (N - 1), fx = (lon - b.w) / (b.e - b.w) * (N - 1);
    if (fy < 0 || fx < 0 || fy > N - 1 || fx > N - 1) return null;
    const i = Math.min(N - 2, fy | 0), j = Math.min(N - 2, fx | 0), a = fy - i, c = fx - j;
    const mix = (arr) => arr[i * N + j] * (1 - a) * (1 - c) + arr[i * N + j + 1] * (1 - a) * c + arr[(i + 1) * N + j] * a * (1 - c) + arr[(i + 1) * N + j + 1] * a * c;
    return [mix(g.u), mix(g.v)];
  },
  frame() {
    this.raf = requestAnimationFrame(() => this.frame());
    if (!this.grid || this.reduced || document.hidden) return;
    const { ctx } = this, s = this._map.getSize();
    ctx.globalCompositeOperation = 'destination-out'; ctx.fillStyle = 'rgba(0,0,0,0.08)'; ctx.fillRect(0, 0, s.x, s.y);
    ctx.globalCompositeOperation = 'source-over'; ctx.lineWidth = 1.2;
    const top = Math.max(20, ...this.grid.kt);
    for (const p of this.parts) {
      const w = p.age++ > 90 ? null : this.sample(p.lat, p.lon);
      if (!w) { this.spawn(p); p.age = 0; continue; }
      const a = this._map.latLngToContainerPoint([p.lat, p.lon]), kt = Math.hypot(w[0], w[1]);
      const k = 0.5 + 2.2 / top; // px per frame per knot, normalised so strong and weak fields both read
      const b = L.point(a.x + w[0] * k * 0.35, a.y - w[1] * k * 0.35);
      ctx.strokeStyle = `rgba(127,178,245,${0.25 + 0.65 * Math.min(1, kt / top)})`;
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
      const ll = this._map.containerPointToLatLng(b); p.lat = ll.lat; p.lon = ll.lng;
    }
  },
  /** Static arrows for people who ask for reduced motion. */
  arrows() {
    if (!this.grid) return;
    const { ctx } = this, s = this._map.getSize();
    ctx.clearRect(0, 0, s.x, s.y); ctx.strokeStyle = 'rgba(127,178,245,.9)'; ctx.lineWidth = 1.5;
    for (let x = 30; x < s.x; x += 60) for (let y = 30; y < s.y; y += 60) {
      const ll = this._map.containerPointToLatLng([x, y]), w = this.sample(ll.lat, ll.lng); if (!w) continue;
      const kt = Math.hypot(w[0], w[1]) || 1, len = 8 + Math.min(18, kt / 4), ux = w[0] / kt, uy = -w[1] / kt;
      ctx.beginPath(); ctx.moveTo(x - ux * len, y - uy * len); ctx.lineTo(x + ux * len, y + uy * len);
      ctx.lineTo(x + ux * len - (ux - uy) * 5, y + uy * len - (uy + ux) * 5); ctx.stroke();
    }
  },
});

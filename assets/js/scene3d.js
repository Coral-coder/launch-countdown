// 3D view: modelled ascent corridor along the published azimuth, a ray-marched cloud volume
// built from model cloud cover on pressure levels, and the wind profile over the pad.
// Scene units are kilometres; x east, y up (times the vertical scale), z south.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { $, h, destination, getJSON, utc, num, compass } from './util.js';
import { wx, onWx } from './wx.js';
import { now } from './clock.js';
import { siteFor } from './sites.js';

const R = 6371, rad = (d) => d * Math.PI / 180;
const TRACK = 0xfab219, WIND = 0x7fb2f5;

// Generic two-stage orbital ascent: [seconds, altitude km, downrange km]. A sketch of a typical
// profile, not provider data; real trajectories differ by vehicle and mission.
const ASCENT = [[0, 0, 0], [20, 1.2, 0.02], [40, 4.5, 0.4], [60, 9.5, 1.8], [75, 14, 4.5], [100, 25, 13], [125, 40, 32], [150, 62, 65], [175, 85, 115],
  [200, 105, 180], [250, 135, 330], [300, 155, 520], [350, 167, 750], [400, 175, 1020], [450, 182, 1340], [500, 190, 1700], [530, 200, 1950]];
const EVENTS = [[72, 'Max-Q'], [152, 'MECO'], [190, 'Kármán line'], [530, 'SECO']];
const T_END = 530;

// Cloud volume: 300 x 300 km around the pad, surface to 16 km.
const VOL = { half: 150, top: 16, N: 7, NZ: 32 };
const CL = [1000, 950, 925, 900, 850, 800, 700, 600, 500, 400, 300, 250, 200, 150];
const stdKm = (p) => 44.33 * (1 - (p / 1013.25) ** 0.1903); // standard-atmosphere height of a pressure level

let radarMesh = null, radar = null, radarKey = '', radarBusy = false, radarNote = 'radar volume loading…', worker = null;
let renderer, scene, camera, controls, root, cloudMesh, marker, markerDrop, box, launch = null, tGetter = () => null;
let chaseDist = null;
let ex = 1, shown = false, raf = 0, cloud = null, cloudKey = '', lastView = 'clouds';

/** Catmull-Rom through a table column, so position is a smooth function of flight time. */
function spline(col, t) {
  let i = ASCENT.findIndex((r) => r[0] > t); if (i < 0) i = ASCENT.length - 1; i = Math.max(1, i);
  const p = (k) => ASCENT[Math.min(ASCENT.length - 1, Math.max(0, k))][col];
  const t0 = ASCENT[i - 1][0], u = Math.min(1, Math.max(0, (t - t0) / (ASCENT[i][0] - t0)));
  const a = p(i - 2), b = p(i - 1), c = p(i), d = p(i + 1);
  return Math.max(0, 0.5 * (2 * b + (c - a) * u + (2 * a - 5 * b + 4 * c - d) * u * u + (3 * b - 3 * c + d - a) * u * u * u));
}

/** lat/lon -> scene km, using Mercator scaled at the pad so it lines up with the map tiles. */
function proj(lat, lon) {
  const { lat: la0, lon: lo0 } = launch.pad, k = Math.cos(rad(la0)), merc = (f) => Math.log(Math.tan(Math.PI / 4 + rad(f) / 2));
  return [R * rad(lon - lo0) * k, -R * (merc(lat) - merc(la0)) * k];
}
const heading = () => launch.heading ?? 90;
function flightPos(t) {
  const [la, lo] = destination(launch.pad.lat, launch.pad.lon, heading(), spline(2, t)), [x, z] = proj(la, lo);
  return new THREE.Vector3(x, spline(1, t) * ex, z);
}

function label(text, pos, { color = '#f0efec', size = 13 } = {}) {
  const c = document.createElement('canvas'), g = c.getContext('2d'), font = `600 ${size * 2}px system-ui, sans-serif`;
  g.font = font; c.width = Math.ceil(g.measureText(text).width) + 16; c.height = size * 2 + 12;
  g.font = font; g.textBaseline = 'middle'; g.lineWidth = 6; g.strokeStyle = 'rgba(0,0,0,.85)'; g.strokeText(text, 8, c.height / 2); g.fillStyle = color; g.fillText(text, 8, c.height / 2);
  const tex = new THREE.CanvasTexture(c); tex.colorSpace = THREE.SRGBColorSpace;
  const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false, sizeAttenuation: false, transparent: true }));
  s.scale.set(c.width / c.height * 0.035, 0.035, 1); s.center.set(0, 0.5); s.position.copy(pos); s.renderOrder = 10;
  return s;
}
const line = (pts, color, opacity = 1) => new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), new THREE.LineBasicMaterial({ color, transparent: opacity < 1, opacity }));

// ---------------------------------------------------------------- ground

function ground(z, n, y) {
  const { lat, lon } = launch.pad, tiles = 2 ** z, km = 2 * Math.PI * R * Math.cos(rad(lat)) / tiles;
  const fx = (lon + 180) / 360 * tiles, fy = (1 - Math.log(Math.tan(rad(lat)) + 1 / Math.cos(rad(lat))) / Math.PI) / 2 * tiles;
  const x0 = Math.floor(fx) - (n >> 1), y0 = Math.floor(fy) - (n >> 1);
  const c = document.createElement('canvas'); c.width = c.height = 256 * n;
  const g = c.getContext('2d'); g.fillStyle = '#17191c'; g.fillRect(0, 0, c.width, c.height);
  const tex = new THREE.CanvasTexture(c); tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = 8;
  for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) {
    const img = new Image(); img.crossOrigin = 'anonymous';
    img.onload = () => { g.drawImage(img, i * 256, j * 256); tex.needsUpdate = true; };
    img.src = `https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/${z}/${y0 + j}/${((x0 + i) % tiles + tiles) % tiles}`;
  }
  const m = new THREE.Mesh(new THREE.PlaneGeometry(n * km, n * km), new THREE.MeshBasicMaterial({ map: tex }));
  m.rotation.x = -Math.PI / 2;
  m.position.set((x0 + n / 2 - fx) * km, y, (y0 + n / 2 - fy) * km);
  return m;
}

// ---------------------------------------------------------------- ascent corridor

/** Tube of varying radius swept along a polyline. */
function sweep(pts, radii, seg = 20) {
  const pos = [], idx = [], up = new THREE.Vector3(0, 1, 0), t = new THREE.Vector3(), n = new THREE.Vector3(), b = new THREE.Vector3();
  pts.forEach((p, i) => {
    t.subVectors(pts[Math.min(pts.length - 1, i + 1)], pts[Math.max(0, i - 1)]).normalize();
    n.crossVectors(t, up); if (n.lengthSq() < 1e-6) n.set(1, 0, 0); n.normalize(); b.crossVectors(n, t);
    for (let k = 0; k < seg; k++) {
      const a = k / seg * Math.PI * 2, c = Math.cos(a) * radii[i], s = Math.sin(a) * radii[i];
      pos.push(p.x + n.x * c + b.x * s, p.y + n.y * c + b.y * s, p.z + n.z * c + b.z * s);
    }
    if (i) for (let k = 0; k < seg; k++) {
      const a = (i - 1) * seg + k, a2 = (i - 1) * seg + (k + 1) % seg, c = a + seg, c2 = a2 + seg;
      idx.push(a, c, a2, a2, c, c2);
    }
  });
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3)); g.setIndex(idx);
  return g;
}

function buildAscent(g) {
  const pts = [], radii = [], ts = [];
  for (let t = 0; t <= T_END; t += 2) { pts.push(flightPos(t)); radii.push(0.5 + 0.05 * spline(2, t) + 0.02 * spline(1, t)); ts.push(t); }
  g.add(line(pts, TRACK));
  g.add(new THREE.Mesh(sweep(pts, radii), new THREE.MeshBasicMaterial({ color: TRACK, transparent: true, opacity: 0.13, side: THREE.DoubleSide, depthWrite: false })));
  // corridor rings and drop lines to the ground track every 30 s
  const ring = new THREE.LineBasicMaterial({ color: TRACK, transparent: true, opacity: 0.55 });
  for (let i = 0; i < pts.length; i += 15) {
    const geo = sweep([pts[Math.max(0, i - 1)], pts[i], pts[Math.min(pts.length - 1, i + 1)]], [radii[i], radii[i], radii[i]], 32);
    const a = geo.getAttribute('position').array.slice(32 * 3, 64 * 3);
    g.add(new THREE.LineLoop(new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(a, 3)), ring));
    if (i) g.add(line([pts[i], new THREE.Vector3(pts[i].x, 0, pts[i].z)], TRACK, 0.25));
  }
  g.add(line(pts.map((p) => new THREE.Vector3(p.x, 0.3, p.z)), TRACK, 0.5)); // ground track
  for (const [t, name] of EVENTS) {
    const p = flightPos(t);
    const dot = new THREE.Mesh(new THREE.SphereGeometry(1, 12, 12), new THREE.MeshBasicMaterial({ color: TRACK })); dot.position.copy(p); dot.userData.fixed = 0.006; g.add(dot);
    g.add(label(`${name} · T+${t}s · ${num(spline(1, t))} km`, p));
  }
}

// ---------------------------------------------------------------- winds and height scale

function buildPole(g) {
  const top = 18;
  g.add(line([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, top * ex, 0)], 0xc3c2b7, 0.6));
  // height scale on the near corner of the cloud volume, clear of the pad
  const cx = -VOL.half, cz = VOL.half;
  g.add(line([new THREE.Vector3(cx, 0, cz), new THREE.Vector3(cx, VOL.top * ex, cz)], 0xc3c2b7, 0.8));
  for (let km = 4; km <= 16; km += 4) g.add(label(`${km} km · ${Math.round(km * 3.281)} kft`, new THREE.Vector3(cx, km * ex, cz), { color: '#c3c2b7', size: 10 }));
  const prof = (wx.target && !wx.target.tbd && wx.profiles.t0) || wx.profiles.now;
  if (!prof) return;
  let last = -9;
  for (const l of prof.levels) {
    if (!$('v3WindLbl').checked || l.kt == null || l.dir == null || l.z == null || l.z > top * 1000 || l.kt < 1) continue;
    const to = rad(l.dir + 180), dir = new THREE.Vector3(Math.sin(to), 0, -Math.cos(to)), len = Math.max(3, l.kt * 0.5);
    g.add(new THREE.ArrowHelper(dir, new THREE.Vector3(0, l.z / 1000 * ex, 0), len, WIND, Math.min(4, len * 0.3), Math.min(2, len * 0.15)));
    if (l.z / 1000 - last > 2.6) { last = l.z / 1000; g.add(label(`${Math.round(l.kt)} kt ${compass(l.dir)}`, dir.clone().multiplyScalar(len + 1).setY(l.z / 1000 * ex), { color: '#9cc3f7', size: 10 })); }
  }
  if (prof.levels.some((l) => l.t != null)) {
    const fz = prof.levels.find((l, i, a) => i && l.t <= 0 && a[i - 1].t > 0);
    if (fz) {
      const y = fz.z / 1000 * ex, s = VOL.half;
      g.add(line([[-s, -s], [s, -s], [s, s], [-s, s], [-s, -s]].map(([x, z]) => new THREE.Vector3(x, y, z)), 0x4a6f9c, 0.8));
      g.add(label(`0 °C · ${(fz.z * 3.281 / 1000).toFixed(1)} kft`, new THREE.Vector3(s, y, s), { color: '#8fb4e6', size: 10 }));
    }
  }
}

// ---------------------------------------------------------------- cloud volume

const VERT = `
out vec3 vOrigin; out vec3 vDir;
void main() {
  vOrigin = (inverse(modelMatrix) * vec4(cameraPosition, 1.0)).xyz;
  vDir = position - vOrigin;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`;
const FRAG = `
precision highp float; precision highp sampler3D;
uniform sampler3D map; uniform vec3 size; uniform float dens; uniform float time; uniform vec3 sun;
in vec3 vOrigin; in vec3 vDir; out vec4 outColor;
float hash(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float noise(vec3 x) {
  vec3 i = floor(x), f = fract(x); f = f * f * (3.0 - 2.0 * f);
  return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
             mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p) { float a = 0.5, s = 0.0; for (int i = 0; i < 4; i++) { s += a * noise(p); p *= 2.03; a *= 0.5; } return s / 0.9375; }
// Model cloud fraction (0..1) at a point in the unit box: x east, y up, z south.
float cover(vec3 p) { return texture(map, vec3(p.x + 0.5, 0.5 - p.z, p.y + 0.5)).r; }
float density(vec3 p) {
  float c = cover(p);
  if (c < 0.015) return 0.0;
  vec3 q = p * size / vec3(11.0, 1.4, 11.0);
  // Break the smooth model field into cloud-sized lumps that fill about a fraction c of the layer.
  float th = mix(0.74, 0.22, c);
  // Fade out toward the walls and lid so the box never slices a cloud into a flat face.
  vec3 e = smoothstep(vec3(0.5), vec3(0.455), abs(p));
  return smoothstep(th, th + 0.14, fbm(q)) * (0.35 + 0.65 * c) * e.x * e.z * smoothstep(0.5, 0.44, p.y);
}
vec2 hitBox(vec3 o, vec3 d) {
  vec3 inv = 1.0 / d, a = (-0.5 - o) * inv, b = (0.5 - o) * inv, mn = min(a, b), mx = max(a, b);
  return vec2(max(mn.x, max(mn.y, mn.z)), min(mx.x, min(mx.y, mx.z)));
}
void main() {
  vec3 d = normalize(vDir);
  vec2 hit = hitBox(vOrigin, d);
  if (hit.x > hit.y) discard;
  hit.x = max(hit.x, 0.0);
  // Fixed-length steps on shells centred on the camera: neighbouring pixels sample the same
  // depths, so the picture does not shimmer as the camera orbits.
  const int STEPS = 230;
  const float dt = 0.0078; // same step for every ray, or neighbouring pixels sample different depths and the picture crawls
  float T = 1.0;
  float t = ceil(hit.x / dt) * dt;
  vec3 p = vOrigin + d * t, col = vec3(0.0);
  float kmPerUnit = length(d * size);
  for (int i = 0; i < STEPS; i++) {
    if (t > hit.y) break;
    t += dt;
    float dn = density(p);
    if (dn > 0.001) {
      float shade = exp(-density(p + sun * 0.035) * 1.1 - density(p + sun * 0.09) * 0.7);
      vec3 c = mix(vec3(0.52, 0.56, 0.64), vec3(1.0, 0.99, 0.96), shade) * (0.8 + 0.2 * (p.y + 0.5));
      float a = 1.0 - exp(-dn * dens * 0.55 * dt * kmPerUnit);
      col += T * a * c; T *= 1.0 - a;
      if (T < 0.02) break;
    }
    p += d * dt;
  }
  float alpha = 1.0 - T;
  if (alpha < 0.003) discard;
  outColor = vec4(col / alpha, alpha);
}`;

// ---------------------------------------------------------------- radar volume

const RADAR_GRID = { n: 192, nz: 40, half: VOL.half, top: VOL.top };
const BUCKET = 'https://unidata-nexrad-level2.s3.amazonaws.com';
// NWS reflectivity colours, 5 dBZ steps from 5 to 75
const DBZ_COLORS = ['#04e9e7', '#019ff4', '#0300f4', '#02fd02', '#01c501', '#008e00', '#fdf802', '#e5bc00', '#fd9500', '#fd0000', '#d40000', '#bc0000', '#f800fd', '#9854c6', '#fdfdfd'];
function dbzLut() {
  const d = new Uint8Array(256 * 4);
  for (let b = 0; b < 256; b++) {
    const dbz = b / 2 - 32, i = Math.max(0, Math.min(DBZ_COLORS.length - 1, Math.floor((dbz - 5) / 5))), c = new THREE.Color(DBZ_COLORS[i]);
    d.set([c.r * 255, c.g * 255, c.b * 255, 255], b * 4);
  }
  const t = new THREE.DataTexture(d, 256, 1); t.magFilter = t.minFilter = THREE.NearestFilter; t.needsUpdate = true;
  return t;
}
const RFRAG = `
precision highp float; precision highp sampler3D;
uniform sampler3D map; uniform sampler2D lut; uniform vec3 size; uniform float minDbz; uniform float opacity;
in vec3 vOrigin; in vec3 vDir; out vec4 outColor;
vec2 hitBox(vec3 o, vec3 d) {
  vec3 inv = 1.0 / d, a = (-0.5 - o) * inv, b = (0.5 - o) * inv, mn = min(a, b), mx = max(a, b);
  return vec2(max(mn.x, max(mn.y, mn.z)), min(mx.x, min(mx.y, mx.z)));
}
void main() {
  vec3 d = normalize(vDir);
  vec2 hit = hitBox(vOrigin, d);
  if (hit.x > hit.y) discard;
  hit.x = max(hit.x, 0.0);
  const int STEPS = 300;
  const float dt = 0.006;
  float T = 1.0, km = length(d * size) * dt;
  float t = ceil(hit.x / dt) * dt;
  vec3 p = vOrigin + d * t, col = vec3(0.0);
  for (int i = 0; i < STEPS; i++) {
    if (t > hit.y) break;
    t += dt;
    // texture axes: x east, y north, z up; bytes are dBZ * 2 + 64, 0 = no echo
    float r = texture(map, vec3(p.x + 0.5, 0.5 - p.z, p.y + 0.5)).r, dbz = r * 127.5 - 32.0;
    if (r > 0.004 && dbz >= minDbz) {
      float s = smoothstep(minDbz, 60.0, dbz);
      float a = 1.0 - exp(-(0.22 + 2.4 * s) * opacity * km);
      // brighter toward the top of the echo so storm cores read as solid shapes
      vec3 c = mix(texture(lut, vec2(r, 0.5)).rgb, vec3(1.0), 0.14) * (0.8 + 0.2 * (p.y + 0.5));
      col += T * a * c; T *= 1.0 - a;
      if (T < 0.02) break;
    }
    p += d * dt;
  }
  float alpha = 1.0 - T;
  if (alpha < 0.004) discard;
  outColor = vec4(col / alpha, alpha);
}`;

function buildRadar(g) {
  if (!radar) return;
  // the radar site itself, relative to the pad
  const [rx, rz] = proj(radar.site.lat, radar.site.lon);
  const dot = new THREE.Mesh(new THREE.SphereGeometry(1, 12, 12), new THREE.MeshBasicMaterial({ color: 0x7fe0a0 })); dot.position.set(rx, 0.3, rz); dot.userData.fixed = 0.006; g.add(dot);
  g.add(label(`${radar.id} radar`, new THREE.Vector3(rx + 2, 0.5, rz), { color: '#9be8b5', size: 10 }));
  const tex = new THREE.Data3DTexture(radar.data, radar.n, radar.n, radar.nz);
  tex.format = THREE.RedFormat; tex.minFilter = tex.magFilter = THREE.LinearFilter; tex.unpackAlignment = 1; tex.needsUpdate = true;
  const h3 = VOL.top * ex;
  radarMesh = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.ShaderMaterial({
    glslVersion: THREE.GLSL3, vertexShader: VERT, fragmentShader: RFRAG, side: THREE.BackSide, transparent: true, depthWrite: false, depthTest: false,
    uniforms: { map: { value: tex }, lut: { value: dbzLut() }, size: { value: new THREE.Vector3(VOL.half * 2, VOL.top, VOL.half * 2) },
      minDbz: { value: +$('v3MinDbz').value }, opacity: { value: +$('v3RadOp').value } },
  }));
  radarMesh.scale.set(VOL.half * 2, h3, VOL.half * 2); radarMesh.position.y = h3 / 2; radarMesh.renderOrder = 6;
  radarMesh.visible = $('v3Radar').checked;
  g.add(radarMesh);
}

/** Find the newest KMLB volume in the public archive and hand it to the decoder if it is new. */
async function loadRadar() {
  if (!launch || launch.pad.lat == null || radarBusy || !$('v3Radar').checked) return;
  const id = siteFor(launch.region).radar;
  if (!id) { if (radar) { radar = null; radarKey = ''; rebuild(); } radarNote = 'no NEXRAD radar covers this launch site'; return; }
  radarBusy = true;
  try {
    let key = null;
    for (const back of [0, 1]) { // around 00Z the newest volume is still under yesterday's prefix
      const d = new Date(now() - back * 864e5), prefix = `${d.getUTCFullYear()}/${String(d.getUTCMonth() + 1).padStart(2, '0')}/${String(d.getUTCDate()).padStart(2, '0')}/${id}/`;
      const xml = await (await fetch(`${BUCKET}/?list-type=2&prefix=${prefix}`, { cache: 'no-store' })).text();
      const keys = [...xml.matchAll(/<Key>([^<]+)<\/Key>/g)].map((m) => m[1]).filter((k) => !k.endsWith('_MDM'));
      if (keys.length) { key = keys[keys.length - 1]; break; }
    }
    if (!key) throw new Error('no volumes listed');
    const job = `${key}|${launch.pad.lat},${launch.pad.lon}`;
    if (job === radarKey) { radarBusy = false; return; }
    if (!radar) radarNote = 'radar volume downloading and decoding…';
    worker = worker || new Worker(new URL('./radar-worker.js', import.meta.url));
    worker.onmessage = (e) => {
      radarBusy = false;
      if (!e.data.ok) { radarNote = `radar volume unavailable: ${e.data.error}`; return; }
      radarKey = e.data.id; radar = { ...e.data.vol, id: e.data.id.slice(e.data.id.lastIndexOf('/') + 1, e.data.id.lastIndexOf('/') + 5) }; radarNote = '';
      rebuild();
    };
    worker.onerror = (e) => { radarBusy = false; radarNote = `radar decoder failed: ${e.message}`; };
    worker.postMessage({ id: job, url: `${BUCKET}/${key}`, grid: { ...RADAR_GRID, lat: launch.pad.lat, lon: launch.pad.lon } });
  } catch (e) { radarBusy = false; radarNote = `radar volume unavailable: ${e.message}`; }
}

function buildClouds(g) {
  const h3 = VOL.top * ex;
  box = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(VOL.half * 2, h3, VOL.half * 2)), new THREE.LineBasicMaterial({ color: 0x55585e, transparent: true, opacity: 0.6 }));
  box.position.y = h3 / 2; g.add(box);
  g.add(label('radar & cloud volume 300 × 300 × 16 km', new THREE.Vector3(VOL.half, h3, -VOL.half), { color: '#898781', size: 10 }));
  if (!cloud) return;
  const tex = new THREE.Data3DTexture(cloud.data.slice(), VOL.N, VOL.N, VOL.NZ);
  tex.format = THREE.RedFormat; tex.minFilter = tex.magFilter = THREE.LinearFilter; tex.unpackAlignment = 1; tex.needsUpdate = true;
  cloudMesh = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.ShaderMaterial({
    glslVersion: THREE.GLSL3, vertexShader: VERT, fragmentShader: FRAG, side: THREE.BackSide, transparent: true, depthWrite: false, depthTest: false,
    uniforms: { map: { value: tex }, size: { value: new THREE.Vector3(VOL.half * 2, VOL.top, VOL.half * 2) }, dens: { value: +$('v3Dens').value }, time: { value: 0 },
      sun: { value: new THREE.Vector3(0.35, 0.8, -0.3).normalize().multiply(new THREE.Vector3(1 / (VOL.half * 2), 1 / VOL.top, 1 / (VOL.half * 2))).normalize() } },
  }));
  cloudMesh.scale.set(VOL.half * 2, h3, VOL.half * 2); cloudMesh.position.y = h3 / 2; cloudMesh.renderOrder = 5;
  cloudMesh.visible = $('v3Clouds').checked;
  g.add(cloudMesh);
}

async function loadClouds() {
  if (!launch || !wx.target) return;
  const { lat, lon } = launch.pad, { t0, tbd } = wx.target;
  const ms = $('v3Time').value === 't0' && !tbd && t0 > now() && t0 < now() + 15 * 864e5 ? t0 : now();
  const hr = new Date(Math.floor(ms / 36e5) * 36e5).toISOString().slice(0, 16), key = `${lat},${lon},${hr}`;
  if (key === cloudKey) return;
  cloudKey = key;
  const N = VOL.N, dLat = VOL.half / 110.57, dLon = VOL.half / (111.32 * Math.cos(rad(lat))), lats = [], lons = [];
  for (let j = 0; j < N; j++) for (let i = 0; i < N; i++) { lats.push((lat - dLat + 2 * dLat * j / (N - 1)).toFixed(3)); lons.push((lon - dLon + 2 * dLon * i / (N - 1)).toFixed(3)); }
  try {
    const d = await getJSON('Open-Meteo cloud layers (3D)', `https://api.open-meteo.com/v1/forecast?latitude=${lats.join(',')}&longitude=${lons.join(',')}&hourly=${CL.map((p) => `cloud_cover_${p}hPa`).join(',')}&start_hour=${hr}&end_hour=${hr}`, { keep: false });
    const data = new Uint8Array(N * N * VOL.NZ), zs = CL.map(stdKm), layers = { low: 0, mid: 0, high: 0 };
    d.forEach((pt, c) => {
      const col = CL.map((p) => pt.hourly[`cloud_cover_${p}hPa`][0] ?? 0);
      for (let k = 0; k < VOL.NZ; k++) {
        const z = (k + 0.5) / VOL.NZ * VOL.top;
        let i = zs.findIndex((v) => v > z), v;
        if (i <= 0) v = i === 0 ? col[0] : col[col.length - 1] * Math.max(0, 1 - (z - zs[zs.length - 1]) / 1.5);
        else v = col[i - 1] + (col[i] - col[i - 1]) * (z - zs[i - 1]) / (zs[i] - zs[i - 1]);
        data[c + N * N * k] = Math.round(Math.max(0, Math.min(100, v)) * 2.55);
        const band = z < 2 ? 'low' : z < 7 ? 'mid' : 'high'; layers[band] = Math.max(layers[band], v);
      }
    });
    cloud = { data, valid: Date.parse(hr + 'Z'), layers };
  } catch { cloud = null; cloudKey = ''; }
  rebuild();
}

// ---------------------------------------------------------------- scene lifecycle

// ---------------------------------------------------------------- aircraft, vessels and hazard areas

// Snapshots written by the deploy workflow (scripts/fetch_traffic.py): the sources cannot be read
// from a browser, so positions are a few minutes old and are dead-reckoned forward from there.
const AIR = 0xb4b8bc, // aircraft are a neutral 70% white so the alert colours stand out
   SEA = 0x6fe36f, NOTAM = 0xffd21f, NOTMAR = 0xff5a36;
const TRACK_CAP_KM = 25;      // a projected track is never drawn longer than this
const TRAFFIC_KM = 220;       // only traffic this close to the pad is drawn
const SIZE = 0.01;            // model size as a fraction of its distance from the camera
const STALE_MIN = 20;         // aircraft older than this are dropped; vessels move slowly enough to keep
let traffic = null, hazards = null, trafficGroup = null, movers = [], trafficNote = 'traffic snapshot loading…', lastMove = 0;

// Small 3D models, built once and shared. All point along -Z with +Y up, about two units long.
const GEO = {};                       // light, jet, heli, boat
const shared = (g) => Object.values(GEO).includes(g);
function models() {
  if (GEO.light) return;
  const part = (g, x, y, z, rx = 0, rz = 0) => { if (rx) g.rotateX(rx); if (rz) g.rotateZ(rz); g.translate(x, y, z); return g.index ? g.toNonIndexed() : g; };
  // flat plate cut from a top-view outline (x across, y aft), laid horizontal
  const plate = (pts, y = 0, thick = 0.04) => part(new THREE.ExtrudeGeometry(new THREE.Shape(pts.map(([x, z]) => new THREE.Vector2(x, z))), { depth: thick, bevelEnabled: false }), 0, y + thick / 2, 0, Math.PI / 2);
  const done = (parts) => { const g = mergeGeometries(parts); g.computeVertexNormals(); return g; };

  // light propeller aircraft: straight high wing
  GEO.light = done([
    part(new THREE.CylinderGeometry(0.13, 0.1, 1.5, 10), 0, 0, 0.05, Math.PI / 2),
    part(new THREE.ConeGeometry(0.13, 0.3, 10), 0, 0, -0.85, -Math.PI / 2),
    part(new THREE.BoxGeometry(2.0, 0.05, 0.4), 0, 0.12, -0.15),
    part(new THREE.BoxGeometry(0.76, 0.05, 0.24), 0, 0.02, 0.68),
    part(new THREE.BoxGeometry(0.05, 0.4, 0.3), 0, 0.2, 0.68),
  ]);
  // airliner: long fuselage, swept wings, engines under the wings, swept tail. Heavies get four engines.
  const engine = (x, z) => part(new THREE.CylinderGeometry(0.07, 0.07, 0.34, 8), x, -0.13, z, Math.PI / 2);
  const airliner = (pods) => done([
    part(new THREE.CylinderGeometry(0.14, 0.14, 1.7, 12), 0, 0, 0, Math.PI / 2),
    part(new THREE.ConeGeometry(0.14, 0.34, 12), 0, 0, -1.02, -Math.PI / 2),
    part(new THREE.ConeGeometry(0.14, 0.5, 12), 0, 0.03, 1.1, Math.PI / 2),
    plate([[0, -0.38], [1.05, 0.3], [1.05, 0.46], [0.14, 0.18], [-0.14, 0.18], [-1.05, 0.46], [-1.05, 0.3]], -0.06),
    plate([[0, 0.78], [0.42, 1.12], [0.42, 1.22], [0, 1.08], [-0.42, 1.22], [-0.42, 1.12]], 0.04),
    part(new THREE.BoxGeometry(0.04, 0.46, 0.34), 0, 0.26, 1.02, -0.5),
    ...pods.flatMap(([x, z]) => [engine(x, z), engine(-x, z)]),
  ]);
  GEO.jet = airliner([[0.38, -0.12]]);
  GEO.heavy = airliner([[0.34, -0.14], [0.68, 0.08]]);
  // business jet: slim fuselage, low swept wing, engines on the rear fuselage, T-tail
  GEO.bizjet = done([
    part(new THREE.CylinderGeometry(0.11, 0.11, 1.4, 10), 0, 0, 0, Math.PI / 2),
    part(new THREE.ConeGeometry(0.11, 0.4, 10), 0, 0, -0.9, -Math.PI / 2),
    part(new THREE.ConeGeometry(0.11, 0.4, 10), 0, 0.02, 0.9, Math.PI / 2),
    plate([[0, -0.2], [0.85, 0.3], [0.85, 0.42], [0.1, 0.2], [-0.1, 0.2], [-0.85, 0.42], [-0.85, 0.3]], -0.08),
    part(new THREE.BoxGeometry(0.04, 0.44, 0.3), 0, 0.26, 0.85, -0.45),
    plate([[0, 0.86], [0.36, 1.06], [0.36, 1.14], [0, 1.04], [-0.36, 1.14], [-0.36, 1.06]], 0.44),
    part(new THREE.CylinderGeometry(0.06, 0.06, 0.34, 8), 0.2, 0.06, 0.5, Math.PI / 2),
    part(new THREE.CylinderGeometry(0.06, 0.06, 0.34, 8), -0.2, 0.06, 0.5, Math.PI / 2),
  ]);
  // helicopter: pod, tail boom, tail rotor fin, crossed main rotor blades, skids
  const pod = new THREE.SphereGeometry(0.3, 12, 10); pod.scale(0.85, 0.85, 1.5);
  GEO.heli = done([
    part(pod, 0, 0, -0.25),
    part(new THREE.CylinderGeometry(0.05, 0.03, 1.0, 8), 0, 0.06, 0.55, Math.PI / 2),
    part(new THREE.BoxGeometry(0.04, 0.34, 0.16), 0, 0.2, 1.02),
    part(new THREE.CylinderGeometry(0.03, 0.03, 0.2, 6), 0, 0.33, -0.2),
    part(new THREE.BoxGeometry(2.0, 0.02, 0.1), 0, 0.44, -0.2),
    part(new THREE.BoxGeometry(0.1, 0.02, 2.0), 0, 0.44, -0.2),
    part(new THREE.BoxGeometry(0.04, 0.04, 0.9), 0.24, -0.33, -0.2),
    part(new THREE.BoxGeometry(0.04, 0.04, 0.9), -0.24, -0.33, -0.2),
  ]);
  // vessels
  const hull = (beam, thick) => plate([[0, -1], [beam * 0.9, -0.62], [beam, 0.95], [-beam, 0.95], [-beam * 0.9, -0.62]], 0, thick);
  const house = (w, hgt, len, y, z) => part(new THREE.BoxGeometry(w, hgt, len), 0, y, z);
  const funnel = (z) => part(new THREE.CylinderGeometry(0.05, 0.06, 0.22, 8), 0, 0.7, z);
  GEO.boat = done([
    plate([[0, -1], [0.3, -0.45], [0.34, 0.9], [-0.34, 0.9], [-0.3, -0.45]], 0, 0.28),
    house(0.44, 0.3, 0.5, 0.43, 0.5), part(new THREE.CylinderGeometry(0.03, 0.03, 0.5, 6), 0, 0.55, -0.1),
  ]);
  GEO.cargo = done([hull(0.24, 0.22), house(0.36, 0.2, 0.5, 0.32, -0.42), house(0.36, 0.26, 0.5, 0.35, 0.13), house(0.4, 0.4, 0.2, 0.42, 0.78), funnel(0.9)]); // container stacks, bridge aft
  GEO.tanker = done([hull(0.26, 0.2), house(0.07, 0.07, 1.25, 0.24, -0.1), house(0.3, 0.05, 0.08, 0.26, -0.5), house(0.3, 0.05, 0.08, 0.26, 0.1), house(0.42, 0.4, 0.2, 0.4, 0.78), funnel(0.9)]); // flat deck with pipework
  GEO.liner = done([hull(0.25, 0.22), house(0.42, 0.26, 1.4, 0.35, 0.05), house(0.36, 0.14, 1.15, 0.55, 0.1), funnel(0.45)]);                                // tall full-length superstructure
  const sail = new THREE.ExtrudeGeometry(new THREE.Shape([[0, 0], [0.62, 0], [0, 1.25]].map(([x, y]) => new THREE.Vector2(x, y))), { depth: 0.02, bevelEnabled: false });
  sail.rotateY(-Math.PI / 2); sail.translate(0.01, 0.3, -0.12);
  GEO.sail = done([plate([[0, -0.9], [0.2, -0.3], [0.2, 0.75], [-0.2, 0.75], [-0.2, -0.3]], 0, 0.2), part(new THREE.CylinderGeometry(0.02, 0.02, 1.35, 6), 0, 0.85, -0.14), sail.toNonIndexed()]);
}

/** Model and relative size for a vessel, from its AIS ship type when one was heard. */
function vesselKind(v) {
  const t = v.ship_type || 0;
  if (t === 36) return ['sail', 0.8];
  if (t >= 60 && t <= 69) return ['liner', 1.35];
  if (t >= 70 && t <= 79) return ['cargo', 1.4];
  if (t >= 80 && t <= 89) return ['tanker', 1.4];
  return ['boat', 0.75];
}

/** Model and relative size for an aircraft: ADS-B emitter category first, then type code, then how it is flying. */
function aircraftKind(v) {
  const t = (v.type || '').toUpperCase(), cat = v.cat || '';
  if (cat === 'A7' || /^(H\d|EC\d|AS\d|B06|B40[79]|B41|B42|B43|R22|R44|R66|S6[14]|S7[06]|S92|UH|AH|CH|MH|MD[569]|A109|A119|A139|A169|AW|BK17|B505|EN[24]8)/.test(t)) return ['heli', 0.85];
  if (cat === 'A5' || /^(B74|B77|B78|A33|A34|A35|A38|MD11|DC10|C17|C5M|K35|KC|B52|A124|IL76)/.test(t)) return ['heavy', 1.5];
  if (/^(C5[0-9]|C6[0-9]|C7[0-9]|C25|GL|G[1-6]\d\d|GLF|LJ|F2T|F9|FA|H25|HA4|PC24|E35|E55|E50|CL[36]|BE40|PRM)/.test(t)) return ['bizjet', 0.85];
  if (/^A[346]$/.test(cat) || /^(B7[0-9]|B3[0-9]|A[23]\d|A1[49]|BCS|E1[3-9]|E[27]\d|CRJ|MD[18]|DC|F1[568]|F22|F35|T38)/.test(t)) return ['jet', 1.15];
  if ((v.kt || 0) > 260 || (v.alt_ft || 0) > 24000) return ['jet', 1.15];
  return ['light', 0.8];
}

const kmBetween = (a, b, c, d) => { const x = rad(d - b) * Math.cos(rad((a + c) / 2)), y = rad(c - a); return Math.hypot(x, y) * R; };
function inRing(lat, lon, ring) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [yi, xi] = ring[i], [yj, xj] = ring[j];
    if ((yi > lat) !== (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}
// The launch corridor's footprint on the surface: from the pad to 500 km downrange, widening with distance.
const CORRIDOR_KM = 500, corridorHalf = (along) => 8 + 0.06 * along;
function inCorridor(lat, lon) {
  const { lat: la0, lon: lo0 } = launch.pad, h = rad(heading());
  const dx = rad(lon - lo0) * Math.cos(rad(la0)) * R, dy = rad(lat - la0) * R;
  const along = dx * Math.sin(h) + dy * Math.cos(h), cross = Math.abs(dx * Math.cos(h) - dy * Math.sin(h));
  return along >= -3 && along <= CORRIDOR_KM && cross <= corridorHalf(Math.max(0, along));
}
const hhmm = (iso) => (iso ? new Date(iso).toISOString().slice(11, 16) + 'Z' : '?');

async function loadTraffic() {
  if (!launch) return;
  const get = async (name, file) => { try { return await getJSON(name, `data/${file}?t=${Date.now()}`, { keep: false }); } catch { return null; } };
  [traffic, hazards] = await Promise.all([get('Aircraft and vessel snapshot', 'traffic.json'), get('Launch hazard areas', 'hazards.json')]);
  buildTraffic();
}

/** Hazard areas that matter now: near this pad and not yet over. */
function activeAreas() {
  const t = now(), { lat, lon } = launch.pad;
  return (hazards?.areas || []).filter((a) => (!a.to || Date.parse(a.to) > t) && a.rings.some((r) => kmBetween(lat, lon, r[0][0], r[0][1]) < 1500))
    .map((a) => ({ ...a, live: (!a.from || Date.parse(a.from) - t < 2 * 36e5) }));
}

function buildTraffic() {
  if (!root || !launch || launch.pad.lat == null) return;
  if (trafficGroup) { root.remove(trafficGroup); trafficGroup.traverse((o) => { if (!shared(o.geometry)) o.geometry?.dispose(); if (o.material) { o.material.map?.dispose(); o.material.dispose(); } }); }
  const shownAt = new Map(movers.map((m) => [m.v.id, { p: m.icon.position.clone(), r: m.icon.rotation.y }]));
  trafficGroup = new THREE.Group(); movers = []; models();
  root.add(trafficGroup);
  const areas = activeAreas(), g = trafficGroup;

  if ($('v3Haz').checked) {
    // corridor footprint on the surface, the zone the forecast check uses
    const edge = (side) => { const pts = []; for (let d = 0; d <= CORRIDOR_KM; d += 25) { const [la, lo] = destination(...destination(launch.pad.lat, launch.pad.lon, heading(), d), heading() + 90 * side, corridorHalf(d)); const [x, z] = proj(la, lo); pts.push(new THREE.Vector3(x, 0.12, z)); } return pts; };
    const foot = line([...edge(1), ...edge(-1).reverse(), edge(1)[0]], TRACK, 0.55); foot.material.depthTest = false; foot.renderOrder = 7; g.add(foot);
    for (const a of areas) {
      const color = a.kind === 'notam' ? NOTAM : NOTMAR, dim = a.live ? 1 : 0.45;
      const y0 = (a.floor_ft || 0) * 0.0003048 * ex, y1 = (a.kind === 'notam' ? Math.min(20, (a.ceil_ft ?? 65000) * 0.0003048) : 0.25) * ex;
      for (const ring of a.rings) {
        const xz = ring.map(([la, lo]) => proj(la, lo));
        const loop = (y) => line([...xz, xz[0]].map(([x, z]) => new THREE.Vector3(x, y, z)), color, 0.9 * dim);
        g.add(loop(y0 + 0.05), loop(y1));
        const every = Math.max(1, Math.round(xz.length / 10));
        xz.forEach(([x, z], i) => { if (i % every === 0) g.add(line([new THREE.Vector3(x, y0, z), new THREE.Vector3(x, y1, z)], color, 0.35 * dim)); });
        const shape = new THREE.Shape(xz.map(([x, z]) => new THREE.Vector2(x, -z)));
        const fill = new THREE.Mesh(new THREE.ShapeGeometry(shape), new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.1 * dim, side: THREE.DoubleSide, depthWrite: false }));
        fill.rotation.x = -Math.PI / 2; fill.position.y = y0 + 0.06; g.add(fill);
        g.add(label(`${a.kind === 'notam' ? 'NOTAM' : 'NOTMAR'} ${a.id} · ${hhmm(a.from)}–${hhmm(a.to)}${a.kind === 'notam' ? ` · to ${a.ceil_ft == null ? 'unlimited' : Math.round(a.ceil_ft / 100) * 100 + ' ft'}` : ''}`,
          new THREE.Vector3(xz[0][0], y1, xz[0][1]), { color: a.kind === 'notam' ? '#ffe27a' : '#ff9a80', size: 10 }));
      }
    }
  }

  const add = (v, sea) => {
    const { lat, lon } = launch.pad;
    if (v.lat == null || kmBetween(lat, lon, v.lat, v.lon) > TRAFFIC_KM) return;
    if (!sea && (v.ground || (now() / 1000 - v.t) / 60 > STALE_MIN)) return;
    const color = sea ? SEA : AIR;
    // Lit models, drawn after the weather volumes so they stay visible through cloud and radar.
    const [kind, rel] = sea ? vesselKind(v) : aircraftKind(v), size = SIZE * rel;
    const icon = new THREE.Mesh(GEO[kind], new THREE.MeshLambertMaterial({ color, transparent: true }));
    icon.renderOrder = 7; icon.userData.fixed = size;
    // a stalk down to the surface makes an aircraft's height readable from any angle
    const stalk = sea ? null : new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.28, depthTest: false }));
    if (stalk) { stalk.renderOrder = 7; g.add(stalk); }
    const track = new THREE.Line(new THREE.BufferGeometry(), new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.5, depthTest: false })); track.renderOrder = 7;
    // 5 minutes ahead: a tick across the track. 10 minutes ahead: an X.
    const mark = (pts) => { const m = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(pts.map(([x, z]) => new THREE.Vector3(x, 0, z))), track.material); m.userData.fixed = 0.007; m.renderOrder = 7; return m; };
    const ticks = [mark([[-1, 0], [1, 0]]), mark([[-1, -1], [1, 1], [-1, 1], [1, -1]])];
    g.add(icon, track, ...ticks);
    movers.push({ v, sea, icon, track, ticks, stalk, size, tag: null, inside: false, state: 0, prev: shownAt.get(v.id) || null, off: new THREE.Vector3(), rotOff: 0 });
  };
  if ($('v3Air').checked) (traffic?.aircraft || []).forEach((v) => add(v, false));
  if ($('v3Sea').checked) (traffic?.vessels || []).forEach((v) => add(v, true));
  moveTraffic(true, 0);
}

/** The traffic lines in the readout; refreshed whenever positions are, so the counts follow the clock. */
function noteTraffic(areas) {
  const snapAge = traffic ? (now() - Date.parse(traffic.generated_at)) / 6e4 : null;
  const n = (sea) => movers.filter((m) => m.sea === sea).length;
  trafficNote = !traffic ? 'traffic snapshot unavailable'
    : `traffic  ${n(false)} aircraft · ${n(true)} vessels · ${Math.round(snapAge)} min old\n` +
      `hazards  ${areas.filter((a) => a.kind === 'notam').length} NOTAM · ${areas.filter((a) => a.kind === 'notmar').length} NOTMAR · ${movers.filter((m) => m.state === 2).length} inside · ${movers.filter((m) => m.state === 1).length} inbound` +
      (snapAge > STALE_MIN ? '\n         snapshot too old to show aircraft' : '') +
      ((traffic.notes || []).some((x) => /AIS/.test(x)) ? '\n         no AIS key set: vessels off' : '') +
      ((hazards?.notes || []).length && !areas.some((a) => a.kind === 'notmar') ? '\n         maritime warnings unavailable' : '');
}

/** Dead-reckon every aircraft and vessel to the present and redraw its projected track. */
// Positions are recomputed every frame so everything glides; the hazard checks run every two seconds.
// When a new snapshot corrects a position, the model eases across to it instead of jumping.
let areasNow = [], lastFrameMs = 0;
function moveTraffic(full = true, dt = 0) {
  if (full) areasNow = activeAreas();
  const areas = areasNow, ease = Math.exp(-dt / 2.5);
  const live = areas.filter((a) => a.live), tNow = now() / 1000;
  const clock = tGetter(), toT0 = clock == null ? null : -clock / 6e4, t0ms = clock == null ? null : now() - clock; // minutes to T-0, and T-0 itself
  for (const m of movers) {
    const { v, sea } = m, kt = v.kt || 0, hasTrack = v.track != null && kt > 0.3;
    const kmPerMin = kt * 1.852 / 60, age = Math.max(0, (tNow - v.t) / 60);
    const at = (min) => {
      const [la, lo] = hasTrack ? destination(v.lat, v.lon, v.track, kmPerMin * (age + min)) : [v.lat, v.lon];
      const altFt = sea ? 0 : Math.max(0, (v.alt_ft || 0) + (v.fpm || 0) * Math.min(age + min, 12));
      const [x, z] = proj(la, lo);
      return { la, lo, altFt, p: new THREE.Vector3(x, altFt * 0.0003048 * ex + (sea ? 0.08 : 0), z) };
    };
    const here = at(0);
    const hdg = hasTrack ? v.track : v.heading, rot = hdg != null ? -rad(hdg) : m.icon.rotation.y;
    if (m.prev) {
      // first placement after a new snapshot: start from where the model was being shown
      m.off = m.prev.p.sub(here.p); if (m.off.length() > 40) m.off.set(0, 0, 0);
      m.rotOff = Math.atan2(Math.sin(m.prev.r - rot), Math.cos(m.prev.r - rot));
      m.prev = null;
    }
    m.off.multiplyScalar(ease); m.rotOff *= ease;
    const shown = (pos) => pos.add(m.off);
    shown(here.p);
    m.icon.position.copy(here.p);
    m.icon.rotation.y = rot + m.rotOff;
    if (m.stalk) m.stalk.geometry.setFromPoints([here.p, new THREE.Vector3(here.p.x, 0, here.p.z)]);
    // Projected track: ten minutes ahead, cut short for anything fast so the picture stays readable.
    const horizon = hasTrack ? Math.min(10, TRACK_CAP_KM / kmPerMin) : 0;
    m.track.visible = horizon > 0;
    if (horizon > 0) m.track.geometry.setFromPoints([here.p, shown(at(horizon / 2).p), shown(at(horizon).p)]);
    [5, 10].forEach((min, i) => { m.ticks[i].visible = horizon >= min; if (horizon >= min) { m.ticks[i].position.copy(shown(at(min).p)); m.ticks[i].rotation.y = -rad(v.track); } });
    if (horizon > 0 && horizon < 5) { m.ticks[0].visible = true; m.ticks[0].position.copy(shown(at(horizon).p)); m.ticks[0].rotation.y = -rad(v.track); } // too fast for 5 minutes: tick where the track ends
    if (m.tag) m.tag.position.copy(here.p);
    if (!full) continue;
    // State 2: inside a hazard area or the launch corridor right now (flashes red / yellow).
    // State 1: on course to be inside one at T-0 (flashes red). Vessels are checked against maritime
    // areas and aircraft against airspace within its altitudes; both against the corridor footprint.
    const hit = (pos, list) => inCorridor(pos.la, pos.lo) || list.some((ar) => (sea ? ar.kind === 'notmar' : ar.kind === 'notam' && pos.altFt >= (ar.floor_ft || 0) && pos.altFt <= (ar.ceil_ft ?? Infinity))
      && ar.rings.some((r) => inRing(pos.la, pos.lo, r)));
    let state = hit(here, live) ? 2 : 0;
    // A straight-line forecast is only meaningful a short way ahead: three hours for a ship, twenty minutes for an aircraft.
    if (!state && hasTrack && toT0 != null && toT0 > 0 && toT0 <= (sea ? 180 : 20)) {
      if (hit(at(toT0), areas.filter((ar) => (!ar.from || Date.parse(ar.from) <= t0ms) && (!ar.to || Date.parse(ar.to) >= t0ms)))) state = 1;
    }
    if (state !== m.state || (state && !m.tag)) {
      if (m.tag) { trafficGroup.remove(m.tag); m.tag.material.map.dispose(); m.tag.material.dispose(); m.tag = null; }
      m.state = state; m.inside = state === 2;
      m.icon.userData.fixed = state ? Math.max(m.size, SIZE) * 2 : m.size;
      m.track.material.opacity = state ? 1 : 0.5;
      if (state) {
        m.tag = label(`${v.name || v.id}${sea ? '' : ' · ' + Math.round(here.altFt / 100) * 100 + ' ft'} · ${Math.round(kt)} kt${state === 1 ? ' · in the area at T-0' : ''}`, here.p, { color: state === 2 ? '#ffe11a' : '#ff8a8a', size: 10 });
        trafficGroup.add(m.tag);
      } else { const c = sea ? SEA : AIR; m.icon.material.color.set(c); m.track.material.color.set(c); }
    }
    if (m.tag) m.tag.position.copy(here.p);
  }
  if (full) noteTraffic(areas);
}

function rebuild() {
  if (!scene || !launch || launch.pad.lat == null) return;
  if (root) { scene.remove(root); root.traverse((o) => { if (!shared(o.geometry)) o.geometry?.dispose(); if (o.material) { o.material.map?.dispose(); o.material.uniforms?.map?.value.dispose(); o.material.uniforms?.lut?.value.dispose(); o.material.dispose(); } }); }
  root = new THREE.Group(); cloudMesh = null; radarMesh = null;
  root.add(ground(5, 5, -0.4), ground(8, 5, 0));
  const ascent = new THREE.Group();
  buildAscent(ascent); buildPole(root); buildClouds(root); buildRadar(root);
  // The corridor is drawn after the weather volumes and ignores depth, so it always reads through
  // cloud and radar instead of popping in and out as the draw order changes.
  ascent.traverse((o) => { if (o.material && !o.isSprite) { o.material.transparent = true; o.material.depthTest = false; o.renderOrder = 8; } });
  root.add(ascent);
  marker = new THREE.Mesh(new THREE.SphereGeometry(1, 16, 16), new THREE.MeshBasicMaterial({ color: 0xffffff })); marker.userData.fixed = 0.011;
  markerDrop = line([new THREE.Vector3(), new THREE.Vector3()], 0xffffff, 0.5);
  for (const o of [marker, markerDrop]) { o.material.transparent = true; o.material.depthTest = false; o.renderOrder = 9; }
  root.add(marker, markerDrop);
  root.add(label(launch.pad.name, new THREE.Vector3(1.5, 0.5, 0), { size: 12 }));
  scene.add(root);
  trafficGroup = null; buildTraffic();
  $('v3Stamp').textContent = cloud ? `cloud model valid ${utc(cloud.valid)}` : 'cloud model loading…';
}

function view(name) {
  lastView = name;
  const az = rad(heading()), along = new THREE.Vector3(Math.sin(az), 0, -Math.cos(az)), side = new THREE.Vector3(along.z, 0, -along.x);
  if (name === 'chase') { chaseDist = null; return; }
  if (name === 'ascent') {
    const mid = flightPos(230); controls.target.set(mid.x, 55 * ex, mid.z);
    camera.position.copy(controls.target).addScaledVector(side, 780).add(new THREE.Vector3(0, 230, 0)).addScaledVector(along, -260);
  } else if (name === 'top') {
    controls.target.set(0, 0, 0); camera.position.set(0, 620, 1);
  } else {
    controls.target.set(0, 4 * ex, 0);
    camera.position.copy(controls.target).addScaledVector(side, 105).addScaledVector(along, -65).add(new THREE.Vector3(0, 42 + 10 * ex, 0));
  }
  controls.update();
}

function frame(ms) {
  raf = requestAnimationFrame(frame);
  if (!root) return;
  const live = tGetter(), follow = $('v3Follow').checked && live != null && live >= 0 && live <= T_END * 1000;
  const t = follow ? live / 1000 : +$('v3T').value;
  if (follow) $('v3T').value = Math.round(t);
  const p = flightPos(t);
  marker.position.copy(p);
  markerDrop.geometry.setFromPoints([p, new THREE.Vector3(p.x, 0, p.z)]);
  $('v3TOut').textContent = `T+${Math.round(t)} s${follow ? ' live' : ''}`;
  // keep markers a constant size on screen
  const dt = Math.min(0.5, (ms - lastFrameMs) / 1000 || 0); lastFrameMs = ms;
  if (ms - lastMove > 2000) { lastMove = ms; moveTraffic(true, dt); } else moveTraffic(false, dt);
  // anything inside a hazard area flashes red / yellow
  const flash = Math.floor(ms / 280) % 2 ? 0xff2a2a : 0xffe11a;
  const warn = Math.floor(ms / 280) % 2 ? 0xff2a2a : 0x5a0c0c;
  for (const m of movers) if (m.state) { const c = m.state === 2 ? flash : warn; m.icon.material.color.set(c); m.track.material.color.set(c); }
  root.traverse((o) => { if (o.userData.fixed) o.scale.setScalar(camera.position.distanceTo(o.position) * o.userData.fixed); });
  const tm = telemetry(t);
  if (lastView === 'chase') {
    // Follow the vehicle, close in while it is slow and pulling back as it accelerates.
    const want = Math.min(1100, 9 + 150 * tm.v), az = rad(heading());
    chaseDist = chaseDist == null ? want : chaseDist + (want - chaseDist) * 0.04;
    const along = new THREE.Vector3(Math.sin(az), 0, -Math.cos(az)), side = new THREE.Vector3(along.z, 0, -along.x);
    controls.target.lerp(p, chaseDist === want ? 1 : 0.15);
    camera.position.copy(controls.target).addScaledVector(side, chaseDist).addScaledVector(along, -chaseDist * 0.35).add(new THREE.Vector3(0, chaseDist * 0.3, 0));
    controls.autoRotate = false; controls.update();
  } else { controls.autoRotate = $('v3Spin').checked; controls.update(); }
  renderer.render(scene, camera);
  const L = cloud?.layers, P = (v, d = 0, w = 6) => num(v, d).padStart(w);
  $('v3Readout').textContent =
    `T+${String(Math.round(t)).padStart(3)} s   calculated telemetry\n` +
    `altitude  ${P(tm.alt, 1)} km  ${P(tm.alt * 3.281, 0, 5)} kft\n` +
    `downrange ${P(tm.dr, 1)} km\n` +
    `velocity  ${P(tm.v * 1000)} m/s ${P(tm.v * 3600, 0, 6)} km/h\n` +
    `vertical  ${P(tm.vv * 1000)} m/s  path ${P(tm.gamma, 0, 3)}°\n` +
    `Mach      ${P(tm.mach, 2)}      accel ${P(tm.g, 1, 4)} g\n` +
    `dyn press ${P(tm.q / 1000, 1)} kPa${tm.maxQ ? '  ← near max-Q' : ''}\n` +
    `air       ${P(tm.tempC, 0)} °C  ${tm.p >= 1 ? P(tm.p, 0, 5) + ' hPa' : P(tm.p * 100, 1, 5) + ' Pa'}\n` +
    `density   ${tm.rho >= 0.001 ? P(tm.rho, 3) + ' kg/m³' : P(tm.rho * 1e6, 1) + ' mg/m³'}\n` +
    (tm.windKt != null ? `wind      ${P(tm.windKt, 0)} kt from ${String(Math.round(tm.windDir)).padStart(3, '0')}°\n          ${tm.head >= 0 ? 'head' : 'tail'} ${P(Math.abs(tm.head), 0, 4)} kt  cross ${P(Math.abs(tm.cross), 0, 3)} kt ${tm.cross >= 0 ? 'R' : 'L'}\n` : '') +
    (tm.tw ? `liftoff   T/W ${tm.tw.toFixed(2)}  ${(tm.thrust / 1000).toFixed(1)} MN / ${tm.mass.toFixed(0)} t\n` : '') +
    `azimuth   ${heading()}°${launch.heading == null ? ' (assumed)' : ''}  ${tm.env}\n` +
    (radar ? `radar ${new Date(radar.time).toISOString().slice(11, 19)}Z ${Math.max(0, Math.round((now() - radar.time) / 6e4))} min old · ${radar.tilts.length} tilts · VCP ${radar.site.vcp}\n      max ${num(radar.maxDbz)} dBZ at ${num(radar.maxKm, 1)} km · tops ${num(radar.echoTop * 3.281)} kft\n` : radarNote + '\n') +
    (L ? `cloud max low ${num(L.low)}% mid ${num(L.mid)}% high ${num(L.high)}%` : 'cloud model unavailable') + '\n' + trafficNote;
}

// ---------------------------------------------------------------- calculated telemetry

/** Temperature, pressure, density and wind at a height, from the model sounding over the pad. */
function atmosphere(zKm) {
  const prof = (wx.target && !wx.target.tbd && wx.profiles.t0) || wx.profiles.now;
  const lv = prof ? prof.levels.filter((l) => l.z != null && l.t != null && l.p != null) : [];
  if (lv.length < 2) { // no sounding yet: standard atmosphere
    const T = zKm < 11 ? 15 - 6.5 * zKm : -56.5, p = 1013.25 * Math.exp(-zKm / 7.2);
    return { T, p, rho: p * 100 / (287.05 * (T + 273.15)), src: 'std atmosphere' };
  }
  const z = zKm * 1000, top = lv[lv.length - 1];
  let T, p, kt = null, dir = null;
  if (z >= top.z) { // above the sounding: hold temperature, let pressure fall off with a 6.5 km scale height
    T = top.t; p = top.p * Math.exp(-(z - top.z) / 6500);
  } else {
    let i = lv.findIndex((l) => l.z > z); i = Math.max(1, i);
    const a = lv[i - 1], b = lv[i], f = Math.min(1, Math.max(0, (z - a.z) / (b.z - a.z)));
    T = a.t + (b.t - a.t) * f; p = a.p * (b.p / a.p) ** f;
    if (a.kt != null && b.kt != null) {
      // blend winds as vectors so a direction change through north does not swing the long way round
      const u = (l) => -l.kt * Math.sin(rad(l.dir)), v = (l) => -l.kt * Math.cos(rad(l.dir));
      const uu = u(a) + (u(b) - u(a)) * f, vv = v(a) + (v(b) - v(a)) * f;
      kt = Math.hypot(uu, vv); dir = (Math.atan2(-uu, -vv) * 180 / Math.PI + 360) % 360;
    }
  }
  return { T, p, rho: p * 100 / (287.05 * (T + 273.15)), kt, dir, src: wx.profiles.t0 && !wx.target.tbd ? 'T-0 sounding' : 'current sounding' };
}

/**
 * Flight state at time t from the nominal ascent profile, flown through the forecast atmosphere.
 * Speeds are ground-relative along the profile; Mach and dynamic pressure use the airspeed after
 * subtracting the wind at that height.
 */
function telemetry(t) {
  const h = 1, alt = spline(1, t), dr = spline(2, t);
  const vel = (tt) => tt <= 0 ? [0, 0] : [(spline(2, tt + h) - spline(2, Math.max(0, tt - h))) / (tt < h ? tt + h : 2 * h), (spline(1, tt + h) - spline(1, Math.max(0, tt - h))) / (tt < h ? tt + h : 2 * h)];
  const [vh, vv] = vel(t), v = Math.hypot(vh, vv);
  // acceleration over a 6 s span, which smooths the joins between profile segments
  const [h1, v1] = vel(Math.max(0, t - 3)), [h2, v2] = vel(t + 3), span = t < 3 ? t + 3 : 6;
  const ax = (h2 - h1) / span * 1000, ay = (v2 - v1) / span * 1000 + 9.81 * Math.max(0, 1 - (vh / 7.8) ** 2);
  const A = atmosphere(alt);
  let head = null, cross = null, air = v;
  if (A.kt != null) {
    const rel = rad(A.dir - heading()), ms = A.kt * 0.5144;
    head = A.kt * Math.cos(rel); cross = A.kt * Math.sin(rel);
    air = Math.hypot(vh * 1000 + ms * Math.cos(rel), ms * Math.sin(rel), vv * 1000) / 1000;
  }
  const q = 0.5 * A.rho * (air * 1000) ** 2, qPrev = 0.5 * atmosphere(spline(1, t + 4)).rho * (Math.hypot(...vel(t + 4)) * 1000) ** 2;
  const cfg = launch.nsf?.rocket_config || {}, ll = launch.ll2?.rocket?.configuration || {};
  const thrust = cfg.liftoff_thrust || ll.to_thrust, mass = ll.launch_mass;
  return { alt, dr, v, vv, gamma: v > 0.001 ? Math.atan2(vv, vh) * 180 / Math.PI : 90, g: Math.hypot(ax, ay) / 9.81, mach: air * 1000 / Math.sqrt(1.4 * 287.05 * (A.T + 273.15)),
    q, maxQ: t > 20 && q > 15000 && Math.abs(q - qPrev) / q < 0.08, tempC: A.T, p: A.p, rho: A.rho, windKt: A.kt, windDir: A.dir, head, cross,
    thrust, mass, tw: thrust && mass ? thrust / (mass * 9.81) : null, env: A.src };
}

export function init(getT) {
  tGetter = getT;
  const el = $('view3d');
  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.setClearColor(0x060709);
  el.append(renderer.domElement);
  scene = new THREE.Scene(); scene.fog = new THREE.Fog(0x060709, 2500, 7000);
  const sun = new THREE.DirectionalLight(0xffffff, 2.2); sun.position.set(0.4, 1, 0.3); // lights only affect the plane and boat models
  scene.add(new THREE.AmbientLight(0xffffff, 1.5), sun);
  camera = new THREE.PerspectiveCamera(42, 1, 1, 12000);
  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true; controls.maxPolarAngle = Math.PI / 2 - 0.02; controls.minDistance = 15; controls.maxDistance = 5000; controls.autoRotateSpeed = 0.6;
  const size = () => { const w = el.clientWidth, ht = el.clientHeight; if (!w || !ht) return; renderer.setSize(w, ht, false); camera.aspect = w / ht; camera.updateProjectionMatrix(); };
  new ResizeObserver(size).observe(el); size();

  if (document.body.classList.contains('saver')) $('v3Spin').checked = true;
  $('v3View').addEventListener('change', (e) => view(e.target.value));
  $('v3Time').addEventListener('change', loadClouds);
  $('v3Exag').addEventListener('input', (e) => { ex = +e.target.value; $('v3ExagOut').textContent = `${ex}×`; rebuild(); view(lastView); });
  $('v3Dens').addEventListener('input', (e) => { if (cloudMesh) cloudMesh.material.uniforms.dens.value = +e.target.value; });
  $('v3WindLbl').addEventListener('change', rebuild);
  $('v3T').addEventListener('input', () => { $('v3Follow').checked = false; });
  $('v3Radar').addEventListener('change', (e) => { if (radarMesh) radarMesh.visible = e.target.checked; if (e.target.checked) loadRadar(); });
  $('v3Clouds').addEventListener('change', (e) => { if (cloudMesh) cloudMesh.visible = e.target.checked; });
  $('v3MinDbz').addEventListener('input', (e) => { $('v3MinOut').textContent = e.target.value; if (radarMesh) radarMesh.material.uniforms.minDbz.value = +e.target.value; });
  $('v3RadOp').addEventListener('input', (e) => { if (radarMesh) radarMesh.material.uniforms.opacity.value = +e.target.value; });
  const bar = h('span', { class: 'dbz' }, 'Radar dBZ 5', h('b', { style: `background:linear-gradient(90deg,${DBZ_COLORS.map((c, i) => `${c} ${i / 15 * 100}% ${(i + 1) / 15 * 100}%`).join(',')})` }), '75 · NEXRAD Level II, all tilts, resampled to a 1.6 km × 0.4 km grid');
  $('v3Legend').replaceChildren(bar,
    h('span', {}, h('i', { style: 'background:#fab219' }), 'Modelled ascent corridor: generic two-stage profile flown along the published azimuth, not provider trajectory data'),
    h('span', {}, h('i', { style: 'background:#f0efec' }), 'Clouds: forecast-model cloud fraction on 14 pressure levels, ray-marched; fine texture is synthetic'),
    h('span', {}, h('i', { style: 'background:#7fb2f5' }), 'Wind arrows over the pad point downwind, length by speed'),
    h('span', {}, h('i', { style: 'background:#5fd0ff' }), 'Aircraft as miniatures of their type (light plane, business jet, airliner, heavy, helicopter), with a line down to the surface, and ', h('i', { style: 'background:#6fe36f' }), 'vessels (boat, cargo ship, tanker, passenger ship, sailing boat), dead-reckoned from the last snapshot, with a track to where they will be in 10 minutes (tick at 5 minutes, X at 10; shortened for anything fast)'),
    h('span', {}, h('i', { style: 'background:#ffd21f' }), 'NOTAM airspace and ', h('i', { style: 'background:#ff5a36' }), 'NOTMAR sea areas, plus the corridor footprint on the surface. Inside one now: flashes red and yellow. On course to be inside one at T-0: flashes red'),
    h('span', {}, 'Drag to orbit, scroll to zoom, right-drag to pan. Drag the telemetry box to move it; double-click it to put it back. Settings are under the gear.'));
  onWx(() => { if (shown && launch) { loadClouds(); } });
  setInterval(() => shown && loadClouds(), 15 * 6e4);
  setInterval(() => shown && loadRadar(), 2 * 6e4);
  setInterval(() => shown && loadTraffic(), 6e4);
  for (const id of ['v3Air', 'v3Sea', 'v3Haz']) $(id).addEventListener('change', buildTraffic);
  readoutControls();
  // The gear and Key buttons fade out after a few seconds without input, and any open menu closes.
  const host = $('view3d'), menus = [$('v3Menu'), $('v3KeyMenu')];
  let idle = 0;
  const wake = () => {
    host.classList.remove('idle'); clearTimeout(idle);
    idle = setTimeout(() => {
      if (host.querySelector('.v3-pop select:focus')) { wake(); return; } // a dropdown is open
      menus.forEach((m) => { m.open = false; }); host.classList.add('idle');
    }, 5000);
  };
  for (const ev of ['pointermove', 'pointerdown', 'keydown', 'wheel', 'input', 'change']) host.addEventListener(ev, wake, { passive: true });
  menus.forEach((m) => m.addEventListener('toggle', () => { if (m.open) menus.forEach((o) => { if (o !== m) o.open = false; }); }));
  host.addEventListener('pointerdown', (e) => { if (!e.target.closest('.v3-pop')) menus.forEach((m) => { m.open = false; }); });
  wake();
}

// ---- the telemetry overlay can be hidden, or dragged anywhere inside the view (double-click to put it back)
function readoutControls() {
  const box = $('v3Readout'), KEY = 'launch-countdown:readout';
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { /* storage unavailable */ }
  const save = () => { try { localStorage.setItem(KEY, JSON.stringify(saved)); } catch { /* ignore */ } };
  const place = () => {
    box.hidden = saved.hidden === true; $('v3Tele').checked = !box.hidden;
    box.style.left = saved.x != null ? `${saved.x * 100}%` : ''; box.style.top = saved.y != null ? `${saved.y * 100}%` : '';
  };
  place();
  $('v3Tele').addEventListener('change', (e) => { saved.hidden = !e.target.checked; save(); place(); });
  let grab = null;
  box.addEventListener('pointerdown', (e) => { const r = box.getBoundingClientRect(); grab = [e.clientX - r.left, e.clientY - r.top]; box.setPointerCapture(e.pointerId); e.preventDefault(); });
  box.addEventListener('pointermove', (e) => {
    if (!grab) return;
    const host = box.parentNode.getBoundingClientRect(), r = box.getBoundingClientRect();
    // stored as a fraction of the view so it survives resizes and the move into the terminal layout
    saved.x = Math.min(Math.max(0, e.clientX - grab[0] - host.left), Math.max(0, host.width - r.width)) / host.width;
    saved.y = Math.min(Math.max(0, e.clientY - grab[1] - host.top), Math.max(0, host.height - r.height)) / host.height;
    place();
  });
  box.addEventListener('pointerup', () => { if (grab) { grab = null; save(); } });
  box.addEventListener('dblclick', () => { delete saved.x; delete saved.y; save(); place(); });
}

export function setView(name) { $('v3View').value = name; view(name); }

export function setLaunch(l) {
  const moved = !launch || launch.pad.lat !== l.pad.lat || launch.heading !== l.heading || launch.id !== l.id;
  launch = l;
  if (!scene || l.pad.lat == null || !moved) return;
  cloudKey = ''; rebuild(); view($('v3View').value);
  if (shown) { loadClouds(); loadRadar(); }
}

export function show() {
  if (shown) return; shown = true;
  loadClouds(); loadRadar(); loadTraffic(); rebuild();
  raf = requestAnimationFrame(frame);
}
export function hide() { shown = false; cancelAnimationFrame(raf); }

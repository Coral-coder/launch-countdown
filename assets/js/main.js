// Page controller: loads the launch list, drives the clock and its HOLD / SCRUB controls,
// and fills in the mission panels. Weather lives in wx.js, the map in map.js.

import { $, h, esc, num, pad2, parts, dur, utc, eastern, local, ago, parseT, getJSON, feeds, onFeeds, inclination } from './util.js';
import { Countdown, now, syncClock, clockOffset, label } from './clock.js';
import * as weather from './wx.js';
import { initMap, showLaunch } from './map.js';

let data = null, launches = [], current = null, cd = null;

const prep = (l) => ({ ...l, netMs: parseT(l.net), openMs: parseT(l.window_open), closeMs: parseT(l.window_close) });

async function loadData() {
  try {
    data = await getJSON('Launch data (Next Spaceflight + Launch Library)', `data/launches.json?t=${Date.now()}`, { keep: false });
  } catch { if (!data) $('stateBanner').textContent = 'COULD NOT LOAD LAUNCH DATA'; return; }
  launches = data.launches.map(prep).filter((l) => l.netMs);
  const sel = $('launchSelect'), keep = current?.id;
  const opt = (l) => h('option', { value: l.id }, `${l.time_tbd ? 'NET ' + l.net.slice(0, 7) : l.net.slice(0, 16).replace('T', ' ') + 'Z'} · ${l.name} · ${l.rocket}`);
  sel.replaceChildren(h('optgroup', { label: 'Upcoming' }, launches.filter((l) => !l.past).map(opt)), h('optgroup', { label: 'Recent' }, launches.filter((l) => l.past).map(opt)));
  const wanted = keep || new URLSearchParams(location.search).get('launch');
  // Default: the next launch with a real T-0 that has not been in flight for more than two hours.
  const next = launches.find((l) => l.id === wanted) || launches.find((l) => !l.past && !l.time_tbd && l.netMs > now() - 2 * 36e5) || launches.find((l) => !l.past) || launches[0];
  select(next.id, false);
  renderSchedule();
}

function select(id, push = true) {
  const l = launches.find((x) => x.id === id);
  if (!l) return;
  const same = current?.id === id, unchanged = same && JSON.stringify(current) === JSON.stringify(l);
  current = l;
  $('launchSelect').value = id;
  if (cd && same) cd.setLaunch(l); else cd = new Countdown(l, renderControls);
  if (push) history.replaceState(null, '', `?launch=${encodeURIComponent(id)}`);
  if (!unchanged) renderLaunch();
  renderControls(); renderSchedule();
  if (l.pad.lat != null) {
    weather.setTarget({ lat: l.pad.lat, lon: l.pad.lon, t0: cd.t0, tbd: l.time_tbd, heading: l.heading, goPct: l.weather_go_pct });
    if (!same) showLaunch(l, launches);
  }
}

// ---------------------------------------------------------------- clock

const el = { sign: $('ckSign'), d: $('ckD'), hh: $('ckH'), mm: $('ckM'), ss: $('ckS'), ms: $('ckMs'), clock: $('clock'), banner: $('stateBanner'), pill: $('statusPill') };
const set = (node, text) => { if (node.textContent !== text) node.textContent = text; };

function state() {
  const official = current.status?.name || '';
  if (current.time_tbd) return ['tbd', 'NO FIRM T-0', `AWAITING A LAUNCH TIME · ${official.toUpperCase()}`];
  if (cd.mode === 'scrub') return ['scrub', 'SCRUBBED', `SCRUBBED AT ${label(cd.t).slice(0, -4)} · ${dur(cd.heldFor)} AGO`];
  if (cd.mode === 'hold') return ['hold', 'HOLD', `HOLD · HOLDING AT ${label(cd.t).slice(0, -4)} · HELD ${dur(cd.heldFor)}`];
  const over = current.closeMs && cd.t0 > current.closeMs + 999 ? ' · PAST WINDOW CLOSE' : '';
  if (cd.t >= 0) return ['flight', cd.t < 36e5 ? 'LIFTOFF' : official.toUpperCase() || 'LAUNCHED', (cd.t < 36e5 ? 'LIFTOFF · MISSION ELAPSED TIME' : `T-0 PASSED · ${official.toUpperCase()}`) + over];
  if (/hold/i.test(official)) return ['hold', 'OFFICIAL HOLD', 'COUNTING LOCALLY · OFFICIAL STATUS: HOLD' + over];
  if (/scrub/i.test(official)) return ['scrub', 'OFFICIAL SCRUB', 'OFFICIAL STATUS: SCRUB · AWAITING NEW T-0'];
  return ['go', /^go/i.test(official) ? 'GO' : 'COUNTING', (/^go/i.test(official) ? 'GO FOR LAUNCH · COUNTING' : `COUNTING · ${official.toUpperCase()}`) + (cd.slipped ? ` · T-0 SLIPPED ${dur(cd.slipped)}` : '') + over];
}

function tick() {
  requestAnimationFrame(tick);
  if (!cd) return;
  if (current.time_tbd) {
    set(el.sign, 'T-'); [el.d, el.hh, el.mm, el.ss].forEach((n) => set(n, '--')); set(el.ms, '.---');
  } else {
    const p = parts(cd.t);
    set(el.sign, p.neg ? 'T-' : 'T+');
    set(el.d, pad2(p.d)); set(el.hh, pad2(p.h)); set(el.mm, pad2(p.m)); set(el.ss, pad2(p.s));
    set(el.ms, '.' + String(p.ms).padStart(3, '0'));
  }
  const [cls, pill, banner] = state();
  set(el.banner, banner); set(el.pill, pill);
  el.banner.className = `banner ${cls}`; el.pill.className = `pill ${cls}`;
  el.clock.className = 'clock' + (cls === 'tbd' ? ' tbd' : cd.mode === 'hold' ? ' held' : cd.mode === 'scrub' ? ' scrubbed' : cd.t >= 0 ? ' plus' : '');
}

function renderControls() {
  const hold = $('btnHold'), scrub = $('btnScrub'), tbd = current.time_tbd;
  hold.textContent = cd.mode === 'hold' ? 'RESUME' : 'HOLD';
  hold.classList.toggle('active', cd.mode === 'hold');
  hold.disabled = tbd || cd.mode === 'scrub';
  scrub.textContent = cd.mode === 'scrub' ? 'RECYCLE' : 'SCRUB';
  scrub.classList.toggle('active', cd.mode === 'scrub');
  scrub.disabled = tbd;
  $('btnReset').disabled = tbd || (cd.mode === 'run' && !cd.slipped);
  [hold, scrub, $('btnReset')].forEach((b) => { b.style.opacity = b.disabled ? 0.4 : 1; });
  $('log').replaceChildren(...(cd.s.log.length ? cd.s.log.map((e) => h('div', {}, h('time', {}, utc(e.t)), e.event))
    : [h('div', { class: 'note' }, 'Nothing yet. HOLD freezes the T clock and slips T-0 when you resume; SCRUB stops the attempt until you recycle.')]));
  // A slipped T-0 changes which forecast hour matters.
  if (current.pad.lat != null && weather.wx.target && weather.wx.target.t0 !== cd.t0 && cd.mode === 'run') {
    weather.setTarget({ ...weather.wx.target, t0: cd.t0 });
  }
  renderFacts();
}

$('btnHold').addEventListener('click', () => (cd.mode === 'hold' ? cd.resume() : cd.hold()));
$('btnScrub').addEventListener('click', () => (cd.mode === 'scrub' ? cd.reset('Recycled: counting toward the published T-0') : cd.scrub()));
$('btnReset').addEventListener('click', () => cd.reset());
document.addEventListener('keydown', (e) => {
  if (e.key.toLowerCase() !== 'h' || e.metaKey || e.ctrlKey || e.altKey || /INPUT|SELECT|TEXTAREA/.test(e.target.tagName)) return;
  if (!$('btnHold').disabled) $('btnHold').click();
});
$('launchSelect').addEventListener('change', (e) => select(e.target.value));

// ---------------------------------------------------------------- once-a-second panels

const fact = (k, v, s) => h('div', { class: 'fact' }, h('div', { class: 'k' }, k), h('div', { class: 'v' }, v), s ? h('div', { class: 's' }, s) : null);

function renderFacts() {
  if (!cd) return;
  const l = current, t = now(), out = [];
  if (l.time_tbd) {
    out.push(fact('No earlier than', new Date(l.netMs).toLocaleDateString('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }), 'placeholder date, no time published'));
  } else {
    out.push(fact('T-0 (UTC)', utc(cd.t0), cd.slipped ? `published ${utc(l.netMs)}` : l.time_precise ? 'published time' : 'time may still move'),
      fact('T-0 Eastern', eastern(cd.t0)), fact('T-0 your time', local(cd.t0)));
    if (l.openMs && l.closeMs) {
      const len = l.closeMs - l.openMs;
      out.push(fact('Launch window', len < 1000 ? 'Instantaneous' : `${dur(len)} long`, len < 1000 ? '' : `${utc(l.openMs).slice(-12, -4)} – ${utc(l.closeMs).slice(-12)}`),
        fact('Window', t < l.openMs ? `opens in ${dur(l.openMs - t)}` : t < l.closeMs ? `${dur(l.closeMs - t)} left` : 'closed', cd.t0 > l.closeMs + 999 ? 'current T-0 is past window close' : ''));
    }
    if (cd.mode !== 'run' || cd.slipped) out.push(fact('L- (published T-0)', `L${cd.l < 0 ? '-' : '+'}${dur(cd.l)}`, 'wall clock, ignores holds'));
  }
  out.push(fact('Official status', l.status?.name || '—', l.ll2?.status?.name ? `Launch Library: ${l.ll2.status.name}` : ''));
  if (l.weather_go_pct != null) out.push(fact('Weather', `${l.weather_go_pct}% GO`, l.ll2?.weather_concerns || 'published probability'));
  out.push(fact('Pad', l.pad.name || '—', l.pad.location), fact('Vehicle', l.rocket || '—', l.provider));
  if (l.heading != null && l.pad.lat != null) out.push(fact('Launch azimuth', `${l.heading}°`, `≈ ${inclination(l.pad.lat, l.heading).toFixed(1)}° inclination`));
  out.push(fact('Launch data', ago(parseT(data.generated_at), t), (data.sources || []).join(' + ') + (data.stale ? ' (stale)' : '')));
  $('heroFacts').replaceChildren(...out);
  renderTimeline();
}

/** "-09:50:00", "00:02:31" or ISO-8601 "PT-38M" / "P0DT0H2M31S" -> ms relative to T-0. */
function relMs(s) {
  if (!s) return null;
  let m = /^(-?)(\d+):(\d+):(\d+)/.exec(s);
  if (m) return (m[1] ? -1 : 1) * ((+m[2] * 60 + +m[3]) * 60 + +m[4]) * 1000;
  m = /^(-?)P(?:(-?\d+)D)?T?(?:(-?\d+)H)?(?:(-?\d+)M)?(?:(-?[\d.]+)S)?$/.exec(s);
  if (!m) return null;
  const v = ((+m[2] || 0) * 86400 + (+m[3] || 0) * 3600 + (+m[4] || 0) * 60 + (+m[5] || 0)) * 1000;
  return m[1] ? -Math.abs(v) : v;
}

function timelineItems() {
  const a = (current.nsf?.timeline_items || []).map((i) => ({ t: relMs(i.relative_time), name: i.name }));
  const b = (current.ll2?.timeline || []).map((i) => ({ t: relMs(i.relative_time), name: i.type?.description || i.type?.abbrev }));
  return (a.length ? a : b).filter((i) => i.t != null && i.name).sort((x, y) => x.t - y.t);
}

function renderTimeline() {
  const items = timelineItems(), t = current.time_tbd ? -Infinity : cd.t;
  const next = items.find((i) => i.t > t);
  $('nextEvent').replaceChildren(...(next && !current.time_tbd ? ['Next: ', h('b', {}, next.name), ` at ${label(next.t).slice(0, -4)} · in ${dur(next.t - t)}`] : []));
  $('timeline').replaceChildren(...(items.length ? items.map((i) => h('div', { class: 'tl' + (i.t <= t ? ' done' : i === next ? ' next' : '') },
    h('span', { class: 't' }, label(i.t).slice(0, -4)), h('span', {}, i.name)))
    : [h('div', { class: 'note' }, 'No countdown timeline has been published for this launch.')]));
}

// ---------------------------------------------------------------- mission panels

const pretty = (k) => k.replace(/^stats_/, '').replace(/_/g, ' ').replace(/\blsp\b/, 'provider').replace(/\bnum\b/, 'number').replace(/\bcons\b/, 'consecutive').replace(/^./, (c) => c.toUpperCase());
function kv(rows) {
  const dl = h('dl', { class: 'kv' });
  for (const [k, v] of rows) {
    if (v == null || v === '' || v === -1 || (typeof v === 'number' && Number.isNaN(v))) continue;
    dl.append(h('dt', {}, k), h('dd', {}, v instanceof Node ? v : String(v)));
  }
  return dl.children.length ? dl : h('div', { class: 'note' }, 'Nothing published yet.');
}
const link = (url, text) => (url ? h('a', { href: url, target: '_blank', rel: 'noopener' }, text || url.replace(/^https?:\/\/(www\.)?/, '').slice(0, 48)) : null);
const isoDur = (s) => { const ms = relMs(s); return ms == null ? s : dur(ms); };

function youtubeId(url) {
  const m = /(?:youtube\.com\/(?:watch\?(?:.*&)?v=|live\/|embed\/)|youtu\.be\/)([\w-]{11})/.exec(url || '');
  return m && m[1];
}

function renderLaunch() {
  const l = current, n = l.nsf || {}, ll = l.ll2 || {}, cfg = n.rocket_config || {}, llcfg = ll.rocket?.configuration || {};
  document.title = `${l.name} · Florida Launch Countdown`;
  $('missionName').textContent = l.name;
  $('missionSub').textContent = [l.provider, l.rocket, l.pad.name, l.pad.location].filter(Boolean).join(' · ');
  $('heroBg').style.backgroundImage = l.image ? `url("${encodeURI(l.image)}")` : '';

  // webcast
  const vids = [[n.vid_url, 'Launch webcast'], [n.nsf_vid_url, 'NASASpaceflight stream'], ...(ll.vid_urls || []).map((v) => [v.url, v.title || v.publisher || 'Webcast']), [n.location_livestream, 'Launch site livestream']]
    .filter(([u], i, a) => u && a.findIndex((x) => x[0] === u) === i);
  const yt = vids.map(([u]) => youtubeId(u)).find(Boolean);
  const vidKey = JSON.stringify(vids);
  if ($('webcast').dataset.key !== vidKey) $('webcast').replaceChildren(yt ? h('iframe', { src: `https://www.youtube-nocookie.com/embed/${yt}?autoplay=0`, allow: 'autoplay; encrypted-media; picture-in-picture; fullscreen', allowfullscreen: '', title: 'Launch webcast' })
    : h('div', { class: 'empty' }, vids.length ? 'The published stream cannot be embedded here. Use the links below.' : 'No webcast link published yet.'));
  $('webcast').dataset.key = vidKey;
  $('webcastLinks').replaceChildren(...vids.map(([u, t]) => link(u, t)));

  // mission
  const m0 = (n.missions || [])[0] || {}, mission = ll.mission || {};
  const patch = (ll.mission_patches || [])[0]?.image_url;
  $('mission').replaceChildren(
    patch ? h('img', { class: 'patch', src: patch, alt: 'Mission patch' }) : null,
    h('p', { class: 'desc' }, m0.description || mission.description || 'No description published.'),
    n.notes ? h('p', { class: 'desc' }, `Notes: ${n.notes}`) : null,
    kv([
      ['Payloads', (n.missions || []).map((m) => m.name).join(', ')], ['Also known as', m0.alternative_names], ['Type', mission.type], ['Orbit', m0.orbit || mission.orbit?.name],
      ['Payload count', m0.payloads], ['Mass', m0.mass ? `${m0.mass.toLocaleString()} kg` : null], ['Manufacturer', m0.manufacturer], ['Operator', m0.operator],
      ['Customers', (mission.agencies || []).map((a) => a.name).join(', ')], ['Programs', (ll.program || []).map((p) => p.name).join(', ')],
      ['COSPAR ID', n.cospar_id], ['Suborbital', n.suborbital ? 'Yes' : null], ['T-0 confirmed', n.liftoff_time_unconfirmed === true ? 'No' : n.liftoff_time_unconfirmed === false ? 'Yes' : null],
      ['Failure reason', ll.failreason], ['Hashtag', ll.hashtag],
      ['Links', h('span', { class: 'links' }, link(l.nsf_url, 'Next Spaceflight'), link(n.info_url, 'Preview article'), link(m0.wiki_url, 'Wikipedia'), link(ll.flightclub_url, 'Flight Club trajectory'),
        ...(ll.info_urls || []).map((u) => link(u.url, u.title || u.source)))],
    ]));

  // vehicle
  const stages = (ll.rocket?.launcher_stage || []).map((s) => {
    const b = s.launcher || {}, land = s.landing || {};
    return h('div', { class: 'upd' }, h('b', {}, `${s.type || 'Stage'} ${b.serial_number || ''}`), kv([
      ['Flight number', s.launcher_flight_number], ['Previous flights', b.flights], ['Reused', s.reused == null ? null : s.reused ? 'Yes' : 'No'], ['Turnaround', s.turn_around_time ? isoDur(s.turn_around_time) : null],
      ['Landing', [land.type?.name, land.landing_location?.name].filter(Boolean).join(' · ')], ['Landing attempt', land.attempt == null ? null : land.attempt ? 'Yes' : 'No'], ['Downrange', land.downrange_distance ? `${Math.round(land.downrange_distance)} km` : null],
    ]));
  });
  const crew = (ll.rocket?.spacecraft_stage || []).flatMap((s) => [...(s.launch_crew || [])].map((c) => `${c.astronaut?.name} (${c.role?.role || 'crew'})`));
  $('vehicle').replaceChildren(kv([
    ['Vehicle', cfg.name || llcfg.full_name], ['Family', cfg.rocket_name || (llcfg.families || [])[0]?.name], ['Provider', n.lsp?.name || ll.launch_service_provider?.name], ['Status', cfg.status],
    ['Height', (cfg.height || llcfg.length) ? `${cfg.height || llcfg.length} m` : null], ['Diameter', (cfg.diameter || llcfg.diameter) ? `${cfg.diameter || llcfg.diameter} m` : null],
    ['Liftoff mass', llcfg.launch_mass ? `${llcfg.launch_mass} t` : null], ['Liftoff thrust', (cfg.liftoff_thrust || llcfg.to_thrust) ? `${(cfg.liftoff_thrust || llcfg.to_thrust).toLocaleString()} kN` : null],
    ['Stages', cfg.stages], ['Solid boosters', cfg.srbs || null], ['Strap-ons', cfg.strap_ons || null],
    ['Payload to LEO', (cfg.payload_leo || llcfg.leo_capacity) ? `${(cfg.payload_leo || llcfg.leo_capacity).toLocaleString()} kg` : null],
    ['Payload to GTO', (cfg.payload_gto || llcfg.gto_capacity) ? `${(cfg.payload_gto || llcfg.gto_capacity).toLocaleString()} kg` : null],
    ['Fairing', cfg.fairing_diameter ? `${cfg.fairing_diameter} m × ${cfg.fairing_height} m` : null], ['Price', cfg.price ? `$${cfg.price} M` : null],
    ['Reusable', llcfg.reusable == null ? null : llcfg.reusable ? 'Yes' : 'No'], ['Crew', crew.join(', ')],
    ['Spacecraft', (ll.rocket?.spacecraft_stage || []).map((s) => s.spacecraft?.name).filter(Boolean).join(', ')],
    ['About', llcfg.description], ['More', h('span', { class: 'links' }, link(cfg.wiki_url || llcfg.wiki_url, 'Wikipedia'), link(llcfg.info_url, 'Manufacturer page'))],
  ]), ...stages);

  // pad & recovery
  const pad = n.pad || {}, llpad = ll.pad || {};
  const rec = (n.recoveries || []).map((r) => h('div', { class: 'upd' }, h('b', {}, `Recovery: ${r.landing_zone?.name || '—'}`), kv([
    ['Zone', [r.landing_zone?.abbrev, r.landing_zone?.location].filter(Boolean).join(' · ')], ['Status', r.status], ['Vehicle', r.vehicle?.name || r.vehicle_family_name],
    ['Position', r.latitude != null ? `${r.latitude}, ${r.longitude}` : null], ['Support ship', r.support_ship?.name], ['Tug', r.tug?.name],
  ])));
  $('padInfo').replaceChildren(kv([
    ['Pad', pad.name || llpad.name], ['Site', pad.location_name || llpad.location?.name], ['Coordinates', l.pad.lat != null ? `${l.pad.lat.toFixed(5)}°, ${l.pad.lon.toFixed(5)}°` : null],
    ['Time zone', pad.timezone || llpad.location?.timezone_name], ['Launch azimuth', l.heading != null ? `${l.heading}°` : null],
    ['Fastest turnaround', pad.fastest_turnaround_time ? isoDur(pad.fastest_turnaround_time) : null], ['Turnaround this time', ll.pad_turnaround ? isoDur(ll.pad_turnaround) : null],
    ['Launches from pad', llpad.total_launch_count], ['Launches from site', llpad.location?.total_launch_count], ['Landings at site', llpad.location?.total_landing_count],
    ['Fairing recovery', n.fairing_recovery_vessel?.name], ['About the site', llpad.location?.description],
    ['More', h('span', { class: 'links' }, link(pad.wiki_url || llpad.wiki_url, 'Wikipedia'), link(l.pad.lat != null ? `https://www.google.com/maps?q=${l.pad.lat},${l.pad.lon}` : null, 'Map'))],
  ]), ...rec);

  // statistics: everything either source counts
  const statRows = Object.entries(n).filter(([k, v]) => k.startsWith('stats_') && typeof v === 'number').map(([k, v]) => [pretty(k), v]);
  const llRows = Object.entries(ll).filter(([k, v]) => /_count(_year)?$/.test(k) && typeof v === 'number').map(([k, v]) => [pretty(k), v]);
  const lsp = ll.launch_service_provider || {};
  $('statsInfo').replaceChildren(kv([...statRows, ...llRows,
    ['Provider launches', lsp.total_launch_count], ['Provider successes in a row', lsp.consecutive_successful_launches], ['Provider failures', lsp.failed_launches],
    ['Provider landings', lsp.successful_landings], ['Provider founded', n.lsp?.founding_year || lsp.founding_year], ['Provider lead', n.lsp?.administrator || lsp.administrator]]));

  $('updates').replaceChildren(...((ll.updates || []).length ? ll.updates.map((u) => h('div', { class: 'upd' }, h('time', {}, utc(parseT(u.created_on))), h('b', {}, u.created_by || ''), h('div', {}, u.comment || ''), link(u.info_url, 'source')))
    : [h('div', { class: 'note' }, 'No updates posted for this launch.')]));
  renderRaw();
}

function renderSchedule() {
  if (!current) return;
  const tbl = $('schedule');
  tbl.replaceChildren(h('tr', {}, ['T-0 (UTC)', 'In', 'Mission', 'Vehicle', 'Provider', 'Pad', 'Status', 'Weather'].map((t) => h('th', {}, t))));
  for (const l of launches) {
    tbl.append(h('tr', { class: 'click' + (l.id === current.id ? ' sel' : ''), onclick: () => { select(l.id); window.scrollTo({ top: 0 }); } },
      h('td', {}, l.time_tbd ? `NET ${new Date(l.netMs).toLocaleDateString('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' })}` : utc(l.netMs)),
      h('td', {}, l.time_tbd ? '—' : (l.netMs > now() ? dur(l.netMs - now()) : ago(l.netMs, now()))),
      h('td', {}, h('b', {}, l.name)), h('td', {}, l.rocket || '—'), h('td', {}, l.provider || '—'), h('td', {}, l.pad.name || '—'),
      h('td', {}, h('span', { class: 'dot', style: l.status?.color ? `background:${l.status.color}` : '' }), l.status?.name || '—'),
      h('td', {}, l.weather_go_pct != null ? `${l.weather_go_pct}%` : '—')));
  }
  $('dataStamp').textContent = data ? `updated ${utc(parseT(data.generated_at))}${data.errors?.length ? ' · ' + data.errors.join('; ') : ''}` : '';
}

// ---------------------------------------------------------------- raw data

function rawBlock(title, getter) {
  const pre = h('pre', { class: 'pre' });
  return h('details', { ontoggle: (e) => { if (e.target.open) pre.textContent = JSON.stringify(getter(), null, 2); } }, h('summary', {}, title), pre);
}

function renderRaw() {
  $('raw').replaceChildren(
    rawBlock('Selected launch — Next Spaceflight record', () => current.nsf),
    rawBlock('Selected launch — Launch Library 2 record', () => current.ll2),
    rawBlock('Countdown controller state', () => ({ ...cd.s, effective_t0: new Date(cd.t0).toISOString(), clock_offset_ms: clockOffset() })),
    rawBlock('Pad forecast (hourly, 17 days)', () => weather.wx.fc),
    rawBlock('Sounding profiles', () => weather.wx.profiles),
    rawBlock('Station observations', () => weather.wx.obs),
    rawBlock('Commit-criteria screen', () => weather.wx.lcc),
    rawBlock('Alerts', () => weather.wx.alerts), rawBlock('NWS forecast periods', () => weather.wx.nws),
    rawBlock('Sea state', () => weather.wx.sea), rawBlock('Tide station', () => weather.wx.tide), rawBlock('Space weather', () => weather.wx.space),
    rawBlock('Every feed: URL, last success, last error', () => Object.fromEntries([...feeds].map(([k, v]) => [k, { state: v.state, url: v.url, at: v.at && new Date(v.at).toISOString(), error: v.error }]))),
    rawBlock('Full launch list', () => data));
}

let feedTimer = null;
onFeeds(() => {
  clearTimeout(feedTimer);
  feedTimer = setTimeout(() => $('feeds').replaceChildren(...[...feeds].map(([name, f]) =>
    h('span', { class: `feed ${f.state}`, title: f.error ? `${f.error} — ${f.url}` : f.url }, name, f.at ? ` · ${ago(f.at)}` : '', f.state === 'err' ? ' · failed' : ''))), 200);
});

// ---------------------------------------------------------------- boot

initMap();
weather.start();
requestAnimationFrame(tick);
setInterval(() => {
  const d = new Date(now());
  $('utcClock').textContent = `${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}:${pad2(d.getUTCSeconds())}`;
  if (cd) renderFacts();
}, 1000);
setInterval(loadData, 60_000);
setInterval(renderSchedule, 30_000);
loadData().then(syncClock);

// Countdown controller: millisecond clock with HOLD / SCRUB, persisted per launch in localStorage.
//
//   run   - counting toward T-0 (the published time, or a slipped one after a hold)
//   hold  - T clock frozen; on resume T-0 slips by however long the hold lasted
//   scrub - attempt called off; clock frozen until RECYCLE

import { parts, pad2 } from './util.js';

const KEY = 'launch-countdown:v1:';

// Wall-clock time that cannot jump backwards mid-count: anchor Date.now() once, then advance
// with the monotonic performance clock. `offset` is an optional correction from a server Date header.
const wall0 = Date.now(), perf0 = performance.now();
let offset = 0;
export const now = () => wall0 + (performance.now() - perf0) + offset;
export const clockOffset = () => offset;

/** Rough sync against this site's own server clock; only applied when the device is clearly wrong. */
export async function syncClock() {
  try {
    const t0 = performance.now();
    const r = await fetch(`data/launches.json?sync=${Date.now()}`, { method: 'HEAD', cache: 'no-store' });
    const rtt = performance.now() - t0;
    const server = Date.parse(r.headers.get('date'));
    if (!server) return null;
    // The Date header has 1 s resolution, so the true server time is server + 0..1 s.
    const diff = server + 500 - (now() - offset - rtt / 2);
    if (Math.abs(diff) > 2000) offset = diff;
    return { diff, applied: offset !== 0, rtt };
  } catch { return null; }
}

export class Countdown {
  constructor(launch, onChange) {
    this.onChange = onChange || (() => {});
    this.setLaunch(launch);
  }

  setLaunch(launch) {
    this.launch = launch;
    this.net = launch.netMs;
    let s = null;
    try { s = JSON.parse(localStorage.getItem(KEY + launch.id)); } catch { /* storage unavailable */ }
    this.s = { mode: 'run', t0: null, frozen: null, since: null, net: this.net, log: [], ...(s || {}) };
    if (this.s.net !== this.net) {
      // The published T-0 moved since we last looked: a slipped local T-0 no longer means anything.
      if (this.s.net != null) this.log(`Published T-0 changed to ${new Date(this.net).toISOString()}`);
      this.s.net = this.net;
      this.s.t0 = null;
      if (this.s.mode === 'scrub') { this.s.mode = 'run'; this.log('Recycled automatically for the new T-0'); }
    }
    this.save();
  }

  save() {
    try { localStorage.setItem(KEY + this.launch.id, JSON.stringify(this.s)); } catch { /* ignore */ }
  }

  log(event) {
    this.s.log.unshift({ t: Math.round(now()), event });
    this.s.log.length = Math.min(this.s.log.length, 100);
  }

  get mode() { return this.s.mode; }
  /** Effective T-0 in epoch ms. */
  get t0() { return this.s.t0 ?? this.net; }
  /** Signed ms on the T clock: negative before T-0, positive after. */
  get t() { return this.s.mode === 'run' ? now() - this.t0 : this.s.frozen; }
  /** Signed ms to the published T-0 (keeps running through holds). */
  get l() { return now() - this.net; }
  get heldFor() { return this.s.mode === 'run' ? 0 : now() - this.s.since; }
  get slipped() { return this.t0 - this.net; }

  hold() {
    if (this.s.mode !== 'run') return;
    Object.assign(this.s, { mode: 'hold', frozen: this.t, since: now() });
    this.log(`HOLD called at ${label(this.s.frozen)}`);
    this.commit();
  }

  resume() {
    if (this.s.mode !== 'hold') return;
    const held = this.heldFor;
    this.s.t0 = now() - this.s.frozen; // pick the count up exactly where it stopped
    Object.assign(this.s, { mode: 'run', frozen: null, since: null });
    this.log(`Count resumed after ${Math.round(held / 1000)} s hold; T-0 now ${new Date(this.t0).toISOString()}`);
    this.commit();
  }

  scrub() {
    if (this.s.mode === 'scrub') return;
    Object.assign(this.s, { mode: 'scrub', frozen: this.t, since: now() });
    this.log(`SCRUB called at ${label(this.s.frozen)}`);
    this.commit();
  }

  /** Clear a hold/scrub and any slip: back to counting toward the published T-0. */
  reset(why = 'Clock reset to published T-0') {
    Object.assign(this.s, { mode: 'run', frozen: null, since: null, t0: null });
    this.log(why);
    this.commit();
  }

  commit() { this.save(); this.onChange(this); }
}

/** "T-00:04:12.345" style label for log lines. */
export function label(ms) {
  const p = parts(ms);
  return `T${p.neg ? '-' : '+'}${p.d ? p.d + 'd ' : ''}${pad2(p.h)}:${pad2(p.m)}:${pad2(p.s)}.${String(p.ms).padStart(3, '0')}`;
}

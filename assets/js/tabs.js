// Tabs (one section on screen at a time) and the slideshow that cycles through them.

import { $, h } from './util.js';

const KEY = 'launch-countdown:ui:v1';
let ui = { secs: 20, on: false, skip: ['raw', 'updates'], tab: 'countdown' };
try { ui = { ...ui, ...JSON.parse(localStorage.getItem(KEY) || '{}') }; } catch { /* storage unavailable */ }
const save = () => { try { localStorage.setItem(KEY, JSON.stringify(ui)); } catch { /* ignore */ } };

let panels = [], onShow = () => {}, shownAt = performance.now();
export const currentTab = () => ui.tab;

export function showTab(name, user = true) {
  if (!panels.some((p) => p.dataset.tab === name)) name = panels[0].dataset.tab;
  ui.tab = name; save();
  panels.forEach((p) => p.classList.toggle('on', p.dataset.tab === name));
  [...$('tabs').children].forEach((b) => {
    const sel = b.dataset.tab === name;
    b.setAttribute('aria-selected', sel);
    if (sel) b.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  });
  $('panels').scrollTop = 0;
  shownAt = performance.now();
  if (user) history.replaceState(null, '', location.pathname + location.search + '#' + name);
  onShow(name);
}

function step(dir = 1) {
  const names = panels.map((p) => p.dataset.tab), pool = names.filter((n) => !ui.skip.includes(n));
  const list = pool.length ? pool : names;
  // From a tab that is not in the rotation, move to the nearest one that is.
  const at = names.indexOf(ui.tab);
  let i = list.indexOf(ui.tab);
  if (i < 0) i = dir > 0 ? list.findIndex((n) => names.indexOf(n) > at) - 1 : list.findIndex((n) => names.indexOf(n) > at);
  showTab(list[((i + dir) % list.length + list.length) % list.length]);
}

function setRunning(on) {
  ui.on = on; save(); shownAt = performance.now();
  $('btnShow').textContent = on ? '❚❚ Slideshow' : '▶ Slideshow';
  $('btnShow').classList.toggle('on', on);
  if (!on) $('showBar').style.width = '0';
}

export function initTabs(cb) {
  onShow = cb;
  panels = [...document.querySelectorAll('.panel')];
  $('tabs').replaceChildren(...panels.map((p) => h('button', { role: 'tab', 'data-tab': p.dataset.tab, onclick: () => showTab(p.dataset.tab) }, p.dataset.label)));
  $('showTabs').replaceChildren(...panels.map((p) => {
    const box = h('input', { type: 'checkbox' }); box.checked = !ui.skip.includes(p.dataset.tab);
    box.addEventListener('change', () => { ui.skip = box.checked ? ui.skip.filter((n) => n !== p.dataset.tab) : [...ui.skip, p.dataset.tab]; save(); });
    return h('label', {}, box, p.dataset.label);
  }));
  const secs = $('showSecs');
  secs.value = ui.secs;
  secs.addEventListener('change', () => { ui.secs = Math.min(600, Math.max(3, +secs.value || 20)); secs.value = ui.secs; save(); shownAt = performance.now(); });
  $('btnShow').addEventListener('click', () => setRunning(!ui.on));
  document.addEventListener('keydown', (e) => {
    if (e.metaKey || e.ctrlKey || e.altKey || /INPUT|SELECT|TEXTAREA/.test(e.target.tagName) || document.querySelector('dialog[open]')) return;
    if (e.key === 'ArrowRight') step(1); else if (e.key === 'ArrowLeft') step(-1); else if (e.key.toLowerCase() === 'p') setRunning(!ui.on);
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('#showPick')) $('showPick').open = false; });
  setInterval(() => {
    if (!ui.on) return;
    // Hold the slide while a dialog is open or the page is in the background.
    if (document.querySelector('dialog[open]') || document.hidden) { shownAt = performance.now(); return; }
    const f = (performance.now() - shownAt) / (ui.secs * 1000);
    $('showBar').style.width = `${Math.min(100, f * 100)}%`;
    if (f >= 1) step(1);
  }, 100);
  setRunning(ui.on);
  showTab(location.hash.slice(1) || ui.tab, false);
}

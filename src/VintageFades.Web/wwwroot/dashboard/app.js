'use strict';
/* Vintage Fades dashboard. Plain JavaScript, no libraries. All text is added with textContent, never as HTML. */

const TZ = 'Africa/Windhoek';
const KEY_STORE = 'vf_admin_key';

const state = {
  key: safeGet(KEY_STORE),
  tab: 'today',
  date: todayStr(),
  day: null,
  barbers: [],
  catalog: [],
  report: null,
  range: 'month',
  customFrom: null,
  customTo: null
};

// ------------------------------------------------------------------ helpers

function safeGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
function safeSet(k, v) { try { v === null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { /* private mode */ } }

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'value') el.value = v;
    else if (k === 'disabled' || k === 'checked' || k === 'selected') el[k] = !!v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) {
    if (kid === null || kid === undefined || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

function todayStr() {
  return new Intl.DateTimeFormat('en-CA', { timeZone: TZ, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}
function addDays(dateStr, n) {
  const d = new Date(dateStr + 'T12:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}
function prettyDate(dateStr) {
  return new Date(dateStr + 'T12:00:00Z').toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' });
}
function shortDate(dateStr) {
  return new Date(dateStr + 'T12:00:00Z').toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' });
}
function money(n) {
  return 'N$' + Number(n || 0).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 2 });
}
function monthStart(dateStr) { return dateStr.slice(0, 8) + '01'; }
function weekStart(dateStr) {
  const d = new Date(dateStr + 'T12:00:00Z');
  const back = (d.getUTCDay() + 6) % 7; // Monday = start of week
  return addDays(dateStr, -back);
}
function rangeDates() {
  const today = todayStr();
  switch (state.range) {
    case 'today': return [today, today];
    case 'week': return [weekStart(today), today];
    case 'month': return [monthStart(today), today];
    case 'lastmonth': {
      const first = monthStart(today);
      const lastDay = addDays(first, -1);
      return [monthStart(lastDay), lastDay];
    }
    default: return [state.customFrom || monthStart(today), state.customTo || today];
  }
}

let toastTimer;
function toast(message, isError) {
  document.querySelectorAll('.toast').forEach(t => t.remove());
  const t = h('div', { class: 'toast' + (isError ? ' error' : '') }, message);
  document.body.append(t);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.remove(), isError ? 6000 : 3000);
}

async function api(path, options = {}) {
  const res = await fetch('/api/admin/' + path, {
    method: options.method || 'GET',
    headers: { 'X-Admin-Key': state.key || '', ...(options.body ? { 'Content-Type': 'application/json' } : {}) },
    body: options.body ? JSON.stringify(options.body) : undefined
  });
  if (res.status === 401) { signOut(); throw new Error('Please sign in again.'); }
  if (res.status === 429) throw new Error('Too many requests. Wait a minute and try again.');
  if (res.status === 204) return null;
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const detail = data && (data.message || data.detail || (data.errors && Object.values(data.errors).flat()[0]) || data.error || data.title);
    throw new Error(detail || ('Something went wrong (' + res.status + ').'));
  }
  return data;
}

async function run(button, work) {
  if (button) button.disabled = true;
  try { await work(); } catch (e) { toast(e.message, true); } finally { if (button) button.disabled = false; }
}

function signOut() {
  safeSet(KEY_STORE, null);
  state.key = null;
  render();
}

// ------------------------------------------------------------------ sheets (pop-up panels)

function openSheet(title, body, buttons) {
  const close = () => backdrop.remove();
  const backdrop = h('div', { class: 'backdrop', onclick: e => { if (e.target === backdrop) close(); } },
    h('div', { class: 'sheet', role: 'dialog', 'aria-label': title },
      h('header', {}, h('h2', {}, title), h('button', { class: 'iconbtn', 'aria-label': 'Close', onclick: close }, '✕')),
      h('div', { class: 'body' }, body),
      buttons && buttons.length ? h('footer', {}, buttons.map(b => h('button', { class: 'btn ' + (b.kind || ''), onclick: ev => b.onClick(ev.currentTarget, close) }, b.label))) : null));
  document.body.append(backdrop);
  return close;
}

// ------------------------------------------------------------------ service picker

function servicePicker() {
  const picked = []; // { name, price }
  const search = h('input', { type: 'search', placeholder: 'Search services…', 'aria-label': 'Search services' });
  const list = h('div', { class: 'picklist' });
  const pickedBox = h('div', { class: 'picked' });
  const totalEl = h('span', {}, money(0));
  const other = h('button', { class: 'btn ghost small', type: 'button', onclick: addOther }, '+ Other service');

  function addLine(name, price) { picked.push({ name, price }); drawPicked(); }

  function drawList() {
    const q = search.value.trim().toLowerCase();
    list.replaceChildren();
    for (const cat of state.catalog) {
      const items = cat.items.filter(i => !q || i.name.toLowerCase().includes(q) || cat.category.toLowerCase().includes(q));
      if (!items.length) continue;
      list.append(h('div', { class: 'cat' }, cat.category));
      for (const i of items) list.append(h('button', { class: 'pick', type: 'button', onclick: () => addLine(i.name, i.price) }, h('span', {}, i.name), h('span', { class: 'muted' }, i.display)));
    }
    if (!list.children.length) list.append(h('div', { class: 'empty' }, 'No match. Use “Other service”.'));
  }

  function drawPicked() {
    pickedBox.replaceChildren();
    picked.forEach((p, idx) => {
      const price = h('input', { type: 'number', inputmode: 'decimal', min: '0', step: '1', value: String(p.price), 'aria-label': 'Price for ' + p.name });
      price.addEventListener('input', () => { p.price = Number(price.value) || 0; drawTotal(); });
      pickedBox.append(h('div', { class: 'p' }, h('span', {}, p.name), price, h('button', { class: 'x', type: 'button', 'aria-label': 'Remove ' + p.name, onclick: () => { picked.splice(idx, 1); drawPicked(); } }, '✕')));
    });
    drawTotal();
  }
  function drawTotal() { totalEl.textContent = money(picked.reduce((s, p) => s + (Number(p.price) || 0), 0)); }

  function addOther() {
    const name = h('input', { type: 'text', placeholder: 'Service name', maxlength: '200' });
    const price = h('input', { type: 'number', inputmode: 'decimal', min: '0', step: '1', placeholder: 'Price (N$)' });
    openSheet('Other service', [h('label', {}, 'Name', name), h('label', {}, 'Price', price)], [
      { label: 'Add', onClick: (btn, close) => {
        if (!name.value.trim()) return toast('Type a name.', true);
        addLine(name.value.trim(), Number(price.value) || 0); close();
      } }
    ]);
  }

  search.addEventListener('input', drawList);
  drawList();
  const el = h('div', { class: 'body', style: null },
    h('label', {}, 'Services done', search), list,
    h('div', { class: 'picked-wrap' }, pickedBox),
    other,
    h('div', { class: 'total' }, h('span', {}, 'Total'), totalEl));
  return { el, lines: () => picked.map(p => ({ name: p.name, price: Number(p.price) || 0 })) };
}

function barberSelect(selectedId, extraFirst) {
  const sel = h('select', {}, extraFirst ? h('option', { value: '' }, extraFirst) : null,
    state.barbers.filter(b => b.isActive).map(b => h('option', { value: String(b.id), selected: b.id === selectedId }, b.displayName)));
  return sel;
}

// ------------------------------------------------------------------ actions

function doneSheet(item) {
  const picker = servicePicker();
  const sel = barberSelect(item.barberId);
  openSheet('Done: ' + (item.customer || 'Customer') + ' · ' + item.time, [
    h('label', {}, 'Who did the service?', sel), ...picker.el.children
  ], [
    { label: 'Save', onClick: (btn, close) => run(btn, async () => {
      const services = picker.lines();
      if (!services.length) throw new Error('Pick at least one service.');
      await api('bookings/' + encodeURIComponent(item.reference) + '/complete', { method: 'POST', body: { barberId: Number(sel.value), services } });
      close(); toast('Saved.'); await loadDay();
    }) }
  ]);
}

function walkInSheet() {
  const picker = servicePicker();
  const name = h('input', { type: 'text', placeholder: 'Customer name (optional)', maxlength: '100' });
  const sel = barberSelect(state.barbers.find(b => b.isActive)?.id);
  const date = h('input', { type: 'date', value: state.date, max: todayStr() });
  openSheet('Walk-in / sale', [
    h('label', {}, 'Customer', name),
    h('div', { class: 'field-row' }, h('label', {}, 'Barber', sel), h('label', {}, 'Date', date)),
    ...picker.el.children
  ], [
    { label: 'Save', onClick: (btn, close) => run(btn, async () => {
      const services = picker.lines();
      if (!services.length) throw new Error('Pick at least one service.');
      await api('sales', { method: 'POST', body: { barberId: Number(sel.value), services, customerName: name.value.trim() || null, date: date.value || null } });
      close(); toast('Saved.'); await loadDay();
    }) }
  ]);
}

function phoneBookingSheet() {
  const name = h('input', { type: 'text', placeholder: 'Customer name', maxlength: '100' });
  const phone = h('input', { type: 'tel', placeholder: 'Phone (optional), e.g. 081 234 5678' });
  const sel = barberSelect(null, 'First available');
  const date = h('input', { type: 'date', value: state.date < todayStr() ? todayStr() : state.date, min: todayStr() });
  const notes = h('input', { type: 'text', placeholder: 'Notes (optional)', maxlength: '500' });
  const slotBox = h('div', { class: 'chips' });
  let chosen = null;

  async function loadSlots() {
    chosen = null;
    slotBox.replaceChildren(h('span', { class: 'muted' }, 'Loading…'));
    try {
      const barber = sel.value ? '&barberId=' + sel.value : '';
      const slots = await api('slots?date=' + date.value + barber);
      const times = [...new Set(slots.map(s => s.time))].sort();
      slotBox.replaceChildren();
      if (!times.length) slotBox.append(h('span', { class: 'muted' }, 'No open slots on this day.'));
      for (const t of times) {
        const chip = h('button', { class: 'chip', type: 'button', onclick: () => {
          chosen = t;
          slotBox.querySelectorAll('.chip').forEach(c => c.classList.toggle('on', c === chip));
        } }, t);
        slotBox.append(chip);
      }
    } catch (e) { slotBox.replaceChildren(h('span', { class: 'muted' }, e.message)); }
  }
  sel.addEventListener('change', loadSlots);
  date.addEventListener('change', loadSlots);
  loadSlots();

  openSheet('Phone booking', [
    h('label', {}, 'Customer name', name), h('label', {}, 'Phone', phone),
    h('div', { class: 'field-row' }, h('label', {}, 'Barber', sel), h('label', {}, 'Date', date)),
    h('label', {}, 'Pick a time', slotBox), h('label', {}, 'Notes', notes)
  ], [
    { label: 'Book it', onClick: (btn, close) => run(btn, async () => {
      if (!name.value.trim()) throw new Error('Type the customer name.');
      if (!chosen) throw new Error('Pick a time.');
      const res = await api('bookings', { method: 'POST', body: {
        customerName: name.value.trim(), phoneNumber: phone.value.trim() || null, date: date.value, time: chosen,
        barberId: sel.value ? Number(sel.value) : null, notes: notes.value.trim() || null } });
      close();
      state.date = date.value;
      toast('Booked ' + res.reference + ' with ' + res.barber + ' at ' + res.time + '. The bot now sees this slot as taken.');
      state.tab = 'today';
      await loadDay();
    }) }
  ]);
}

function fabMenu() {
  openSheet('Add', [
    h('button', { class: 'btn', onclick: () => { document.querySelector('.backdrop')?.remove(); phoneBookingSheet(); } }, '📞  Phone booking'),
    h('button', { class: 'btn ghost', onclick: () => { document.querySelector('.backdrop')?.remove(); walkInSheet(); } }, '✂️  Walk-in / sale')
  ]);
}

// ------------------------------------------------------------------ today view

function statusBadge(s) { return h('span', { class: 'badge ' + s }, s === 'NoShow' ? 'No-show' : s); }

function appointmentCard(item) {
  const lines = item.services.map(s => h('div', { class: 'line' },
    h('span', {}, s.name + (s.barber !== item.barber ? ' · ' + s.barber : '')),
    h('span', {}, money(s.price), ' ', h('button', { class: 'x', 'aria-label': 'Remove ' + s.name, onclick: () => run(null, async () => {
      if (!confirm('Remove “' + s.name + '” from the records?')) return;
      await api('sales/' + s.id, { method: 'DELETE' }); await loadDay();
    }) }, '✕'))));

  const actions = [];
  if (item.status === 'Confirmed') {
    actions.push(h('button', { class: 'btn small', onclick: () => doneSheet(item) }, '✓ Done'));
    actions.push(h('button', { class: 'btn ghost small', onclick: ev => run(ev.currentTarget, async () => {
      if (!confirm('Mark ' + (item.customer || 'this customer') + ' as a no-show?')) return;
      await api('bookings/' + encodeURIComponent(item.reference) + '/no-show', { method: 'POST' }); await loadDay();
    }) }, 'No-show'));
    actions.push(h('button', { class: 'btn danger small', onclick: ev => run(ev.currentTarget, async () => {
      if (!confirm('Cancel this appointment? The customer is told on their chat.')) return;
      await api('bookings/' + encodeURIComponent(item.reference) + '/cancel', { method: 'POST', body: { reason: 'Cancelled by the shop' } }); await loadDay();
    }) }, 'Cancel'));
  } else if (item.status === 'Completed') {
    actions.push(h('button', { class: 'btn ghost small', onclick: () => doneSheet(item) }, '+ Add service'));
  }

  return h('div', { class: 'card s-' + item.status },
    h('div', { class: 'row' }, h('span', { class: 'time' }, item.time + ' – ' + item.endTime), statusBadge(item.status)),
    h('div', { class: 'row' }, h('span', { class: 'who' }, item.customer || 'Customer'), h('span', { class: 'badge barber' }, item.barber)),
    item.phone ? h('div', { class: 'muted' }, item.phone + (item.source === 'Admin' ? ' · phone booking' : ' · ' + item.source)) : h('div', { class: 'muted' }, item.source === 'Admin' ? 'Phone booking' : item.source),
    item.notes ? h('div', { class: 'muted' }, '“' + item.notes + '”') : null,
    lines.length ? h('div', { class: 'lines' }, lines) : null,
    item.total > 0 ? h('div', { class: 'row', style: null }, h('span', { class: 'muted' }, 'Total'), h('b', {}, money(item.total))) : null,
    actions.length ? h('div', { class: 'actions' }, actions) : null);
}

function walkInCard(w) {
  return h('div', { class: 'card walkin' },
    h('div', { class: 'row' }, h('span', { class: 'time' }, w.time), h('span', { class: 'badge' }, 'Walk-in')),
    h('div', { class: 'who' }, w.customer || 'Walk-in customer'),
    h('div', { class: 'lines' }, w.services.map(s => h('div', { class: 'line' },
      h('span', {}, s.name + ' · ' + s.barber),
      h('span', {}, money(s.price), ' ', h('button', { class: 'x', 'aria-label': 'Remove ' + s.name, onclick: () => run(null, async () => {
        if (!confirm('Remove “' + s.name + '” from the records?')) return;
        await api('sales/' + s.id, { method: 'DELETE' }); await loadDay();
      }) }, '✕'))))));
}

function todayView() {
  const d = state.day;
  const wrap = h('div', {});
  if (!d) return h('div', { class: 'empty' }, 'Loading…');
  const upcoming = d.appointments.filter(a => a.status === 'Confirmed').length;
  const done = d.appointments.filter(a => a.status === 'Completed').length;
  wrap.append(h('div', { class: 'summary' },
    h('div', { class: 'stat' }, h('div', { class: 'num' }, money(d.revenue)), h('div', { class: 'lbl' }, 'Earned')),
    h('div', { class: 'stat' }, h('div', { class: 'num' }, upcoming + ' / ' + done), h('div', { class: 'lbl' }, 'Waiting / done'))));
  if (!d.appointments.length && !d.walkIns.length) wrap.append(h('div', { class: 'empty' }, 'Nothing booked for this day yet.'));
  d.appointments.forEach(a => wrap.append(appointmentCard(a)));
  if (d.walkIns.length) { wrap.append(h('div', { class: 'section-title' }, 'Walk-ins')); d.walkIns.forEach(w => wrap.append(walkInCard(w))); }
  return wrap;
}

// ------------------------------------------------------------------ reports view

function chips(options, current, onPick) {
  return h('div', { class: 'chips' }, options.map(([id, label]) => h('button', { class: 'chip' + (id === current ? ' on' : ''), onclick: () => onPick(id) }, label)));
}

function reportView() {
  const wrap = h('div', {});
  wrap.append(chips([['today', 'Today'], ['week', 'This week'], ['month', 'This month'], ['lastmonth', 'Last month'], ['custom', 'Custom']], state.range, async id => {
    state.range = id; render(); if (id !== 'custom') await loadReport();
  }));
  if (state.range === 'custom') {
    const from = h('input', { type: 'date', value: state.customFrom || monthStart(todayStr()) });
    const to = h('input', { type: 'date', value: state.customTo || todayStr() });
    wrap.append(h('div', { class: 'field-row', style: null }, h('label', {}, 'From', from), h('label', {}, 'To', to)),
      h('div', { class: 'actions' }, h('button', { class: 'btn small', onclick: () => { state.customFrom = from.value; state.customTo = to.value; run(null, loadReport); } }, 'Show')));
  }
  const r = state.report;
  if (!r) { wrap.append(h('div', { class: 'empty' }, 'Loading…')); return wrap; }

  wrap.append(h('div', { class: 'section-title' }, shortDate(r.from) + ' – ' + shortDate(r.to)));
  wrap.append(h('div', { class: 'summary' },
    stat(money(r.revenue), 'Revenue'), stat(r.visits, 'Customers served'),
    stat(r.services, 'Services done'), stat(money(r.averageVisit), 'Average per customer'),
    stat(r.noShows, 'No-shows'), stat(r.cancelled, 'Cancelled')));

  wrap.append(h('div', { class: 'section-title' }, 'By barber'));
  if (!r.byBarber.length) wrap.append(h('div', { class: 'empty' }, 'No services recorded in this period.'));
  const maxB = Math.max(1, ...r.byBarber.map(b => b.revenue));
  r.byBarber.forEach((b, i) => {
    const bar = h('i', {}); bar.style.width = Math.max(2, (b.revenue / maxB) * 100) + '%';
    wrap.append(h('div', { class: 'barrow' },
      h('div', { class: 'top' }, h('span', {}, (i === 0 ? '🥇 ' : '') + b.barber), h('span', {}, money(b.revenue))),
      h('div', { class: 'muted' }, b.visits + ' customers · ' + b.services + ' services' + (r.revenue > 0 ? ' · ' + Math.round((b.revenue / r.revenue) * 100) + '%' : '')),
      h('div', { class: 'bar' }, bar)));
  });

  if (r.byService.length) {
    wrap.append(h('div', { class: 'section-title' }, 'By service'));
    const maxS = Math.max(1, ...r.byService.map(s => s.revenue));
    r.byService.slice(0, 12).forEach(s => {
      const bar = h('i', {}); bar.style.width = Math.max(2, (s.revenue / maxS) * 100) + '%';
      wrap.append(h('div', { class: 'barrow' },
        h('div', { class: 'top' }, h('span', {}, s.service), h('span', {}, money(s.revenue))),
        h('div', { class: 'muted' }, s.count + ' × '), h('div', { class: 'bar gold' }, bar)));
    });
  }

  if (r.byDay.length > 1) {
    wrap.append(h('div', { class: 'section-title' }, 'By day'));
    const maxD = Math.max(1, ...r.byDay.map(x => x.revenue));
    const chart = h('div', { class: 'chart' });
    r.byDay.forEach(x => { const c = h('div', { class: 'col', title: shortDate(x.date) + ': ' + money(x.revenue) }); c.style.height = Math.max(3, (x.revenue / maxD) * 100) + '%'; chart.append(c); });
    wrap.append(chart, h('div', { class: 'chart-labels' }, h('span', {}, shortDate(r.byDay[0].date)), h('span', {}, shortDate(r.byDay[r.byDay.length - 1].date))));
  }

  wrap.append(h('div', { class: 'actions' }, h('button', { class: 'btn ghost small', onclick: () => downloadCsv(r) }, '⬇ Download as spreadsheet (CSV)')));
  return wrap;
}
function stat(num, label) { return h('div', { class: 'stat' }, h('div', { class: 'num' }, String(num)), h('div', { class: 'lbl' }, label)); }

function downloadCsv(r) {
  const esc = v => '"' + String(v).replaceAll('"', '""') + '"';
  const rows = [['Report', r.from + ' to ' + r.to], [], ['Barber', 'Customers', 'Services', 'Revenue (N$)'],
    ...r.byBarber.map(b => [b.barber, b.visits, b.services, b.revenue]), [], ['Service', 'Count', 'Revenue (N$)'],
    ...r.byService.map(s => [s.service, s.count, s.revenue]), [], ['Date', 'Services', 'Revenue (N$)'], ...r.byDay.map(d => [d.date, d.services, d.revenue])];
  const blob = new Blob([rows.map(row => row.map(esc).join(',')).join('\r\n')], { type: 'text/csv' });
  const a = h('a', { href: URL.createObjectURL(blob), download: 'vintage-fades-' + r.from + '-to-' + r.to + '.csv' });
  document.body.append(a); a.click(); a.remove();
}

// ------------------------------------------------------------------ loading + rendering

async function loadDay() {
  state.day = await api('day?date=' + state.date);
  render();
}
async function loadReport() {
  const [from, to] = rangeDates();
  state.report = null; render();
  state.report = await api('reports?from=' + from + '&to=' + to);
  render();
}
async function loadBasics() {
  const [barbers, catalog] = await Promise.all([api('barbers'), api('catalog')]);
  state.barbers = barbers; state.catalog = catalog;
}

function loginView() {
  const input = h('input', { type: 'password', placeholder: 'Admin key', autocomplete: 'current-password', 'aria-label': 'Admin key' });
  const error = h('div', { class: 'muted' });
  const form = h('form', { onsubmit: async ev => {
    ev.preventDefault();
    error.textContent = '';
    state.key = input.value.trim();
    const res = await fetch('/api/admin/status', { headers: { 'X-Admin-Key': state.key } }).catch(() => null);
    if (res && res.ok) { safeSet(KEY_STORE, state.key); await start(); return; }
    state.key = null;
    error.textContent = !res ? 'Can’t reach the server.' : res.status === 429 ? 'Too many tries. Wait a minute.' : 'That key was not accepted. Check it matches Admin__ApiKey in Railway.';
  } }, h('label', {}, 'Admin key', input), h('button', { class: 'btn', type: 'submit' }, 'Open dashboard'), error);
  return h('div', { class: 'login' }, h('img', { src: 'icon.svg', alt: '' }), h('h1', {}, 'Vintage Fades'), h('div', { class: 'muted' }, 'Appointments and earnings'), form);
}

function render() {
  const app = document.getElementById('app');
  app.replaceChildren();
  document.querySelectorAll('.tabs, .fab').forEach(e => e.remove());
  if (!state.key) { app.append(loginView()); return; }

  const header = h('div', { class: 'topbar' },
    h('div', { class: 'topbar-row' }, h('div', { class: 'brand' }, 'Vintage Fades', h('small', {}, state.tab === 'today' ? 'Appointments' : 'Earnings')),
      h('button', { class: 'iconbtn', 'aria-label': 'Sign out', onclick: () => { if (confirm('Sign out?')) signOut(); } }, '⎋')));
  if (state.tab === 'today') {
    header.append(h('div', { class: 'datenav' },
      h('button', { class: 'iconbtn', 'aria-label': 'Previous day', onclick: () => { state.date = addDays(state.date, -1); state.day = null; run(null, loadDay); render(); } }, '‹'),
      h('button', { class: 'label iconbtn', style: null, onclick: () => { state.date = todayStr(); state.day = null; run(null, loadDay); render(); } }, state.date === todayStr() ? 'Today · ' + shortDate(state.date) : prettyDate(state.date)),
      h('button', { class: 'iconbtn', 'aria-label': 'Next day', onclick: () => { state.date = addDays(state.date, 1); state.day = null; run(null, loadDay); render(); } }, '›')));
  }
  app.append(header, h('main', {}, state.tab === 'today' ? todayView() : reportView()));

  document.body.append(
    h('div', { class: 'tabs' },
      h('button', { class: state.tab === 'today' ? 'active' : '', onclick: () => { state.tab = 'today'; run(null, loadDay); render(); } }, h('span', {}, '📅'), 'Today'),
      h('button', { class: state.tab === 'reports' ? 'active' : '', onclick: () => { state.tab = 'reports'; render(); run(null, loadReport); } }, h('span', {}, '📈'), 'Earnings')),
    h('button', { class: 'fab', 'aria-label': 'Add', onclick: fabMenu }, '+'));
}

async function start() {
  render();
  await run(null, async () => { await loadBasics(); await loadDay(); });
}

if (state.key) start(); else render();

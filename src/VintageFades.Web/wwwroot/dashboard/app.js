'use strict';
/* Vintage Fades dashboard. Plain JavaScript, no libraries. All text is added with textContent, never as HTML. */

const TZ = 'Africa/Windhoek';
const KEY_STORE = 'vf_admin_key';

const state = {
  key: safeGet(KEY_STORE),
  tab: 'dashboard',
  date: todayStr(),
  day: null,
  barbers: [],
  catalog: [],
  report: null,
  range: 'month',
  customFrom: null,
  customTo: null,
  // dashboard overview
  dashRange: 'month',
  dash: null,        // { report, today }
  // calendar
  weekStart: null,
  week: null,
  calBarber: ''
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

function phoneBookingSheet(pre = {}) {
  const name = h('input', { type: 'text', placeholder: 'Customer name', maxlength: '100' });
  const phone = h('input', { type: 'tel', placeholder: 'Phone (optional), e.g. 081 234 5678' });
  const sel = barberSelect(pre.barberId ?? null, 'First available');
  const startDate = pre.date || state.date;
  const date = h('input', { type: 'date', value: startDate < todayStr() ? todayStr() : startDate, min: todayStr() });
  const notes = h('input', { type: 'text', placeholder: 'Notes (optional)', maxlength: '500' });
  const slotBox = h('div', { class: 'chips' });
  let chosen = pre.time || null;

  async function loadSlots() {
    slotBox.replaceChildren(h('span', { class: 'muted' }, 'Loading…'));
    try {
      const barber = sel.value ? '&barberId=' + sel.value : '';
      const slots = await api('slots?date=' + date.value + barber);
      const times = [...new Set(slots.map(s => s.time))].sort();
      if (!times.includes(chosen)) chosen = null;
      slotBox.replaceChildren();
      if (!times.length) slotBox.append(h('span', { class: 'muted' }, 'No open slots on this day.'));
      for (const t of times) {
        const chip = h('button', { class: 'chip' + (t === chosen ? ' on' : ''), type: 'button', onclick: () => {
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
      toast('Booked ' + res.reference + ' with ' + res.barber + ' at ' + res.time + '. The bot and the website now see this slot as taken.');
      await refresh();
    }) }
  ]);
}

function fabMenu() {
  openSheet('Add', [
    h('button', { class: 'btn', onclick: () => { document.querySelector('.backdrop')?.remove(); phoneBookingSheet(); } }, '📞  Phone booking'),
    h('button', { class: 'btn ghost', onclick: () => { document.querySelector('.backdrop')?.remove(); walkInSheet(); } }, '✂️  Walk-in / sale')
  ]);
}

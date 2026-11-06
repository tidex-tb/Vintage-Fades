'use strict';
/* Vintage Fades public site. Plain JavaScript. All text is added with textContent, never as HTML. */

const $ = id => document.getElementById(id);
const state = { info: null, barberId: null, barberName: 'Any available barber', date: null, time: null, availability: [], booked: null, found: null };

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) if (kid !== null && kid !== undefined && kid !== false) el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  return el;
}

async function api(path, body) {
  const res = await fetch('/api/public/' + path, body
    ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
    : undefined);
  if (res.status === 204) return null;
  if (res.status === 429) throw new Error('Too many tries. Please wait a few minutes and try again.');
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) { const e = new Error((data && data.message) || 'Something went wrong. Please try again.'); e.code = data && data.error; throw e; }
  return data;
}

function prettyDate(d, long) {
  return new Date(d + 'T12:00:00Z').toLocaleDateString('en-GB', { weekday: long ? 'long' : 'short', day: 'numeric', month: 'long', timeZone: 'UTC' });
}

// ------------------------------------------------------------------ static content

function renderInfo(info) {
  document.title = info.shopName + ' · Windhoek';
  $('foot-name').textContent = info.shopName;
  $('year').textContent = new Date().getFullYear();
  $('s-length').textContent = info.durationMinutes + ' minutes';

  const facts = $('facts');
  facts.replaceChildren(
    h('li', {}, '🕘 ' + info.hours),
    info.address ? h('li', {}, '📍 ' + info.address.split(',').slice(-2).join(',').trim()) : null,
    h('li', {}, '✂️ ' + info.barbers.length + ' barbers'));

  // prices
  const tabs = $('cat-tabs');
  const list = $('price-list');
  const show = idx => {
    tabs.querySelectorAll('.tab').forEach((t, i) => t.setAttribute('aria-selected', String(i === idx)));
    const cat = info.catalog[idx];
    list.replaceChildren(
      cat.note ? h('div', { class: 'cat-note' }, cat.note) : null,
      ...cat.items.map(i => h('div', { class: 'price-row' }, h('div', {}, i.name, i.details ? h('small', {}, i.details) : null), h('b', {}, i.price))));
  };
  info.catalog.forEach((c, idx) => tabs.append(h('button', { class: 'tab', type: 'button', role: 'tab', 'aria-selected': 'false', onclick: () => show(idx) }, c.category)));
  if (info.catalog.length) show(0);
  $('notes').replaceChildren(...info.notes.map(n => h('li', {}, n)));

  // team
  $('team').replaceChildren(...info.barbers.map(b => h('div', { class: 'member' },
    h('div', { class: 'avatar', 'aria-hidden': 'true' }, b.name.slice(0, 1).toUpperCase()),
    h('h3', {}, b.name), h('p', {}, 'Barber'),
    h('a', { class: 'btn ghost small', href: '#book', onclick: () => pickBarber(b.id, b.name) }, 'Book ' + b.name))));

  // contact
  const rows = [];
  if (info.address) rows.push(['📍', info.address, null]);
  if (info.phone) rows.push(['📞', info.phone, 'tel:' + info.phone.replace(/[^\d+]/g, '')]);
  if (info.email) rows.push(['✉️', info.email, 'mailto:' + info.email]);
  if (info.instagram) rows.push(['📷', info.instagram, 'https://instagram.com/' + info.instagram.replace('@', '')]);
  rows.push(['🕘', info.hours, null]);
  $('contact').replaceChildren(...rows.map(([ic, text, href]) => h('li', {}, h('span', { class: 'ic', 'aria-hidden': 'true' }, ic),
    href ? h('a', { href, ...(href.startsWith('http') ? { target: '_blank', rel: 'noopener' } : {}) }, text) : h('span', {}, text))));
  if (info.address) $('map-address').textContent = info.address.split(',').slice(-2).join(',').trim() || info.address;
}

// ------------------------------------------------------------------ booking flow

function radioGroup(container, selector) {
  // Arrow keys move between options, as a real radio group does.
  container.addEventListener('keydown', e => {
    if (!['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp'].includes(e.key)) return;
    const items = [...container.querySelectorAll(selector + ':not(:disabled)')];
    const i = items.indexOf(document.activeElement);
    if (i < 0) return;
    e.preventDefault();
    items[(i + (e.key === 'ArrowRight' || e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length].focus();
  });
}

function renderBarberPicks() {
  const box = $('barbers-pick');
  const options = [{ id: null, name: 'Any available barber' }, ...state.info.barbers];
  box.replaceChildren(...options.map(b => h('button', { class: 'chip', type: 'button', role: 'radio', 'aria-checked': String(b.id === state.barberId),
    onclick: () => pickBarber(b.id, b.name) }, b.name)));
}

function pickBarber(id, name) {
  state.barberId = id; state.barberName = id === null ? 'Any available barber' : name;
  state.time = null;
  $('s-barber').textContent = state.barberName;
  renderBarberPicks();
  loadAvailability();
}

async function loadAvailability() {
  const days = $('days');
  days.replaceChildren(h('div', { class: 'skeleton' }));
  try {
    state.availability = await api('availability' + (state.barberId ? '?barberId=' + state.barberId : ''));
  } catch (e) { days.replaceChildren(h('p', { class: 'muted' }, e.message)); return; }
  if (state.date && !(state.availability.find(d => d.date === state.date)?.freeSlots > 0)) state.date = null;
  if (!state.date) state.date = state.availability.find(d => d.freeSlots > 0)?.date || null;
  renderDays();
  await loadTimes();
}

function renderDays() {
  const days = $('days');
  if (!state.availability.length) { days.replaceChildren(h('p', { class: 'muted' }, 'No days to show right now.')); return; }
  days.replaceChildren(...state.availability.map(d => {
    const dt = new Date(d.date + 'T12:00:00Z');
    return h('button', { class: 'day', type: 'button', role: 'radio', disabled: d.freeSlots === 0, 'aria-checked': String(d.date === state.date),
      'aria-label': prettyDate(d.date, true) + (d.freeSlots ? ', ' + d.freeSlots + ' open' : ', fully booked'),
      onclick: () => { state.date = d.date; state.time = null; renderDays(); loadTimes(); } },
      h('span', {}, dt.toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' })),
      h('b', {}, dt.getUTCDate()),
      h('small', {}, d.freeSlots === 0 ? 'Full' : dt.toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' })));
  }));
  days.querySelector('[aria-checked="true"]')?.scrollIntoView({ block: 'nearest', inline: 'center' });
}

async function loadTimes() {
  const box = $('times');
  updateSummary();
  if (!state.date) { box.replaceChildren(h('p', { class: 'muted' }, state.availability.length ? 'Fully booked for the coming weeks. Please call the shop.' : 'Pick a day to see open times.')); return; }
  box.replaceChildren(h('div', { class: 'skeleton' }));
  let slots;
  try { slots = await api('slots?date=' + state.date + (state.barberId ? '&barberId=' + state.barberId : '')); }
  catch (e) { box.replaceChildren(h('p', { class: 'muted' }, e.message)); return; }
  if (state.time && !slots.some(s => s.time === state.time)) state.time = null;
  $('time-note').textContent = slots.length ? '· ' + slots.length + ' open' : '';
  if (!slots.length) { box.replaceChildren(h('p', { class: 'muted' }, 'No open times on this day. Try another day.')); updateSummary(); return; }
  box.replaceChildren(...slots.map(s => h('button', { class: 'time', type: 'button', role: 'radio', 'aria-checked': String(s.time === state.time),
    onclick: () => { state.time = s.time; loadTimesChecked(); } },
    s.time, state.barberId === null && s.barbers.length > 1 ? h('small', {}, s.barbers.length + ' barbers') : null)));
  updateSummary();
}
function loadTimesChecked() {
  $('times').querySelectorAll('.time').forEach(b => b.setAttribute('aria-checked', String(b.firstChild.textContent === state.time)));
  updateSummary();
}

function updateSummary() {
  $('s-date').textContent = state.date ? prettyDate(state.date, true) : '-';
  $('s-time').textContent = state.time || '-';
  $('submit').disabled = !(state.date && state.time);
}

async function submitBooking(ev) {
  ev.preventDefault();
  const err = $('form-error');
  err.textContent = '';
  const name = $('f-name').value.trim(), phone = $('f-phone').value.trim();
  if (!name) { err.textContent = 'Please type your name.'; $('f-name').focus(); return; }
  if (phone.replace(/\D/g, '').length < 9) { err.textContent = 'Please enter a valid phone number.'; $('f-phone').focus(); return; }
  const btn = $('submit');
  btn.disabled = true; btn.textContent = 'Booking…';
  try {
    const ok = await api('bookings', { name, phone, date: state.date, time: state.time, barberId: state.barberId, notes: $('f-notes').value.trim() || null, website: $('f-website').value });
    state.booked = ok;
    showSuccess(ok);
  } catch (e) {
    err.textContent = e.message;
    if (e.code === 'SlotUnavailable') { state.time = null; await loadAvailability(); }
  } finally { btn.textContent = 'Confirm booking'; updateSummary(); }
}

function showSuccess(ok) {
  document.querySelector('.book-grid').classList.add('hidden');
  const box = $('success');
  box.classList.remove('hidden');
  $('ok-ref').textContent = ok.reference;
  $('ok-name').textContent = ok.customer;
  $('ok-barber').textContent = ok.barber;
  $('ok-date').textContent = prettyDate(ok.date, true);
  $('ok-time').textContent = ok.time + ' – ' + ok.endTime;
  $('ok-where').textContent = state.info.address || state.info.shopName;
  box.focus();
  box.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function downloadIcs(ok) {
  const stamp = (d, t) => d.replaceAll('-', '') + 'T' + t.replace(':', '') + '00';
  const esc = s => String(s).replace(/([,;\\])/g, '\\$1');
  const lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Vintage Fades//Booking//EN', 'BEGIN:VEVENT',
    'UID:' + ok.reference + '@vintagefades', 'DTSTAMP:' + new Date().toISOString().replace(/[-:]|\.\d{3}/g, ''),
    'DTSTART;TZID=Africa/Windhoek:' + stamp(ok.date, ok.time), 'DTEND;TZID=Africa/Windhoek:' + stamp(ok.date, ok.endTime),
    'SUMMARY:' + esc('Haircut at ' + state.info.shopName), 'LOCATION:' + esc(state.info.address || ''),
    'DESCRIPTION:' + esc('Barber: ' + ok.barber + '. Reference ' + ok.reference), 'END:VEVENT', 'END:VCALENDAR'];
  const a = h('a', { href: URL.createObjectURL(new Blob([lines.join('\r\n')], { type: 'text/calendar' })), download: 'vintage-fades-' + ok.reference + '.ics' });
  document.body.append(a); a.click(); a.remove();
}

function resetBooking() {
  state.time = null; state.booked = null;
  $('f-notes').value = '';
  document.querySelector('.book-grid').classList.remove('hidden');
  $('success').classList.add('hidden');
  loadAvailability();
  $('book').scrollIntoView({ behavior: 'smooth' });
}

// ------------------------------------------------------------------ manage booking

async function findBooking(ev) {
  ev.preventDefault();
  const err = $('m-error');
  err.textContent = '';
  $('found').classList.add('hidden');
  try {
    const f = await api('bookings/lookup', { reference: $('m-ref').value.trim(), phone: $('m-phone').value.trim() });
    state.found = { reference: f.reference, phone: $('m-phone').value.trim() };
    $('fd-ref').textContent = f.reference; $('fd-barber').textContent = f.barber;
    $('fd-date').textContent = prettyDate(f.date, true); $('fd-time').textContent = f.time + ' – ' + f.endTime;
    $('fd-note').textContent = state.info.cancellationNoticeHours ? 'Cancellations need at least ' + state.info.cancellationNoticeHours + ' hours notice. Closer than that, please call the shop.' : '';
    $('found').classList.remove('hidden');
  } catch (e) { err.textContent = e.message; }
}

async function cancelFound() {
  if (!state.found || !confirm('Cancel this booking?')) return;
  const btn = $('fd-cancel');
  btn.disabled = true;
  try {
    await api('bookings/cancel', state.found);
    $('found').classList.add('hidden');
    $('m-error').textContent = '';
    $('m-ref').value = ''; $('m-phone').value = '';
    alert('Your booking is cancelled. The time is free for someone else again.');
    loadAvailability();
  } catch (e) { $('fd-note').textContent = e.message; }
  finally { btn.disabled = false; }
}

// ------------------------------------------------------------------ start

async function start() {
  try {
    state.info = await api('info');
  } catch (e) {
    $('days').replaceChildren(h('p', { class: 'muted' }, 'We could not load the booking page. Please refresh, or call the shop.'));
    return;
  }
  renderInfo(state.info);
  radioGroup($('barbers-pick'), '.chip'); radioGroup($('days'), '.day'); radioGroup($('times'), '.time');
  renderBarberPicks();
  $('book-form').addEventListener('submit', submitBooking);
  $('manage-form').addEventListener('submit', findBooking);
  $('fd-cancel').addEventListener('click', cancelFound);
  $('ok-ics').addEventListener('click', () => state.booked && downloadIcs(state.booked));
  $('ok-again').addEventListener('click', resetBooking);
  await loadAvailability();
  // Other customers book too: refresh open times every 30 seconds while the page is open and no confirmation is showing.
  setInterval(() => { if (!document.hidden && !state.booked) { loadAvailability(); } }, 30000);
}

start();

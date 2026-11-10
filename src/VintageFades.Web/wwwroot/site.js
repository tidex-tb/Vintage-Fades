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


// ------------------------------------------------------------------ icons (inline SVG, no external files)

const ICON_PATHS = {
  pin: 'M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5a2.5 2.5 0 0 1 0-5 2.5 2.5 0 0 1 0 5z',
  phone: 'M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z',
  mail: 'M20 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 4l-8 5-8-5V6l8 5 8-5v2z',
  clock: 'M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z',
  scissors: 'M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z',
  instagram: 'M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zM12 0C8.741 0 8.333.014 7.053.072 2.695.272.273 2.69.073 7.052.014 8.333 0 8.741 0 12c0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98C8.333 23.986 8.741 24 12 24c3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98C15.668.014 15.259 0 12 0zm0 5.838a6.162 6.162 0 1 0 0 12.324 6.162 6.162 0 0 0 0-12.324zM12 16a4 4 0 1 1 0-8 4 4 0 0 1 0 8zm6.406-11.845a1.44 1.44 0 1 0 0 2.881 1.44 1.44 0 0 0 0-2.881z',
  whatsapp: 'M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 0 1-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 0 1-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 0 1 2.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0 0 12.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 0 0 5.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 0 0-3.48-8.413z'
};

function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICON_PATHS[name]);
  path.setAttribute('fill', 'currentColor');
  svg.append(path);
  return svg;
}

// ------------------------------------------------------------------ static content

function renderInfo(info) {
  document.title = info.shopName + ' · Windhoek';
  $('foot-name').textContent = info.shopName;
  $('year').textContent = new Date().getFullYear();
  $('s-length').textContent = info.durationMinutes + ' minutes';

  const facts = $('facts');
  facts.replaceChildren(...[
    h('li', {}, icon('clock'), info.hours),
    info.address ? h('li', {}, icon('pin'), info.address.split(',').slice(-2).join(',').trim()) : null,
    h('li', {}, icon('scissors'), info.barbers.length + ' barbers')].filter(Boolean));

  // prices
  const tabs = $('cat-tabs');
  const list = $('price-list');
  const show = idx => {
    tabs.querySelectorAll('.tab').forEach((t, i) => t.setAttribute('aria-selected', String(i === idx)));
    const cat = info.catalog[idx];
    list.replaceChildren(...[
      cat.note ? h('div', { class: 'cat-note' }, cat.note) : null,
      ...cat.items.map(i => h('div', { class: 'price-row' }, h('div', {}, i.name, i.details ? h('small', {}, i.details) : null), h('b', {}, i.price)))].filter(Boolean));
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
  if (info.address) rows.push(['pin', info.address, null, '']);
  if (info.phone) {
    const digits = info.phone.replace(/\D/g, '');
    const intl = digits.startsWith('0') ? '264' + digits.slice(1) : digits;
    rows.push(['phone', info.phone, 'tel:' + info.phone.replace(/[^\d+]/g, ''), '']);
    rows.push(['whatsapp', 'Chat on WhatsApp', 'https://wa.me/' + intl, 'wa']);
  }
  if (info.email) rows.push(['mail', info.email, 'mailto:' + info.email, '']);
  if (info.instagram) rows.push(['instagram', info.instagram, 'https://instagram.com/' + info.instagram.replace('@', ''), 'ig']);
  rows.push(['clock', info.hours, null, '']);
  $('contact').replaceChildren(...rows.map(([ic, text, href, tone]) => h('li', {}, h('span', { class: 'ic ' + tone, 'aria-hidden': 'true' }, icon(ic)),
    href ? h('a', { href, ...(href.startsWith('http') ? { target: '_blank', rel: 'noopener' } : {}) }, text) : h('span', {}, text))));
  $('map-pin').replaceChildren(icon('pin'));
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

/** silent = background refresh: no loading placeholder, nothing moves, and nothing is redrawn unless a slot count changed. */
async function loadAvailability(silent) {
  const days = $('days');
  if (!silent) days.replaceChildren(h('div', { class: 'skeleton' }));
  let fresh;
  try {
    fresh = await api('availability' + (state.barberId ? '?barberId=' + state.barberId : ''));
  } catch (e) { if (!silent) days.replaceChildren(h('p', { class: 'muted' }, e.message)); return; }
  if (silent && JSON.stringify(fresh) === JSON.stringify(state.availability)) return;
  state.availability = fresh;
  if (state.date && !(state.availability.find(d => d.date === state.date)?.freeSlots > 0)) { state.date = null; state.time = null; }
  if (!state.date) state.date = state.availability.find(d => d.freeSlots > 0)?.date || null;
  renderDays(!silent);
  await loadTimes(silent);
}

function renderDays(center) {
  const days = $('days');
  const keep = days.scrollLeft;
  if (!state.availability.length) { days.replaceChildren(h('p', { class: 'muted' }, 'No days to show right now.')); return; }
  days.replaceChildren(...state.availability.map(d => {
    const dt = new Date(d.date + 'T12:00:00Z');
    return h('button', { class: 'day', type: 'button', role: 'radio', disabled: d.freeSlots === 0, 'aria-checked': String(d.date === state.date),
      'aria-label': prettyDate(d.date, true) + (d.freeSlots ? ', ' + d.freeSlots + ' open' : ', fully booked'),
      onclick: () => { state.date = d.date; state.time = null; renderDays(false); loadTimes(); } },
      h('span', {}, dt.toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' })),
      h('b', {}, dt.getUTCDate()),
      h('small', {}, d.freeSlots === 0 ? 'Full' : dt.toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' })));
  }));
  // Only ever scroll the day strip sideways (never the page), and only to show a newly chosen day.
  const sel = days.querySelector('[aria-checked="true"]');
  days.scrollLeft = center && sel ? Math.max(0, sel.offsetLeft - (days.clientWidth - sel.offsetWidth) / 2) : keep;
}

async function loadTimes(silent) {
  const box = $('times');
  updateSummary();
  if (!state.date) { box.replaceChildren(h('p', { class: 'muted' }, state.availability.length ? 'Fully booked for the coming weeks. Please call the shop.' : 'Pick a day to see open times.')); return; }
  if (!silent) box.replaceChildren(h('div', { class: 'skeleton' }));
  let slots;
  try { slots = await api('slots?date=' + state.date + (state.barberId ? '&barberId=' + state.barberId : '')); }
  catch (e) { if (!silent) box.replaceChildren(h('p', { class: 'muted' }, e.message)); return; }
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
  setInterval(() => { if (!document.hidden && !state.booked) { loadAvailability(true); } }, 30000);
}

start();

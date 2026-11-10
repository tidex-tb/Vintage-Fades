'use strict';
/* Vintage Fades dashboard: pages and layout. Uses the helpers in app.js. All text is added with textContent, never as HTML. */

// ------------------------------------------------------------------ small building blocks

function statusBadge(s) { return h('span', { class: 'badge ' + s }, s === 'NoShow' ? 'No-show' : s === 'Confirmed' ? 'Booked' : s); }
function stat(num, label, hint) { return h('div', { class: 'stat' }, h('div', { class: 'lbl' }, label), h('div', { class: 'num' }, String(num)), hint ? h('div', { class: 'hint' }, hint) : null); }
function initials(name) { return (name || '?').trim().slice(0, 1).toUpperCase(); }
function card(title, body, tools) {
  return h('section', { class: 'panel' }, h('div', { class: 'panel-head' }, h('h3', {}, title), tools || null), body);
}
function seg(options, current, onPick) {
  return h('div', { class: 'seg', role: 'tablist' }, options.map(([id, label]) =>
    h('button', { class: id === current ? 'on' : '', type: 'button', onclick: () => onPick(id) }, label)));
}
function svgEl(tag, attrs) {
  const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs || {})) el.setAttribute(k, v);
  return el;
}
function monthLabel(ym) { return new Date(ym + '-15T12:00:00Z').toLocaleDateString('en-GB', { month: 'short', timeZone: 'UTC' }); }
function sourceLabel(s) { return s === 'Admin' ? 'Phone booking' : s === 'Web' ? 'Website' : s; }

// ------------------------------------------------------------------ appointment cards (Appointments page)

function appointmentActions(item) {
  const actions = [];
  if (item.status === 'Confirmed') {
    actions.push(h('button', { class: 'btn small', onclick: () => doneSheet(item) }, '✓ Done'));
    actions.push(h('button', { class: 'btn ghost small', onclick: ev => run(ev.currentTarget, async () => {
      if (!confirm('Mark ' + (item.customer || 'this customer') + ' as a no-show?')) return;
      await api('bookings/' + encodeURIComponent(item.reference) + '/no-show', { method: 'POST' }); await refresh();
    }) }, 'No-show'));
    actions.push(h('button', { class: 'btn danger small', onclick: ev => run(ev.currentTarget, async () => {
      if (!confirm('Cancel this appointment? The customer is told on their chat.')) return;
      await api('bookings/' + encodeURIComponent(item.reference) + '/cancel', { method: 'POST', body: { reason: 'Cancelled by the shop' } }); await refresh();
    }) }, 'Cancel'));
  } else if (item.status === 'Completed') {
    actions.push(h('button', { class: 'btn ghost small', onclick: () => doneSheet(item) }, '+ Add service'));
  }
  return actions.length ? h('div', { class: 'actions' }, actions) : null;
}

function appointmentCard(item) {
  const lines = item.services.map(s => h('div', { class: 'line' },
    h('span', {}, s.name + (s.barber !== item.barber ? ' · ' + s.barber : '')),
    h('span', {}, money(s.price), ' ', h('button', { class: 'x', 'aria-label': 'Remove ' + s.name, onclick: () => run(null, async () => {
      if (!confirm('Remove “' + s.name + '” from the records?')) return;
      await api('sales/' + s.id, { method: 'DELETE' }); await refresh();
    }) }, '✕'))));
  return h('div', { class: 'card s-' + item.status },
    h('div', { class: 'row' }, h('span', { class: 'time' }, item.time + ' – ' + item.endTime), statusBadge(item.status)),
    h('div', { class: 'row' }, h('span', { class: 'who' }, item.customer || 'Customer'), h('span', { class: 'badge barber' }, item.barber)),
    h('div', { class: 'muted' }, (item.phone ? item.phone + ' · ' : '') + sourceLabel(item.source)),
    item.notes ? h('div', { class: 'muted' }, '“' + item.notes + '”') : null,
    lines.length ? h('div', { class: 'lines' }, lines) : null,
    item.total > 0 ? h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Total'), h('b', {}, money(item.total))) : null,
    appointmentActions(item));
}

function walkInCard(w) {
  return h('div', { class: 'card walkin' },
    h('div', { class: 'row' }, h('span', { class: 'time' }, w.time), h('span', { class: 'badge' }, 'Walk-in')),
    h('div', { class: 'who' }, w.customer || 'Walk-in customer'),
    h('div', { class: 'lines' }, w.services.map(s => h('div', { class: 'line' },
      h('span', {}, s.name + ' · ' + s.barber),
      h('span', {}, money(s.price), ' ', h('button', { class: 'x', 'aria-label': 'Remove ' + s.name, onclick: () => run(null, async () => {
        if (!confirm('Remove “' + s.name + '” from the records?')) return;
        await api('sales/' + s.id, { method: 'DELETE' }); await refresh();
      }) }, '✕'))))));
}

function appointmentsView() {
  const d = state.day;
  const wrap = h('div', {});
  const shift = n => () => { state.date = addDays(state.date, n); state.day = null; render(); run(null, refresh); };
  wrap.append(h('div', { class: 'datenav' },
    h('button', { class: 'iconbtn', 'aria-label': 'Previous day', onclick: shift(-1) }, '‹'),
    h('button', { class: 'label iconbtn', onclick: () => { state.date = todayStr(); state.day = null; render(); run(null, refresh); } },
      state.date === todayStr() ? 'Today · ' + shortDate(state.date) : prettyDate(state.date)),
    h('button', { class: 'iconbtn', 'aria-label': 'Next day', onclick: shift(1) }, '›')));
  if (!d) { wrap.append(h('div', { class: 'empty' }, 'Loading…')); return wrap; }
  const waiting = d.appointments.filter(a => a.status === 'Confirmed').length;
  const done = d.appointments.filter(a => a.status === 'Completed').length;
  wrap.append(h('div', { class: 'stats three' }, stat(money(d.revenue), 'Earned'), stat(waiting, 'Waiting'), stat(done, 'Done')));
  if (!d.appointments.length && !d.walkIns.length) wrap.append(h('div', { class: 'empty' }, 'Nothing booked for this day yet.'));
  const list = h('div', { class: 'cards' });
  d.appointments.forEach(a => list.append(appointmentCard(a)));
  wrap.append(list);
  if (d.walkIns.length) {
    wrap.append(h('div', { class: 'section-title' }, 'Walk-ins'));
    const w = h('div', { class: 'cards' });
    d.walkIns.forEach(x => w.append(walkInCard(x)));
    wrap.append(w);
  }
  return wrap;
}

// ------------------------------------------------------------------ dashboard overview

function dashRangeDates() {
  const today = todayStr();
  switch (state.dashRange) {
    case 'week': return [weekStart(today), today];
    case '6m': return [addDays(today, -182), today];
    case 'year': return [addDays(today, -364), today];
    default: return [monthStart(today), today];
  }
}

function revenueChart(report) {
  const grouped = state.dashRange === '6m' || state.dashRange === 'year';
  let points;
  if (grouped) {
    const by = new Map();
    report.byDay.forEach(d => by.set(d.date.slice(0, 7), (by.get(d.date.slice(0, 7)) || 0) + d.revenue));
    points = [...by.entries()].map(([k, v]) => ({ label: monthLabel(k), title: new Date(k + '-15T12:00:00Z').toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' }), value: v }));
  } else {
    points = report.byDay.map(d => ({ label: shortDate(d.date), title: shortDate(d.date), value: d.revenue }));
  }
  if (!points.length || points.every(p => p.value === 0)) return h('div', { class: 'empty' }, 'No earnings recorded in this period yet. Press ✓ Done on an appointment to start tracking.');
  const max = Math.max(1, ...points.map(p => p.value));
  const chart = h('div', { class: 'bars' });
  points.forEach(p => {
    const col = h('div', { class: 'bar-col', tabindex: '0', 'aria-label': p.title + ': ' + money(p.value) }, h('span', { class: 'tip' }, p.title + ' · ' + money(p.value)), h('i', {}));
    col.querySelector('i').style.height = Math.max(3, (p.value / max) * 100) + '%';
    chart.append(col);
  });
  const step = Math.ceil(points.length / 8);
  const axis = h('div', { class: 'axis' }, points.map((p, i) => h('span', {}, i % step === 0 ? p.label : '')));
  return h('div', {}, chart, axis);
}

function topBarbers(report) {
  if (!report.byBarber.length) return h('div', { class: 'empty' }, 'No services recorded yet.');
  const max = Math.max(1, ...report.byBarber.map(b => b.revenue));
  return h('div', { class: 'people' }, report.byBarber.map((b, i) => {
    const bar = h('i', {}); bar.style.width = Math.max(2, (b.revenue / max) * 100) + '%';
    return h('div', { class: 'person' },
      h('div', { class: 'avatar' }, initials(b.barber)),
      h('div', { class: 'grow' },
        h('div', { class: 'row' }, h('b', {}, (i === 0 ? '🥇 ' : '') + b.barber), h('span', { class: 'pill' }, 'Barber')),
        h('div', { class: 'meter' }, bar),
        h('div', { class: 'muted small' }, money(b.revenue) + ' · ' + b.services + ' services')),
      h('div', { class: 'count' }, h('b', {}, b.visits), h('span', {}, 'Clients')));
  }));
}

const DONUT_COLORS = ['#8b5cf6', '#ff5a1f', '#f2f0fb', '#5b5780', '#d9b44a'];
function serviceShare(report) {
  if (!report.byService.length) return h('div', { class: 'empty' }, 'No services recorded yet.');
  const sorted = [...report.byService].sort((a, b) => b.revenue - a.revenue);
  const top = sorted.slice(0, 4);
  const rest = sorted.slice(4).reduce((s, x) => s + x.revenue, 0);
  const slices = [...top.map(s => ({ name: s.service, value: s.revenue })), ...(rest > 0 ? [{ name: 'Other services', value: rest }] : [])];
  const total = slices.reduce((s, x) => s + x.value, 0) || 1;
  const r = 15.915; // circumference = 100
  const svg = svgEl('svg', { viewBox: '0 0 42 42', class: 'donut', role: 'img', 'aria-label': 'Share of earnings by service' });
  svg.append(svgEl('circle', { cx: 21, cy: 21, r, fill: 'none', stroke: '#231f40', 'stroke-width': 6 }));
  let offset = 25;
  slices.forEach((s, i) => {
    const pct = (s.value / total) * 100;
    svg.append(svgEl('circle', { cx: 21, cy: 21, r, fill: 'none', stroke: DONUT_COLORS[i % DONUT_COLORS.length], 'stroke-width': 6,
      'stroke-dasharray': pct + ' ' + (100 - pct), 'stroke-dashoffset': offset }));
    offset -= pct;
  });
  return h('div', { class: 'share' }, svg, h('ul', { class: 'legend' }, slices.map((s, i) => {
    const dot = h('i', {}); dot.style.background = DONUT_COLORS[i % DONUT_COLORS.length];
    return h('li', {}, dot, h('span', { class: 'grow' }, s.name), h('b', {}, Math.round((s.value / total) * 100) + '%'));
  })));
}

function todayList(day) {
  if (!day) return h('div', { class: 'empty' }, 'Loading…');
  const items = day.appointments.filter(a => a.status !== 'Cancelled');
  if (!items.length) return h('div', { class: 'empty' }, 'No appointments today.');
  return h('div', { class: 'appts' }, items.map(a => h('button', { class: 'appt', type: 'button', onclick: () => apptSheet(a) },
    h('div', { class: 'avatar sm' }, initials(a.customer)),
    h('div', { class: 'grow' }, h('b', {}, a.customer || 'Customer'), h('div', { class: 'muted small' }, a.barber + ' · ' + sourceLabel(a.source))),
    statusBadge(a.status),
    h('span', { class: 'timebox' }, a.time))));
}

function apptSheet(item) {
  const body = [
    h('div', { class: 'row' }, h('span', { class: 'time' }, item.time + ' – ' + item.endTime), statusBadge(item.status)),
    h('div', { class: 'who' }, item.customer || 'Customer'),
    h('div', { class: 'muted' }, item.barber + ' · ' + (item.phone ? item.phone + ' · ' : '') + sourceLabel(item.source)),
    item.notes ? h('div', { class: 'muted' }, '“' + item.notes + '”') : null,
    item.services.length ? h('div', { class: 'lines' }, item.services.map(s => h('div', { class: 'line' }, h('span', {}, s.name), h('span', {}, money(s.price))))) : null
  ];
  const actions = appointmentActions(item);
  if (actions) body.push(actions);
  const close = openSheet(item.reference, body, null);
  // Any action button closes the pop-up (it reloads the page data itself).
  document.querySelector('.sheet .actions')?.addEventListener('click', () => setTimeout(close, 0));
}

function dashboardView() {
  const wrap = h('div', { class: 'dash' });
  const d = state.dash;
  if (!d) { wrap.append(h('div', { class: 'empty' }, 'Loading…')); return wrap; }
  const r = d.report;
  wrap.append(h('div', { class: 'stats' },
    stat(money(r.revenue), 'Revenue'), stat(r.visits, 'Customers served'), stat(r.services, 'Services done'),
    stat(money(r.averageVisit), 'Average per customer'), stat(r.noShows, 'No-shows'), stat(r.cancelled, 'Cancelled')));
  wrap.append(h('div', { class: 'dash-grid' },
    card('Revenue report', revenueChart(r), seg([['week', 'Week'], ['month', 'Month'], ['6m', '6 month'], ['year', 'Year']], state.dashRange, async id => {
      state.dashRange = id; state.dash = null; render(); await run(null, loadDash);
    })),
    card('Top barbers', topBarbers(r)),
    card('Service share', serviceShare(r)),
    card('Today’s appointments', todayList(d.today), h('button', { class: 'link', onclick: () => go('appointments') }, 'View all'))));
  return wrap;
}

// ------------------------------------------------------------------ calendar (week grid)

const HOURS = Array.from({ length: 10 }, (_, i) => 9 + i); // 09:00 to 18:00 start times

function calendarView() {
  const wrap = h('div', { class: 'cal' });
  if (!state.weekStart) state.weekStart = weekStart(todayStr());
  const ws = state.weekStart;
  const move = n => () => { state.weekStart = n === 0 ? weekStart(todayStr()) : addDays(ws, n); state.week = null; render(); run(null, refresh); };
  const barberSel = h('select', { 'aria-label': 'Filter by barber', onchange: e => { state.calBarber = e.target.value; render(); } },
    h('option', { value: '' }, 'All barbers'),
    state.barbers.filter(b => b.isActive).map(b => h('option', { value: String(b.id), selected: String(b.id) === state.calBarber }, b.displayName)));
  wrap.append(h('div', { class: 'cal-bar' },
    h('div', { class: 'cal-nav' },
      h('button', { class: 'iconbtn', 'aria-label': 'Previous week', onclick: move(-7) }, '‹'),
      h('b', {}, shortDate(ws) + ' – ' + shortDate(addDays(ws, 6))),
      h('button', { class: 'iconbtn', 'aria-label': 'Next week', onclick: move(7) }, '›'),
      h('button', { class: 'btn ghost small', onclick: move(0) }, 'This week')),
    h('div', { class: 'legend-inline' },
      h('span', {}, h('i', { class: 'k booked' }), 'Booked'), h('span', {}, h('i', { class: 'k done' }), 'Done'), h('span', {}, h('i', { class: 'k noshow' }), 'No-show')),
    barberSel));
  if (!state.week) { wrap.append(h('div', { class: 'empty' }, 'Loading…')); return wrap; }

  const today = todayStr();
  const grid = h('div', { class: 'grid-week' });
  grid.append(h('div', { class: 'gh corner' }, 'Time'));
  state.week.forEach((dayData, idx) => {
    const day = { ...dayData, date: addDays(ws, idx) }; // the API's date is display text; the column's real date is computed here
    const dt = new Date(day.date + 'T12:00:00Z');
    grid.append(h('div', { class: 'gh' + (day.date === today ? ' today' : '') },
      dt.toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' }).toUpperCase() + ' ', h('span', { class: 'dn' }, dt.getUTCDate())));
  });
  for (const hour of HOURS) {
    const label = String(hour).padStart(2, '0') + ':00';
    grid.append(h('div', { class: 'hl' }, label));
    state.week.forEach((dayData, idx) => {
    const day = { ...dayData, date: addDays(ws, idx) }; // the API's date is display text; the column's real date is computed here
      const appts = day.appointments.filter(a => a.status !== 'Cancelled' && parseInt(a.time, 10) === hour && (!state.calBarber || String(a.barberId) === state.calBarber));
      const cell = h('div', { class: 'cell' + (day.date === today ? ' today' : '') });
      appts.forEach(a => cell.append(h('button', { class: 'ev s-' + a.status, type: 'button', title: a.time + ' · ' + a.barber, onclick: () => apptSheet(a) },
        h('b', {}, a.customer || 'Customer'), h('span', {}, a.time + ' · ' + a.barber))));
      if (day.date >= today && appts.length < 4) {
        cell.append(h('button', { class: 'plus' + (appts.length ? ' small' : ''), type: 'button', 'aria-label': 'Book ' + label + ' on ' + shortDate(day.date),
          onclick: () => phoneBookingSheet({ date: day.date, time: label, barberId: state.calBarber ? Number(state.calBarber) : null }) }, '+'));
      }
      grid.append(cell);
    });
  }
  wrap.append(h('div', { class: 'grid-scroll' }, grid));
  return wrap;
}

// ------------------------------------------------------------------ earnings (detailed report)

function chips(options, current, onPick) {
  return h('div', { class: 'chips' }, options.map(([id, label]) => h('button', { class: 'chip' + (id === current ? ' on' : ''), onclick: () => onPick(id) }, label)));
}

function reportView() {
  const wrap = h('div', {});
  wrap.append(chips([['today', 'Today'], ['week', 'This week'], ['month', 'This month'], ['lastmonth', 'Last month'], ['custom', 'Custom']], state.range, async id => {
    state.range = id; render(); if (id !== 'custom') await run(null, loadReport);
  }));
  if (state.range === 'custom') {
    const from = h('input', { type: 'date', value: state.customFrom || monthStart(todayStr()) });
    const to = h('input', { type: 'date', value: state.customTo || todayStr() });
    wrap.append(h('div', { class: 'field-row pad' }, h('label', {}, 'From', from), h('label', {}, 'To', to)),
      h('div', { class: 'actions' }, h('button', { class: 'btn small', onclick: () => { state.customFrom = from.value; state.customTo = to.value; run(null, loadReport); } }, 'Show')));
  }
  const r = state.report;
  if (!r) { wrap.append(h('div', { class: 'empty' }, 'Loading…')); return wrap; }

  wrap.append(h('div', { class: 'section-title' }, shortDate(r.from) + ' – ' + shortDate(r.to)));
  wrap.append(h('div', { class: 'stats' },
    stat(money(r.revenue), 'Revenue'), stat(r.visits, 'Customers served'), stat(r.services, 'Services done'),
    stat(money(r.averageVisit), 'Average per customer'), stat(r.noShows, 'No-shows'), stat(r.cancelled, 'Cancelled')));

  const byBarber = h('div', {});
  if (!r.byBarber.length) byBarber.append(h('div', { class: 'empty' }, 'No services recorded in this period.'));
  const maxB = Math.max(1, ...r.byBarber.map(b => b.revenue));
  r.byBarber.forEach((b, i) => {
    const bar = h('i', {}); bar.style.width = Math.max(2, (b.revenue / maxB) * 100) + '%';
    byBarber.append(h('div', { class: 'barrow' },
      h('div', { class: 'top' }, h('span', {}, (i === 0 ? '🥇 ' : '') + b.barber), h('span', {}, money(b.revenue))),
      h('div', { class: 'muted small' }, b.visits + ' customers · ' + b.services + ' services' + (r.revenue > 0 ? ' · ' + Math.round((b.revenue / r.revenue) * 100) + '%' : '')),
      h('div', { class: 'bar' }, bar)));
  });
  const byService = h('div', {});
  const maxS = Math.max(1, ...r.byService.map(s => s.revenue));
  if (!r.byService.length) byService.append(h('div', { class: 'empty' }, 'Nothing yet.'));
  r.byService.slice(0, 12).forEach(s => {
    const bar = h('i', {}); bar.style.width = Math.max(2, (s.revenue / maxS) * 100) + '%';
    byService.append(h('div', { class: 'barrow' },
      h('div', { class: 'top' }, h('span', {}, s.service), h('span', {}, money(s.revenue))),
      h('div', { class: 'muted small' }, s.count + ' ×'), h('div', { class: 'bar gold' }, bar)));
  });
  wrap.append(h('div', { class: 'dash-grid two' }, card('By barber', byBarber), card('By service', byService)));

  if (r.byDay.length > 1) {
    const maxD = Math.max(1, ...r.byDay.map(x => x.revenue));
    const chart = h('div', { class: 'bars' });
    r.byDay.forEach(x => {
      const col = h('div', { class: 'bar-col', 'aria-label': shortDate(x.date) + ': ' + money(x.revenue) }, h('span', { class: 'tip' }, shortDate(x.date) + ' · ' + money(x.revenue)), h('i', {}));
      col.querySelector('i').style.height = Math.max(3, (x.revenue / maxD) * 100) + '%';
      chart.append(col);
    });
    wrap.append(card('By day', h('div', {}, chart, h('div', { class: 'axis between' }, h('span', {}, shortDate(r.byDay[0].date)), h('span', {}, shortDate(r.byDay[r.byDay.length - 1].date))))));
  }
  wrap.append(h('div', { class: 'actions' }, h('button', { class: 'btn ghost small', onclick: () => downloadCsv(r) }, '⬇ Download as spreadsheet (CSV)')));
  return wrap;
}

function downloadCsv(r) {
  const esc = v => '"' + String(v).replaceAll('"', '""') + '"';
  const rows = [['Report', r.from + ' to ' + r.to], [], ['Barber', 'Customers', 'Services', 'Revenue (N$)'],
    ...r.byBarber.map(b => [b.barber, b.visits, b.services, b.revenue]), [], ['Service', 'Count', 'Revenue (N$)'],
    ...r.byService.map(s => [s.service, s.count, s.revenue]), [], ['Date', 'Services', 'Revenue (N$)'], ...r.byDay.map(d => [d.date, d.services, d.revenue])];
  const blob = new Blob([rows.map(row => row.map(esc).join(',')).join('\r\n')], { type: 'text/csv' });
  const a = h('a', { href: URL.createObjectURL(blob), download: 'vintage-fades-' + r.from + '-to-' + r.to + '.csv' });
  document.body.append(a); a.click(); a.remove();
}

// ------------------------------------------------------------------ loading

async function loadBasics() {
  const [barbers, catalog, bot] = await Promise.all([api('barbers'), api('catalog'), api('bot')]);
  state.barbers = barbers; state.catalog = catalog; state.bot = bot;
}

async function setBot(paused) {
  const ask = paused
    ? 'Pause the bot on WhatsApp, Instagram and Messenger? Customers get no automatic replies until you turn it back on. Their messages are still saved.'
    : 'Turn the bot back on for every platform?';
  if (!confirm(ask)) return;
  await run(null, async () => {
    state.bot = await api('bot', { method: 'PUT', body: { paused } });
    toast(state.bot.paused ? 'Bot paused on all platforms.' : 'Bot is on again.');
    render();
  });
}

function botSwitch() {
  const paused = !!state.bot.paused;
  return h('button', { class: 'botpill ' + (paused ? 'off' : 'on'), type: 'button', 'aria-pressed': String(!paused),
    title: paused ? 'Click to turn the bot back on' : 'Click to pause the bot on every platform', onclick: () => setBot(!paused) },
    h('i', {}), paused ? 'Bot paused' : 'Bot on');
}
async function loadDash() {
  const [from, to] = dashRangeDates();
  const [report, today] = await Promise.all([api('reports?from=' + from + '&to=' + to), api('day?date=' + todayStr())]);
  state.dash = { report, today };
  render();
}
async function loadReport() {
  const [from, to] = rangeDates();
  state.report = null; render();
  state.report = await api('reports?from=' + from + '&to=' + to);
  render();
}
async function loadWeek() {
  if (!state.weekStart) state.weekStart = weekStart(todayStr());
  state.week = await api('week?from=' + state.weekStart);
  render();
}
async function loadDayData() { state.day = await api('day?date=' + state.date); render(); }

/** Reloads whatever the current page shows. */
async function refresh() {
  switch (state.tab) {
    case 'dashboard': return loadDash();
    case 'calendar': return loadWeek();
    case 'appointments': return loadDayData();
    default: return loadReport();
  }
}
const loadDay = refresh;

function go(tab) {
  state.tab = tab;
  render();
  run(null, refresh);
}

// ------------------------------------------------------------------ shell

const NAV = [
  ['dashboard', 'Dashboard', '▦'],
  ['calendar', 'Calendar', '🗓'],
  ['appointments', 'Appointments', '✂'],
  ['reports', 'Earnings', '📈']
];
const TITLES = { dashboard: 'Dashboard', calendar: 'Calendar', appointments: 'Appointments', reports: 'Earnings' };

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
  return h('div', { class: 'login' }, h('img', { src: '/logo.jpeg', alt: '' }), h('h1', {}, 'Vintage Fades'), h('div', { class: 'muted' }, 'Appointments and earnings'), form);
}

function render() {
  const app = document.getElementById('app');
  app.replaceChildren();
  if (!state.key) { app.append(loginView()); return; }

  const side = h('aside', { class: 'side' },
    h('div', { class: 'logo' }, h('img', { src: '/logo.jpeg', alt: '' }), h('span', {}, 'Vintage ', h('b', {}, 'Fades'))),
    h('nav', { 'aria-label': 'Sections' }, NAV.map(([id, label, icon]) =>
      h('button', { class: state.tab === id ? 'active' : '', onclick: () => go(id), 'aria-current': state.tab === id ? 'page' : null },
        h('span', { class: 'ic', 'aria-hidden': 'true' }, icon), h('span', { class: 'tx' }, label)))),
    h('div', { class: 'side-foot' },
      h('a', { class: 'link', href: '/', target: '_blank', rel: 'noopener' }, 'View customer site ↗'),
      h('button', { class: 'link', onclick: () => { if (confirm('Sign out?')) signOut(); } }, 'Sign out')));

  const top = h('header', { class: 'top' },
    h('div', {}, h('h1', {}, TITLES[state.tab]), h('div', { class: 'muted small' }, prettyDate(todayStr()))),
    h('div', { class: 'top-actions' },
      botSwitch(),
      h('button', { class: 'btn ghost small', onclick: () => walkInSheet() }, '✂ Walk-in'),
      h('button', { class: 'btn small', onclick: () => phoneBookingSheet() }, '＋ Phone booking'),
      h('button', { class: 'iconbtn only-mobile', 'aria-label': 'Sign out', onclick: () => { if (confirm('Sign out?')) signOut(); } }, '⎋')));

  const views = { dashboard: dashboardView, calendar: calendarView, appointments: appointmentsView, reports: reportView };
  const banner = state.bot.paused ? h('div', { class: 'banner', role: 'status' },
    h('span', {}, '⏸ The bot is paused on WhatsApp, Instagram and Messenger. Customer messages are saved but not answered.'),
    h('button', { class: 'btn small', onclick: () => setBot(false) }, 'Turn bot back on')) : null;
  app.append(h('div', { class: 'shell' }, side, h('main', {}, top, banner, (views[state.tab] || dashboardView)())));
}

async function start() {
  render();
  await run(null, async () => { await loadBasics(); await refresh(); });
}

// Bookings arrive from the website, WhatsApp and Instagram while this page is open: refresh every minute.
setInterval(() => { if (state.key && !document.hidden && !document.querySelector('.backdrop')) run(null, refresh); }, 60000);

if (state.key) start(); else render();

'use strict';
(() => {
  const $ = id => document.getElementById(id);
  let accounts = [], satellites = [], richClients = [], rows = [], editing = null, next = null, pendingDelete = null;
  const enc = encodeURIComponent;
  function node(tag, text, cls) { const el = document.createElement(tag); if (text != null) el.textContent = text; if (cls) el.className = cls; return el; }
  function button(text, action) { const el = node('button', text); el.type = 'button'; el.onclick = () => run(action); return el; }
  async function api(path, method = 'GET', body, headers = {}) {
    const res = await fetch('/api/calendars' + path, { method, headers: { 'Content-Type': 'application/json', ...headers }, body: body === undefined ? undefined : JSON.stringify(body) });
    if (res.status === 204) return null;
    const data = await res.json(); if (!res.ok) throw new Error(data.error || 'Calendar request failed.'); return data;
  }
  async function run(action) { try { $('notice').textContent = ''; await action(); } catch (e) { $('notice').textContent = e.message; } }
  function selected() { const [a, c] = $('calendar').value.split('|'); if (!a || !c) throw new Error('Select a subscribed calendar first.'); return '/' + enc(a) + '/' + enc(c); }
  function resetEditor() { editing = null; $('event-form').hidden = true; $('event-form').reset(); }
  function recipientPicker(title, choices, selected, key, label, allSelected = false) {
    const field = node('fieldset', null, 'recipient-picker'); field.append(node('legend', title)); const inputs = [];
    const all = node('input'); all.type = 'checkbox'; all.checked = allSelected;
    const allRow = node('label', null, 'check'); allRow.append(all, document.createTextNode('All (current and future)')); field.append(allRow);
    for (const choice of choices) {
      const input = node('input'); input.type = 'checkbox'; input.checked = selected.some(s => key(s) === key(choice));
      const row = node('label', null, 'check'); row.append(input, document.createTextNode(label(choice))); field.append(row); inputs.push({input, choice});
    }
    for (const missing of selected.filter(s => !choices.some(c => key(c) === key(s)))) {
      const input = node('input'); input.type = 'checkbox'; input.checked = true;
      const row = node('label', null, 'check'); row.append(input, document.createTextNode(label(missing) + ' (unavailable — uncheck to remove)')); field.append(row); inputs.push({input, choice: missing});
    }
    if (!inputs.length) field.append(node('p', 'No recipients configured.', 'muted'));
    function updateAll() { for (const row of inputs) row.input.disabled = all.checked; }
    all.onchange = updateAll; updateAll();
    return {element: field, all: () => all.checked, values: () => all.checked ? [] : inputs.filter(r => r.input.checked).map(r => r.choice)};
  }
  function notificationText(event) {
    if (event.reminders?.useDefault) return 'Calendar defaults';
    const overrides = event.reminders?.overrides || [];
    return overrides.length ? overrides.map(r => (r.minutes === 0 ? 'At start' : r.minutes + ' minutes before') + ' (' + r.method + ')').join(', ') : 'No notifications';
  }
  async function refresh() {
    accounts = await api('/accounts');
    richClients = await api('/recipients');
    const old = $('calendar').value; $('calendar').replaceChildren(new Option('Select a calendar', ''));
    $('accounts').replaceChildren();
    if (!accounts.length) $('accounts').append(node('p', 'Connect a service account to get started.', 'empty'));
    for (const account of accounts) {
      const card = node('article', null, 'account'); card.append(node('h3', account.name), node('small', account.email));
      const actions = node('div', null, 'actions');
      actions.append(button('Fetch calendars', async () => { const found = await api('/' + enc(account.id) + '/discover', 'POST'); await refresh(); $('notice').textContent = found.length ? 'Fetched ' + found.length + ' calendars.' : 'Google returned no calendars. Share a calendar with the service account and add its Calendar ID below.'; }),
        button('Disconnect', async () => { if (confirm('Disconnect ' + account.name + '? Google events will remain in the calendar.')) { await api('/accounts/' + enc(account.id), 'DELETE'); await refresh(); } }));
      card.append(actions);
      const form = node('form'); const label = node('label', 'Shared calendar missing? Add its calendar ID'); const input = node('input'); input.required = true; input.placeholder = 'Calendar settings → Integrate calendar → Calendar ID'; label.append(input); form.append(label, node('button', 'Add shared calendar'));
      form.onsubmit = e => { e.preventDefault(); run(async () => { await api('/' + enc(account.id) + '/calendar-list', 'POST', {calendarId: input.value.trim()}); await refresh(); }); }; card.append(form);
      for (const cal of account.calendars) {
        const item = node('div', null, 'subscription');
        const check = node('input'); check.type = 'checkbox'; check.checked = cal.enabled;
        const title = node('label', null, 'check'); title.append(check, document.createTextNode(cal.name)); item.append(title);
        const announce = node('input'); announce.type = 'checkbox'; announce.checked = cal.announce;
        const al = node('label', null, 'check'); al.append(announce, document.createTextNode('Send event notifications')); item.append(al);
        const savedSatellites = (cal.satelliteIds ?? (cal.satelliteId ? [cal.satelliteId] : [])).map(id => ({id}));
        const targets = recipientPicker('Satellites', satellites, savedSatellites, s => s.id, s => s.name || s.id, cal.allSatellites === true);
        const clients = recipientPicker('Rich API clients', richClients, cal.richClients || [], c => JSON.stringify([c.owner,c.clientId]), c => (c.name || c.clientId) + ' · ' + c.owner, cal.allRichClients === true);
        item.append(targets.element, clients.element, node('p', 'Each recipient receives the event’s configured notification times. Rich clients receive durable notifications through the Rich API; display and sound depend on the client.', 'muted'));
        item.append(button('Save subscription', async () => { await api('/' + enc(account.id) + '/subscriptions/' + enc(cal.id), 'PUT', {enabled: check.checked, announce: announce.checked, allSatellites: targets.all(), allRichClients: clients.all(), satelliteIds: targets.values().map(s => s.id), richClients: clients.values().map(c => ({clientId:c.clientId,owner:c.owner}))}); await refresh(); $('notice').textContent = 'Subscription saved.'; }));
        const defaultsForm = node('form'); const defaultsLabel = node('label', 'Service account default notification (minutes before; blank disables)');
        const minutes = node('input'); minutes.type = 'number'; minutes.min = '0'; minutes.max = '40320'; defaultsLabel.append(minutes);
        defaultsForm.append(defaultsLabel, node('button', 'Set notification default'));
        defaultsForm.onsubmit = e => { e.preventDefault(); run(async () => { await api('/' + enc(account.id) + '/defaults/' + enc(cal.id), 'PUT', {minutes: minutes.value === '' ? null : Number(minutes.value)}); $('notice').textContent = 'Service account notification default saved.'; }); };
        item.append(defaultsForm);
        card.append(item);
        if (cal.enabled) $('calendar').append(new Option(account.name + ' / ' + cal.name, account.id + '|' + cal.id));
      }
      $('accounts').append(card);
    }
    $('calendar').value = old;
  }
  async function load(more = false) {
    const path = selected(); resetEditor();
    const from = new Date($('from').value + 'T00:00:00'), to = new Date($('to').value + 'T00:00:00'); to.setDate(to.getDate() + 1);
    if (!(to > from)) throw new Error('Choose valid start and end dates.');
    const data = await api(path + '/events?from=' + enc(from.toISOString()) + '&to=' + enc(to.toISOString()) + (more && next ? '&page=' + enc(next) : ''));
    if (selected() !== path) return;
    rows = more ? rows.concat(data.items || []) : (data.items || []); next = data.nextPageToken;
    $('more').hidden = !next; $('events').replaceChildren();
    if (!rows.length) $('events').append(node('p', 'No events in this date range.'));
    for (const event of rows) {
      const card = node('article', null, 'event'); card.append(node('h3', event.summary || 'Untitled event'));
      const start = event.start?.dateTime ? new Date(event.start.dateTime).toLocaleString() : event.start?.date;
      const end = event.end?.dateTime ? new Date(event.end.dateTime).toLocaleString() : event.end?.date;
      card.append(node('time', start + ' → ' + end));
      card.append(node('p', 'Notifications: ' + notificationText(event)));
      if (event.location) card.append(node('p', event.location));
      if (event.description) card.append(node('p', event.description));
      const actions = node('div', null, 'actions'); actions.append(button('Edit', () => edit(event)), button('Remove', async () => {
        pendingDelete = await api(path + '/events/' + enc(event.id) + '/prepare-delete', 'POST');
        $('delete-summary').textContent = (pendingDelete.summary || 'Untitled event') + ' · ' + start; $('delete-dialog').showModal();
      })); card.append(actions); $('events').append(card);
    }
  }
  function local(value) { const date = new Date(value); return new Date(date - date.getTimezoneOffset() * 60000).toISOString().slice(0,16); }
  function edit(event) {
    selected(); editing = event || null; $('event-form').reset(); $('event-form').hidden = false; $('event-title').textContent = event ? 'Edit event' : 'Add event';
    $('summary').value = event?.summary || ''; $('location').value = event?.location || ''; $('description').value = event?.description || '';
    $('event-notifications').textContent = event ? 'Current notifications: ' + notificationText(event) + '. Leave unchanged to preserve them.' : 'New events use the calendar defaults unless you choose a different notification.';
    $('notification-minutes').hidden = true;
    $('all-day').checked = !!event?.start?.date; setTimeType();
    $('start').value = event?.start?.date || (event?.start?.dateTime ? local(event.start.dateTime) : local(new Date()));
    $('end').value = event?.end?.date || (event?.end?.dateTime ? local(event.end.dateTime) : local(new Date(Date.now() + 3600000)));
    $('summary').focus();
  }
  function setTimeType() { for (const field of ['start', 'end']) $(field).type = $('all-day').checked ? 'date' : 'datetime-local'; }
  $('all-day').onchange = setTimeType;
  $('notification').onchange = () => { $('notification-minutes').hidden = $('notification').value !== 'custom'; };
  $('account-form').onsubmit = e => { e.preventDefault(); run(async () => { const file = $('credential').files[0]; if (file.size > 32768) throw new Error('Service account key is too large.'); await api('/accounts', 'POST', {name: $('account-name').value, credential: await file.text()}); $('account-form').reset(); await refresh(); }); };
  $('event-form').onsubmit = e => { e.preventDefault(); run(async () => {
    const path = selected(), start = $('start').value, end = $('end').value;
    if (end <= start) throw new Error('End must be after start.');
    const time = value => $('all-day').checked ? {date: value} : {dateTime: new Date(value).toISOString()};
    const body = {summary: $('summary').value, location: $('location').value, description: $('description').value, start: time(start), end: time(end)};
    const notification = $('notification').value;
    if (notification === 'default') body.reminders = {useDefault: true};
    else if (notification === 'custom') {
      const values = $('notification-minutes').value.split(',').map(s => s.trim());
      if (values.length > 5 || values.some(s => !/^\d+$/.test(s) || Number(s) > 40320)) throw new Error('Enter up to five notification times in whole minutes, from 0 to 40320, separated by commas.');
      body.reminders = {useDefault:false, overrides:[...new Set(values.map(Number))].map(minutes => ({method:'popup',minutes}))};
    } else if (notification !== 'preserve') body.reminders = {useDefault: false, overrides: notification === 'none' ? [] : [{method: 'popup', minutes: Number(notification)}]};
    const headers = editing ? {'If-Match': editing.etag} : {};
    $('save-event').disabled = true;
    try { await api(path + '/events' + (editing ? '/' + enc(editing.id) : ''), editing ? 'PATCH' : 'POST', body, headers); resetEditor(); await load(); $('notice').textContent = 'Event saved.'; }
    finally { $('save-event').disabled = false; }
  }); };
  $('confirm-delete').onclick = () => run(async () => { if (!pendingDelete) return; $('confirm-delete').disabled = true; try { await api('/confirm-delete', 'POST', {confirmation: pendingDelete.confirmation}); pendingDelete = null; $('delete-dialog').close(); await load(); $('notice').textContent = 'Event removed.'; } finally { $('confirm-delete').disabled = false; } });
  $('cancel-delete').onclick = () => { pendingDelete = null; $('delete-dialog').close(); };
  $('cancel-edit').onclick = resetEditor; $('new-event').onclick = () => run(() => edit(null));
  $('refresh').onclick = () => run(refresh); $('load-events').onclick = () => run(() => load()); $('more').onclick = () => run(() => load(true)); $('calendar').onchange = () => { rows = []; next = null; resetEditor(); $('more').hidden = true; $('events').replaceChildren(node('p','Fetch events for this calendar.')); };
  $('from').value = local(new Date()).slice(0,10); $('to').value = local(new Date(Date.now() + 7*86400000)).slice(0,10);
  run(async () => { const response = await fetch('/api/satellites'); if (response.ok) { const data = await response.json(); satellites = (Array.isArray(data) ? data : data.items || data.satellites || []).map(s => s.device || s); } await refresh(); });
})();

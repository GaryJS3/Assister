'use strict';
(() => {
  const $ = id => document.getElementById(id);
  let items = [], editing = null, defaultZone = 'America/New_York', busy = false;
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  function notice(message) { $('notice').textContent = message; }
  async function request(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(error.error || 'Request failed (' + response.status + '). Please try again.');
    }
    return response.status === 204 ? null : response.json();
  }
  function locked(value) {
    busy = value;
    document.querySelectorAll('button').forEach(button => { button.disabled = value; });
  }
  function reset() {
    editing = null;
    $('reminder-form').reset();
    $('zone').value = defaultZone;
    $('editor-title').textContent = 'Add a reminder';
    $('save').textContent = 'Add reminder';
    $('cancel').hidden = true;
  }
  function edit(item) {
    editing = item.id;
    $('message').value = item.message;
    $('weekday').value = item.weekday ?? '';
    $('time').value = String(item.hour).padStart(2, '0') + ':' + String(item.minute).padStart(2, '0');
    $('zone').value = item.timeZone;
    $('satellite').value = item.satelliteId;
    if (!$('satellite').value) {
      const option = new Option(item.satelliteId, item.satelliteId);
      $('satellite').append(option);
      $('satellite').value = item.satelliteId;
    }
    $('editor-title').textContent = 'Edit reminder';
    $('save').textContent = 'Save changes';
    $('cancel').hidden = false;
    $('message').focus();
  }
  function text(tag, value, className) {
    const element = document.createElement(tag);
    element.textContent = value;
    if (className) element.className = className;
    return element;
  }
  function render() {
    $('count').textContent = items.length + ' active';
    $('reminders').replaceChildren();
    if (!items.length) $('reminders').append(text('p', 'Nothing scheduled yet. Add your first reminder, or ask Assister to remind you.', 'empty'));
    for (const item of items) {
      const row = document.createElement('article');
      row.className = 'reminder';
      const content = document.createElement('div');
      content.append(text('h3', item.message));
      const clock = String(item.hour).padStart(2, '0') + ':' + String(item.minute).padStart(2, '0');
      content.append(text('p', (item.oneTime ? 'Once' : item.weekday === null ? 'Every day' : 'Every ' + days[item.weekday]) + ' · ' + clock + ' · ' + item.importance, 'rhythm'));
      let next;
      try { next = new Intl.DateTimeFormat(undefined, { timeZone: item.timeZone, weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(item.nextDueAt)); }
      catch { next = new Date(item.nextDueAt).toISOString(); }
      content.append(text('p', 'Next: ' + next + ' · ' + item.timeZone));
      content.append(text('p', 'Announcement → ' + (item.satelliteId === 'all' ? 'All satellites' : item.satelliteId)));
      const actions = document.createElement('div');
      actions.className = 'reminder-actions';
      const editButton = text('button', 'Edit');
      editButton.type = 'button';
      editButton.setAttribute('aria-label', 'Edit reminder: ' + item.message);
      editButton.onclick = () => edit(item);
      const removeButton = text('button', 'Remove');
      removeButton.type = 'button';
      removeButton.setAttribute('aria-label', 'Remove reminder: ' + item.message);
      removeButton.onclick = async () => {
        if (busy || !confirm('Remove reminder "' + item.message + '"?')) return;
        locked(true);
        try {
          await request('/api/reminders/' + item.id, { method: 'DELETE' });
          if (editing === item.id) reset();
          await load();
          notice('Reminder removed.');
        } catch (error) { notice(error.message); }
        finally { locked(false); }
      };
      actions.append(editButton, removeButton);
      row.append(content, actions);
      $('reminders').append(row);
    }
  }
  async function load() {
    const loaded = [];
    let page;
    do {
      page = await request('/api/reminders?offset=' + loaded.length);
      loaded.push(...page.items);
    } while (page.items.length === 100);
    items = loaded;
    defaultZone = page.defaultTimeZone;
    if (!editing && !$('zone').value) $('zone').value = defaultZone;
    render();
  }
  $('reminder-form').onsubmit = async event => {
    event.preventDefault();
    if (busy) return;
    locked(true);
    const [hour, minute] = $('time').value.split(':').map(Number);
    const body = { message: $('message').value.trim(), satelliteId: $('satellite').value.trim(),
      hour, minute, weekday: $('weekday').value === '' ? null : Number($('weekday').value), timeZone: $('zone').value.trim() };
    try {
      const wasEditing = editing !== null;
      await request('/api/reminders' + (editing ? '/' + editing : ''), {
        method: editing ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      reset();
      await load();
      notice(wasEditing ? 'Reminder updated.' : 'Reminder added.');
    } catch (error) { notice(error.message); }
    finally { locked(false); }
  };
  $('cancel').onclick = () => { reset(); notice(''); };
  $('refresh').onclick = async () => {
    if (busy) return;
    locked(true);
    try { await load(); notice('Reminders refreshed.'); }
    catch (error) { notice(error.message); }
    finally { locked(false); }
  };
  async function start() {
    locked(true);
    try { await load(); }
    catch (error) { $('reminders').textContent = 'Reminders could not be loaded.'; notice(error.message); }
    finally { locked(false); }
    try {
      const satellites = await request('/api/satellites');
      for (const entry of satellites) {
        const option = document.createElement('option');
        option.value = entry.device.id;
        option.textContent = entry.device.name;
        if (![...$('satellite').options].some(existing => existing.value === option.value)) $('satellite').append(option);
      }
    } catch { /* Manual satellite IDs remain available. */ }
  }
  start();
})();

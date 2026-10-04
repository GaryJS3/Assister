'use strict';
const $ = id => document.getElementById(id);
const node = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; };
const id = prefix => `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
let nativePhrases = {}, actions = [];
const actionFor = actionId => actions.find(a => a.id === actionId);
const actionLabel = actionId => { const a = actionFor(actionId); return a ? `${a.integrationName} → ${a.name}` : actionId; };
function renderInputs() {
  const a = actionFor($('action').value);
  $('action-inputs').textContent = a ? `${a.id} · ${a.stateChanging ? 'Changes state' : 'Read-only / local response'} · Inputs: ${a.inputs.map(i => `${i.label} (${i.type}${i.required ? ', required' : ', optional'})`).join(', ') || 'None'}` : '';
  for (const [field, input] of [['fixed-target', 'target'], ['fixed-brightness', 'brightness'], ['fixed-area', 'area']]) $(field).disabled = selected?.builtIn || !a?.inputs.some(i => i.name === input);
}
function populateActions(actionId) {
  $('action').replaceChildren();
  for (const a of actions.filter(a => a.integrationId === $('integration').value)) { const option = node('option', a.name); option.value = a.id; $('action').append(option); }
  if (actionId) $('action').value = actionId;
  renderInputs();
}
let definitions = [], examples = [], selected = null, preview = null, exampleId = null, testResults = [], inspectionGeneration = 0;
async function api(path, method = 'GET', body) {
  const r = await fetch(`/api/intents${path}`, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (r.status === 204) return null;
  const result = await r.json();
  if (!r.ok) throw new Error(result.error || `Request failed (${r.status})`);
  return result;
}
function action(fn) { return async event => { if (event) event.preventDefault(); $('notice').textContent = ''; try { await fn(event); } catch (e) { $('notice').textContent = e.message; } }; }
function panel(name) {
  for (const p of ['workspace', 'examples', 'candidates']) $(p).hidden = p !== name;
  document.querySelectorAll('[data-panel]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.panel === name)));
}
function invalidate() { inspectionGeneration++; preview = null; $('execute').disabled = true; $('save-example').disabled = true; $('execution').replaceChildren(); $('preview').replaceChildren(node('p', 'Inspect this request to see the current plan.', 'muted')); }
function definition() {
  return { id: selected?.id || id('intent'), name: $('name').value.trim(), actionId: $('action').value, enabled: $('enabled').checked,
    patterns: $('patterns').value.split('\n').map(x => x.trim()).filter(Boolean), target: $('fixed-target').disabled ? null : $('fixed-target').value.trim() || null,
    brightness: $('fixed-brightness').disabled || $('fixed-brightness').value === '' ? null : Number($('fixed-brightness').value), area: $('fixed-area').disabled ? null : $('fixed-area').value.trim() || null,
    response: $('response-template').value, builtIn: selected?.builtIn || false, version: selected?.version || 0 };
}
function select(d) {
  const phrases = nativePhrases[d.id] || []; $('native-coverage').hidden = !phrases.length;
  $('native-phrases').replaceChildren(...phrases.map(text => node('li', text)));
  selected = d; $('definition-form').hidden = false; $('editor-empty').hidden = true; $('editor-title').textContent = d.name || 'New intent';
  $('definition-kind').textContent = d.builtIn ? 'BUILT-IN' : 'CUSTOM';
  $('name').value = d.name; $('integration').value = actionFor(d.actionId).integrationId; populateActions(d.actionId); $('integration').disabled = d.builtIn; $('action').disabled = d.builtIn;
  $('enabled').checked = d.enabled; $('patterns').value = d.patterns.join('\n');
  $('pattern-label').textContent = d.builtIn && !['assister.time', 'assister.date'].includes(d.actionId) ? ' / additional aliases' : '';
  $('fixed-target').value = d.target || ''; $('fixed-brightness').value = d.brightness ?? ''; $('fixed-area').value = d.area || '';
  renderInputs();
  $('response-template').value = d.response; $('delete-intent').hidden = d.builtIn || !definitions.some(x => x.id === d.id);
  $('dirty').textContent = ''; $('preview-draft').checked = false; invalidate(); renderLibrary();
}
function renderLibrary() {
  const filter = $('search').value.toLowerCase(); $('intent-list').replaceChildren();
  $('intent-count').textContent = `${definitions.filter(x => x.enabled).length} ON`;
  for (const d of definitions.filter(x => `${x.name} ${actionLabel(x.actionId)}`.toLowerCase().includes(filter))) {
    const b = node('button', undefined, `intent-item${selected?.id === d.id ? ' selected' : ''}${d.enabled ? '' : ' off'}`);
    b.type = 'button'; b.setAttribute('aria-pressed', String(selected?.id === d.id));
    b.append(node('strong', d.name), node('small', `${actionLabel(d.actionId)} · ${d.builtIn ? 'native' : 'custom'} · ${d.enabled ? 'enabled' : 'disabled'}`));
    b.onclick = () => select(d); $('intent-list').append(b);
  }
}
async function refresh() {
  [definitions, examples] = await Promise.all([api(''), api('/examples')]);
  renderLibrary(); renderExamples();
  if (selected && definitions.some(x => x.id === selected.id)) select(definitions.find(x => x.id === selected.id));
  else if (!selected && definitions.length) select(definitions[0]);
}
function fact(list, label, text) { const row = node('div'); row.append(node('dt', label), node('dd', text)); list.append(row); }
function renderPreview(p) {
  $('preview').replaceChildren(); const head = node('div', undefined, 'decision-head');
  head.append(node('strong', p.action ? `${p.action.integrationName} → ${p.action.name}` : (p.matchStatus === 'unmatched' ? 'No intent match' : 'Routing decision')), node('span', p.outcome, `status ${p.outcome}`));
  $('preview').append(head, node('p', p.reason, 'muted'));
  const list = node('dl', undefined, 'decision-steps'); const match = p.matchStatus === 'matched' ? p.candidates[0] : null;
  fact(list, 'Input', p.normalized);
  fact(list, 'Rule', match ? `${match.definition.name} (${match.definition.id})` : p.candidates.map(c => c.definition.name).join(' / ') || 'None');
  if (match) {
    fact(list, 'Phrase', match.pattern);
    if (match.intent) fact(list, 'Slots', Object.entries(match.intent).filter(([k, v]) => v !== null && !['kind', 'matchedRule'].includes(k)).map(([k, v]) => `${k}: ${v}`).join(' · '));
  }
  fact(list, 'Targets', p.entityIds.length ? p.entityIds.join(', ') : 'No device target');
  if (p.alternatives.length) fact(list, 'Options', p.alternatives.map(x => `${x.name} (${x.entityId})`).join(', '));
  fact(list, 'Execution', p.stateChanging ? 'Device command — not sent' : 'Read-only / response');
  $('preview').append(list);
  if (p.response !== null) { const response = node('div', undefined, 'response-preview'); response.append(node('small', p.stateChanging ? 'EXPECTED AFTER CONFIRMED COMPLETION' : 'RESPONSE PREVIEW'), node('p', p.response)); $('preview').append(response); }
  if ($('preview-draft').checked) $('preview').append(node('p', 'Unsaved draft included. Save and inspect again before executing.', 'hint'));
  $('execute').disabled = !p.canExecute; $('save-example').disabled = $('preview-draft').checked || p.matchStatus === 'timer-route';
}
function openExample(e) {
  exampleId = e.id || id('example'); $('example-title').textContent = e.id ? 'Edit a test.' : 'Save a test.';
  $('example-name').value = e.name || ''; $('example-text').value = e.text || ''; $('example-area').value = e.area || '';
  $('example-status').value = e.matchStatus || 'matched'; $('example-target').value = e.expectedTarget || ''; $('example-brightness').value = e.expectedBrightness ?? '';
  $('example-rule').replaceChildren(); const none = node('option', 'Any / no rule'); none.value = ''; $('example-rule').append(none);
  for (const d of definitions) { const option = node('option', d.name); option.value = d.id; $('example-rule').append(option); }
  if (e.expectedRuleId && !definitions.some(x => x.id === e.expectedRuleId)) { const deleted = node('option', `${e.expectedRuleId} (deleted)`); deleted.value = e.expectedRuleId; $('example-rule').append(deleted); }
  $('example-rule').value = e.expectedRuleId || ''; $('example-error').textContent = ''; $('example-dialog').showModal();
}
function renderExamples() {
  if (!testResults.length) $('test-summary').textContent = '';
  $('test-count').textContent = examples.length; $('example-list').replaceChildren();
  if (!examples.length) { $('example-list').append(node('p', 'No tests saved yet. Inspect a request and save its expected match.', 'empty')); return; }
  const table = node('table'), head = node('thead'), hr = node('tr'), body = node('tbody');
  for (const title of ['Test / request', 'Expected', 'Result', 'Actions']) hr.append(node('th', title)); head.append(hr); table.append(head, body);
  for (const e of examples) {
    const tr = node('tr'), label = node('td'); label.append(node('strong', e.name), node('small', e.text));
    const expected = node('td', e.matchStatus); expected.append(node('small', e.expectedRuleId || 'Any / no rule'));
    const result = testResults.find(x => x.example.id === e.id), outcome = node('td'); outcome.append(node('span', result ? result.passed ? 'PASS' : 'FAIL' : 'NOT RUN', `status ${result ? result.passed ? 'passed' : 'failed' : ''}`));
    if (result) outcome.append(node('small', result.failures.join(' ') || `Resolution: ${result.actual.outcome}`));
    const actions = node('td'), inspect = node('button', 'Inspect'), edit = node('button', 'Edit'), remove = node('button', 'Delete');
    inspect.onclick = action(async () => { panel('workspace'); $('request').value = e.text; $('request-area').value = e.area || ''; $('preview-draft').checked = false; await inspectRequest(); });
    edit.onclick = () => openExample(e); remove.onclick = action(async () => { await api(`/examples/${encodeURIComponent(e.id)}`, 'DELETE'); examples = await api('/examples'); testResults = []; renderExamples(); });
    actions.append(inspect, edit, remove); tr.append(label, expected, outcome, actions); body.append(tr);
  }
  $('example-list').append(table);
}
async function inspectRequest() {
  invalidate(); $('inspect').disabled = true;
  const generation = inspectionGeneration;
  try {
    const request = { text: $('request').value, area: $('request-area').value.trim() || null };
    if ($('preview-draft').checked) { if (!selected) throw new Error('Choose an intent to preview a draft.'); request.draft = definition(); }
    const result = await api('/inspect', 'POST', request);
    if (generation === inspectionGeneration) { preview = result; renderPreview(preview); }
  } finally { $('inspect').disabled = false; }
}
async function candidates() {
  const r = await fetch('/api/diagnostics/runs'); if (!r.ok) throw new Error('Recent requests are unavailable.');
  const runs = await r.json(), seen = new Set(); $('candidate-list').replaceChildren();
  for (const run of runs.filter(x => x.handledBy === 'language-model' || x.outcome === 'unmatched')) {
    const text = run.userText?.trim(); if (!text || seen.has(text.toLowerCase())) continue; seen.add(text.toLowerCase());
    const row = node('div', undefined, 'candidate'), description = node('div'); description.append(node('p', text), node('small', run.rawResponse || 'No response'));
    const inspect = node('button', 'Inspect'); inspect.onclick = action(async () => { panel('workspace'); $('request').value = text; $('request-area').value = run.area || ''; $('preview-draft').checked = false; await inspectRequest(); });
    row.append(description, node('small', `${run.handledBy || 'unmatched'} · ${run.outcome}`), inspect); $('candidate-list').append(row);
  }
  if (!seen.size) $('candidate-list').append(node('p', 'No recent LLM or unmatched requests in the retained history.', 'empty'));
}
document.querySelectorAll('[data-panel]').forEach(b => b.onclick = action(async () => { panel(b.dataset.panel); if (b.dataset.panel === 'candidates') await candidates(); }));
document.querySelectorAll('[data-close]').forEach(b => b.onclick = () => $(b.dataset.close).close());
document.querySelectorAll('[data-request]').forEach(b => b.onclick = action(async () => { $('request').value = b.dataset.request; $('preview-draft').checked = false; await inspectRequest(); }));
$('search').oninput = renderLibrary;
$('refresh').onclick = action(refresh);
$('new-intent').onclick = () => select({ id: id('intent'), name: '', actionId: 'assister.reply', enabled: true, patterns: ['hello assister'], response: 'Hello.', builtIn: false, version: 0 });
$('definition-form').oninput = () => { $('dirty').textContent = 'Unsaved changes'; if ($('preview-draft').checked) invalidate(); };
$('integration').onchange = () => { populateActions(); invalidate(); };
$('action').onchange = () => { renderInputs(); if ($('action').value === 'assister.reply' && $('response-template').value === '{response}') $('response-template').value = 'Hello.'; invalidate(); };
$('definition-form').onsubmit = action(async () => {
  $('save-intent').disabled = true;
  try { const d = definition(); selected = await api(`/${encodeURIComponent(d.id)}`, 'PUT', d); testResults = []; await refresh(); $('notice').textContent = 'Intent saved. Changes apply to the next request.'; }
  finally { $('save-intent').disabled = false; }
});
$('delete-intent').onclick = action(async () => { await api(`/${encodeURIComponent(selected.id)}?version=${selected.version}`, 'DELETE'); selected = null; testResults = []; await refresh(); $('notice').textContent = 'Custom intent deleted. Saved tests keep their expectations.'; });
$('inspect-form').onsubmit = action(inspectRequest);
for (const field of ['request', 'request-area', 'preview-draft']) $(field).addEventListener('input', invalidate);
$('save-example').onclick = () => {
  const match = preview.matchStatus === 'matched' ? preview.candidates[0] : null;
  openExample({ name: preview.text.slice(0, 128), text: preview.text, area: $('request-area').value.trim() || null, matchStatus: preview.matchStatus,
    expectedRuleId: match?.definition.id || null, expectedTarget: match?.intent?.target || null, expectedBrightness: match?.intent?.brightnessPercent ?? null });
};
$('add-example').onclick = () => openExample({});
$('example-form').onsubmit = async event => {
  event.preventDefault(); $('example-error').textContent = '';
  try {
    await api(`/examples/${encodeURIComponent(exampleId)}`, 'PUT', { id: exampleId, name: $('example-name').value.trim(), text: $('example-text').value,
      area: $('example-area').value.trim() || null, expectedRuleId: $('example-rule').value || null, matchStatus: $('example-status').value,
      expectedTarget: $('example-target').value.trim() || null, expectedBrightness: $('example-brightness').value === '' ? null : Number($('example-brightness').value) });
    $('example-dialog').close(); examples = await api('/examples'); testResults = []; renderExamples();
  } catch (e) { $('example-error').textContent = e.message; }
};
$('run-tests').onclick = action(async () => {
  $('run-tests').disabled = true;
  try { testResults = await api('/tests', 'POST', {}); const passed = testResults.filter(x => x.passed).length; $('test-summary').textContent = `${passed} / ${testResults.length} passed. No actions executed.`; renderExamples(); }
  finally { $('run-tests').disabled = false; }
});
$('load-candidates').onclick = action(candidates);
$('execute').onclick = () => {
  $('execution-plan').replaceChildren(node('p', preview.text), node('p', `Integration: ${preview.action.integrationName}`), node('p', `Action: ${preview.action.name}`), node('p', `Targets: ${preview.entityIds.join(', ') || 'No device'}`), node('p', `Expected response: ${preview.response || ''}`));
  $('execute-dialog').showModal();
};
$('confirm-execute').onclick = action(async () => {
  const inspected = preview; $('execute-dialog').close(); $('execute').disabled = true;
  try {
    const result = await api('/execute', 'POST', { text: inspected.text, area: $('request-area').value.trim() || null, fingerprint: inspected.fingerprint });
    $('execution').replaceChildren(node('span', `${result.outcome}: ${result.response}`, `status ${result.outcome === 'succeeded' ? 'passed' : 'failed'}`));
    const link = node('a', 'Open execution trace ↗'); link.href = `/?run=${encodeURIComponent(result.runId)}`; $('execution').append(link);
    preview = null; $('save-example').disabled = true;
  } catch (e) { invalidate(); throw e; }
});
action(async () => {
  const catalog = await api('/catalog'); nativePhrases = catalog.nativePhrases; actions = catalog.actions; for (const integration of catalog.integrations) { const option = node('option', integration.name); option.value = integration.id; $('integration').append(option); }
  await refresh();
})();

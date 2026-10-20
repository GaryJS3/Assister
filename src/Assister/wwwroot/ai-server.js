'use strict';
const field = id => document.getElementById(id);
function populate(settings) {
  for (const key of ['baseUrl', 'model', 'temperature', 'maxTokens', 'timeoutSeconds']) field(key).value = settings[key];
  field('enableThinking').value = settings.enableThinking === null ? '' : String(settings.enableThinking);
  field('apiKey').value = '';
  field('apiKey').placeholder = settings.hasApiKey ? 'A key is configured' : 'Optional';
  field('clearApiKey').checked = false;
}
function notice(message, error = false) {
  field('notice').textContent = message;
  field('notice').classList.toggle('error', error);
}
async function request(options) {
  const response = await fetch('/api/settings/ai-server', { cache: 'no-store', ...options });
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.error || `Unable to update AI settings (HTTP ${response.status}).`);
  }
  return response.json();
}
field('settings').addEventListener('submit', async event => {
  event.preventDefault();
  const values = {};
  for (const key of ['baseUrl', 'model', 'apiKey']) values[key] = field(key).value;
  for (const key of ['temperature', 'maxTokens', 'timeoutSeconds']) values[key] = Number(field(key).value);
  values.enableThinking = field('enableThinking').value === '' ? null : field('enableThinking').value === 'true';
  values.clearApiKey = field('clearApiKey').checked;
  field('fields').disabled = true;
  notice('Saving…');
  try {
    populate(await request({ method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) }));
    notice('Saved. Subsequent requests will use these settings.');
  } catch (error) { notice(error.message, true); }
  finally { field('fields').disabled = false; }
});
request().then(settings => { populate(settings); field('fields').disabled = false; notice(''); })
  .catch(error => notice(error.message, true));

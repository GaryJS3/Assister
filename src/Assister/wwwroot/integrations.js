'use strict';
const node = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; };
async function refresh() {
  document.getElementById('refresh').disabled = true;
  try {
    const response = await fetch('/api/integrations', {cache:'no-store'});
    if (!response.ok) throw new Error('Integration catalog is unavailable.');
    const integrations = await response.json(), list = document.getElementById('integrations');
    list.replaceChildren(); document.getElementById('notice').textContent = '';
    for (const integration of integrations) {
      const card = node('article', undefined, 'summary-card integration');
      const heading = node('div', undefined, 'summary-top');
      heading.append(node('h2', integration.name), node('span', integration.status, 'badge'));
      card.append(heading, node('p', integration.description, 'muted'), node('p', `${integration.id} · ${integration.builtIn ? 'Built-in' : 'Extension'}`, 'mono'));
      const actions = node('ul', undefined, 'integration-actions');
      for (const action of integration.actions) {
        const row = node('li'); row.append(node('strong', action.name), node('span', action.stateChanging ? 'Changes state' : 'Read-only / local response', 'badge'), node('code', action.id));
        row.append(node('p', action.inputs.length ? `Inputs: ${action.inputs.map(i => `${i.label} (${i.type}, ${i.required ? 'required' : 'optional'})`).join(', ')}` : 'No inputs.', 'muted'));
        actions.append(row);
      }
      card.append(actions); const link = node('a', 'Manage intents →'); link.href = '/intents.html'; card.append(link); list.append(card);
    }
  } catch (error) { document.getElementById('notice').textContent = error.message; }
  finally { document.getElementById('refresh').disabled = false; }
}
document.getElementById('refresh').onclick = refresh;
refresh();

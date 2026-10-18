const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup() {
  const requests = [];
  class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.attributes = {}; this.pauses = 0; }
    append(...items) { this.children.push(...items); }
    replaceChildren(...items) { this.children = [...items]; }
    setAttribute(key, value) { this.attributes[key] = value; }
    pause() { this.pauses++; }
  }
  let reply = async () => ({ ok: true, status: 200, json: async () => [
    { direction: 'input', state: 'available', url: '/input.wav', size: 46 },
    { direction: 'output', state: 'available', url: '/output.wav', size: 46 },
  ] });
  const context = vm.createContext({ window: {}, document: { createElement: tag => new Element(tag) },
    fetch: async (...args) => { requests.push(args); return reply(...args); }, Date, encodeURIComponent });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Assister/wwwroot/run-audio.js'), 'utf8'), context);
  const host = new Element('section');
  const panel = new context.window.RunAudioPanel(host);
  return { panel, host, requests, respond: fn => { reply = fn; } };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('trace polling preserves both players and review playback issues no acknowledgements', async () => {
  const { panel, requests } = setup();
  panel.select({ runId: 'one', source: 'voice' }); await tick();
  const input = panel.slots.get('input').audio, output = panel.slots.get('output').audio;
  assert.equal(input.controls, true); assert.equal(input.preload, 'none');
  assert.equal(input.attributes['aria-label'], 'Input recording');
  panel.select({ runId: 'one', source: 'voice', finishedAt: 'now' }); await panel.refresh();
  assert.equal(panel.slots.get('input').audio, input);
  assert.equal(input.pauses, 0);
  output.onplay(); assert.equal(input.pauses, 1);
  assert.ok(requests.every(([url, options]) => url.endsWith('/audio') && !options.method));
});

test('changing runs pauses old audio and late metadata cannot overwrite the new selection', async () => {
  const app = setup();
  app.panel.select({ runId: 'one', source: 'voice' }); await tick();
  const audio = app.panel.slots.get('input').audio;
  let release;
  app.respond(() => new Promise(resolve => { release = resolve; }));
  const old = app.panel.refresh();
  app.respond(async () => ({ ok: true, status: 200, json: async () => [{ direction: 'input', state: 'expired' }] }));
  app.panel.select({ runId: 'two', source: 'voice' }); await tick();
  release({ ok: true, status: 200, json: async () => [{ direction: 'input', state: 'available', url: '/old.wav' }] });
  await old;
  assert.equal(audio.pauses, 1);
  assert.equal(app.panel.run.runId, 'two');
  assert.equal(app.panel.slots.get('input').audio, null);
  assert.match(app.panel.slots.get('input').status.textContent, /expired/);
});

test('rich recordings use authenticated routes and present sign-in and expired states', async () => {
  const app = setup();
  app.respond(async () => ({ status: 401 }));
  app.panel.select({ runId: 'private', source: 'rich-client' }); await tick();
  assert.match(app.requests[0][0], /^\/api\/client\/runs/);
  assert.equal(app.panel.notice.children[0].href, '/chat.html');
  app.respond(async () => ({ ok: true, status: 200, json: async () => [
    { direction: 'input', state: 'expired' }, { direction: 'output', state: 'not-captured' },
  ] }));
  app.panel.select({ runId: 'private', source: 'rich-client', finishedAt: 'now' }); await app.panel.refresh();
  assert.match(app.panel.slots.get('input').status.textContent, /expired/);
  assert.match(app.panel.slots.get('output').status.textContent, /No recording/);
});

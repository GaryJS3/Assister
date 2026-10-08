const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function client() {
  const elements = new Map(), played = [];
  class Audio {
    constructor(url) { this.src = url; this.ended = false; }
    play() { played.push(this); return Promise.resolve(); }
    pause() { this.onpause?.(); }
    end() { this.ended = true; this.onended?.(); }
  }
  const element = () => ({ checked: true, append() {}, textContent: '', hidden: false });
  const document = { getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    createElement(type) { return type === 'audio' ? new Audio() : element(); } };
  const context = vm.createContext({ document, Audio, setTimeout, clearTimeout, clearInterval,
    window: { addEventListener() {} }, crypto: { randomUUID: () => 'playback-id' },
    fetch: async () => ({ ok: true, text: async () => '' }) });
  const source = fs.readFileSync(path.join(__dirname, '../../src/Assister/wwwroot/chat.js'), 'utf8');
  vm.runInContext(source.replace('load().catch(error);', `globalThis.hooks = { apply, stopTones, setup() {
    active = 'request'; const v = { cursor: 0, row: { append() {} }, mediaStatus: {}, status: {} }; views.set(active, v); return v;
  }, drain: () => toneQueue };`), context);
  const view = context.hooks.setup();
  let sequence = 0;
  const send = (type, data, replaying = false) => context.hooks.apply({ id: 'request' }, { sequence: ++sequence, type, data }, replaying);
  const cue = (name, placement = 'immediate') => ({ name, url: `/api/voice/tones/${name}.wav`, expiresAt: new Date(Date.now() + 5000).toISOString(), placement });
  return { ...context.hooks, view, played, send, cue, elements };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('live cues serialize before speech and Goodbye follows speech only once', async () => {
  const c = client();
  c.send('tone.play', c.cue('confirmed'));
  c.send('tone.play', c.cue('done'));
  c.send('tts.audio', { url: '/answer.wav' });
  c.send('tone.play', c.cue('goodbye', 'after-response-audio'));
  await tick(); assert.equal(c.played.length, 1);
  c.played[0].end(); await tick(); assert.equal(c.played[1].src, '/api/voice/tones/done.wav');
  c.played[1].end(); await tick(); assert.equal(c.played[2].src, '/answer.wav');
  c.played[2].end(); await tick(); assert.equal(c.played[3].src, '/api/voice/tones/goodbye.wav');
  c.played[3].end(); await c.drain();
  c.played[2].end(); await tick(); assert.equal(c.played.length, 4);
});

test('history, expired cues and duplicate delivery stay silent', async () => {
  const c = client();
  c.send('tone.play', c.cue('confirmed'), true);
  c.send('tone.play', { ...c.cue('done'), expiresAt: new Date(0).toISOString() });
  c.send('tone.play', c.cue('ai-think'));
  c.apply({ id: 'request' }, { sequence: 3, type: 'tone.play', data: c.cue('ai-think') });
  await tick(); assert.equal(c.played.length, 1);
  c.played[0].end(); await c.drain();
});

test('cancellation clears current and queued tones, speech and deferred Goodbye', async () => {
  const c = client();
  c.send('tone.play', c.cue('done'));
  c.send('tts.audio', { url: '/answer.wav' });
  c.send('tone.play', c.cue('goodbye', 'after-response-audio'));
  await tick();
  c.send('interaction.cancelled', {});
  await tick(); assert.equal(c.played.length, 1); assert.equal(c.view.goodbye, null);
  c.view.audio.end(); await tick(); assert.equal(c.played.length, 1);
});

test('stopped speech discards deferred Goodbye', async () => {
  const c = client(); c.send('tts.audio', { url: '/answer.wav' });
  c.send('tone.play', c.cue('goodbye', 'after-response-audio'));
  await tick(); c.view.audio.onplaying(); c.view.audio.pause(); c.view.audio.end();
  await tick(); assert.equal(c.played.length, 1); assert.equal(c.view.goodbye, null);
});

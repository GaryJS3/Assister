const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function client(cryptoOverride) {
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
    window: { addEventListener() {} }, crypto: cryptoOverride || { randomUUID: () => 'playback-id' },
    fetch: async () => ({ ok: true, text: async () => '' }) });
  const source = fs.readFileSync(path.join(__dirname, '../../src/Assister/wwwroot/chat.js'), 'utf8');
  vm.runInContext(source.replace('load().catch(error);', `globalThis.hooks = { apply, stopTones, newId, setup() {
    active = 'request'; const v = { cursor: 0, row: { append() {} }, mediaStatus: {}, status: {} }; views.set(active, v); return v;
  }, enableStreaming() { speechContext = { state: 'running' }; window.AssisterSpeechPlayer = class {
    constructor(ctx, options) { this.options = options; this.segments = []; }
    add(segment) { this.segments.push(segment); }
    finish(count) { this.count = count; }
    stop() { this.stopped = true; }
    end() { this.ended = true; this.options.done(); }
  }; }, drain: () => toneQueue };`), context);
  const view = context.hooks.setup();
  let sequence = 0;
  const send = (type, data, replaying = false) => context.hooks.apply({ id: 'request' }, { sequence: ++sequence, type, data }, replaying);
  const cue = (name, placement = 'immediate') => ({ name, url: `/api/voice/tones/${name}.wav`, expiresAt: new Date(Date.now() + 5000).toISOString(), placement });
  return { ...context.hooks, view, played, send, cue, elements };
}
const tick = () => new Promise(resolve => setImmediate(resolve));
test('private HTTP origins generate valid version-four identifiers without randomUUID', () => {
  let seed = 0;
  const c = client({ getRandomValues(bytes) { for (let i = 0; i < bytes.length; i++) bytes[i] = (seed++ * 17) & 255; return bytes; } });
  const first = c.newId(), second = c.newId();
  assert.match(first, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.notEqual(first, second);
});

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
test('streamed speech suppresses retained-recording autoplay and Goodbye waits for stream playback', async () => {
  const c = client(); c.enableStreaming(); c.send('tts.segment', { index: 0 });
  c.view.mediaStatus.textContent = 'Speaking';
  c.send('tts.audio', { url: '/retained.wav' }); c.send('tts.completed', { streaming: true, segmentCount: 1 });
  assert.equal(c.view.mediaStatus.textContent, 'Speaking');
  c.send('tone.play', c.cue('goodbye', 'after-response-audio')); await tick();
  assert.equal(c.played.length, 0); assert.equal(c.view.stream.count, 1);
  c.view.stream.end(); await tick(); assert.equal(c.played.length, 1);
  assert.equal(c.played[0].src, '/api/voice/tones/goodbye.wav'); c.played[0].end(); await c.drain();
});
test('historical segments stay silent and cancellation stops live streamed speech', async () => {
  const c = client(); c.enableStreaming(); c.send('tts.segment', { index: 0 }, true);
  assert.equal(c.view.stream, undefined);
  c.send('tts.segment', { index: 0 }); c.send('interaction.cancel_requested', {});
  assert.equal(c.view.stream.stopped, true);
});

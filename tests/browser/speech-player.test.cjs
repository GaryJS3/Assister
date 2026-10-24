const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const tick = () => new Promise(resolve => setImmediate(resolve));
function setup(fetchOverride) {
  const sources = [], reports = [], states = [];
  const context = { state: 'running', currentTime: 0, destination: {},
    decodeAudioData: async () => ({ duration: .5, numberOfChannels: 1 }),
    createBufferSource() { const source = { connect() {}, disconnect() {}, start(at) { this.at = at; }, stop() { this.stopped = true; }, end() { this.onended?.(); } }; sources.push(source); return source; } };
  const sandbox = { window: {}, AbortController, setTimeout,
    fetch: fetchOverride || (async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(16044) })) };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Assister/wwwroot/speech-player.js'), 'utf8'), sandbox);
  const player = new sandbox.window.AssisterSpeechPlayer(context, { report: state => reports.push(state), status: state => states.push(state) });
  return { player, context, sources, reports, states };
}
const segment = index => ({ index, url: `/segments/${index}`, sampleRate: 16000, sampleWidth: 2, channels: 1, size: 16044 });
test('first audio is scheduled before completion, ordered segments join without gaps, completion waits for playback', async () => {
  const c = setup(); c.player.add(segment(0)); await tick();
  assert.equal(c.sources.length, 1); assert.deepEqual(c.reports, ['started']);
  c.player.add(segment(1)); c.player.finish(2); await tick();
  assert.equal(c.sources[1].at, c.sources[0].at + .5);
  c.sources[0].end(); assert.deepEqual(c.reports, ['started']);
  c.sources[1].end(); assert.deepEqual(c.reports, ['started', 'completed']);
  c.player.finish(2); c.sources[1].end(); assert.equal(c.reports.length, 2);
});
test('cancellation during download cannot schedule stale audio', async () => {
  let release;
  const c = setup(() => new Promise(resolve => { release = resolve; }));
  c.player.add(segment(0)); await tick(); c.player.stop();
  release({ ok: true, arrayBuffer: async () => new ArrayBuffer(16044) }); await tick();
  assert.equal(c.sources.length, 0); assert.equal(c.player.queue.length, 0);
  assert.deepEqual(c.reports, ['failed']);
});
test('stop clears scheduled and queued audio and is idempotent', async () => {
  const c = setup(); for (let i = 0; i < 10; i++) c.player.add(segment(i)); await tick();
  assert.ok(c.sources.length <= 5); assert.ok(c.player.queue.length > 0);
  c.player.stop(); c.player.stop(); await new Promise(resolve => setTimeout(resolve, 30));
  assert.ok(c.sources.every(source => source.stopped)); assert.equal(c.player.queue.length, 0);
  assert.deepEqual(c.reports, ['started', 'stopped']);
});
test('gaps, mismatched completion and missing segments fail explicitly', async () => {
  const gap = setup(); gap.player.add(segment(1)); assert.deepEqual(gap.reports, ['failed']);
  const count = setup(); count.player.add(segment(0)); await tick(); count.player.finish(2);
  assert.deepEqual(count.reports, ['started', 'failed']);
  const missing = setup(async () => ({ ok: false })); missing.player.add(segment(0)); await tick();
  assert.deepEqual(missing.reports, ['failed']);
});
test('tones gate first speech and a failed or suspended audio context does not hang', async () => {
  const c = setup(); let ready; c.player.ready = new Promise(resolve => { ready = resolve; });
  c.player.add(segment(0)); await tick(); assert.equal(c.sources.length, 0);
  ready(); await tick(); assert.equal(c.sources.length, 1); c.player.stop();
  const suspended = setup(); suspended.context.state = 'suspended'; suspended.player.add(segment(0)); await tick();
  assert.deepEqual(suspended.reports, ['failed']);
});

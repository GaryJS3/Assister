const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
function capture(rate, input) {
  const messages = []; let Processor;
  const environment = vm.createContext({ sampleRate: rate, AudioWorkletProcessor: class { constructor() { this.port = { postMessage: value => messages.push(value) }; } }, registerProcessor: (_, value) => { Processor = value; } });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Assister/wwwroot/pcm-capture.js'), 'utf8'), environment);
  const processor = new Processor(); processor.process([[input]]); processor.port.onmessage({ data: 'stop' });
  return messages;
}
for (const rate of [48000, 44100]) test(`${rate} Hz capture produces 16 kHz little-endian PCM`, () => {
  const messages = capture(rate, new Float32Array(rate / 100).fill(0.5));
  const buffers = messages.filter(message => message.pcm).map(message => Buffer.from(message.pcm));
  const pcm = Buffer.concat(buffers); assert.equal(pcm.length, 320);
  // Fractional resampling can fall on either side of a half-LSB rounding boundary.
  for (let index = 0; index < pcm.length; index += 2) assert.ok(Math.abs(pcm.readInt16LE(index) - 16384) <= 1);
  assert.ok(messages.some(message => message.done));
});
test('Capture stops producing data after 30 seconds even if browser timers are delayed', () => {
  const messages = capture(48000, new Float32Array(48000 * 31));
  assert.equal(messages.filter(message => message.pcm).reduce((bytes, message) => bytes + message.pcm.byteLength, 0), 960000);
  assert.ok(messages.some(message => message.limit));
});

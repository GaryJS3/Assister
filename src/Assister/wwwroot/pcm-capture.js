'use strict';
// Mono PCM at 16 kHz, independent of the browser's actual capture sample rate.
class PcmCapture extends AudioWorkletProcessor {
  constructor() {
    super(); this.ratio = sampleRate / 16000; this.remaining = this.ratio; this.sum = 0; this.samples = []; this.stopped = false; this.total = 0;
    this.port.onmessage = e => { if (e.data === 'stop') { this.stopped = true; this.flush(); this.port.postMessage({ done: true }); } };
  }
  flush() {
    if (!this.samples.length) return;
    const buffer = new ArrayBuffer(this.samples.length * 2); const view = new DataView(buffer);
    this.samples.forEach((value, index) => view.setInt16(index * 2, Math.round(Math.max(-1, Math.min(1, value)) * 32767), true));
    this.port.postMessage({ pcm: buffer }, [buffer]); this.samples = [];
  }
  process(inputs) {
    if (this.stopped) return true;
    const input = inputs[0]?.[0]; if (!input) return true;
    for (const value of input) {
      let weight = 1;
      while (weight > 0.000001) {
        const used = Math.min(weight, this.remaining); this.sum += value * used; this.remaining -= used; weight -= used;
        if (this.remaining < 0.000001) {
          this.samples.push(this.sum / this.ratio); this.sum = 0; this.remaining = this.ratio;
          if (++this.total >= 480000) { this.stopped = true; this.flush(); this.port.postMessage({ limit: true }); return true; }
          if (this.samples.length >= 1600) this.flush();
        }
      }
    }
    return true;
  }
}
registerProcessor('pcm-capture', PcmCapture);

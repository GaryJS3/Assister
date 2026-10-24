'use strict';
(() => {
  // Decode serially; schedule ~2 seconds ahead. The pending queue contains URLs, not decoded audio.
  class SpeechPlayer {
    constructor(context, { ready = Promise.resolve(), report = () => {}, status = () => {}, done = () => {} } = {}) {
      Object.assign(this, { context, ready, report, status, done });
      this.queue = []; this.sources = new Set(); this.abort = new AbortController();
      this.index = 0; this.bytes = 0; this.next = 0; this.ended = false;
    }
    add(segment) {
      if (this.stopped || this.ended) return;
      if (segment.index !== this.index || this.index >= 1024 || segment.size < 44 || segment.size > 65581 ||
          (this.bytes += segment.size) > 8 * 1024 * 1024 + 1024 * 45) return this.stop('failed');
      const format = `${segment.sampleRate}/${segment.sampleWidth}/${segment.channels}`;
      if (this.format && this.format !== format) return this.stop('failed');
      this.format = format; this.index++; this.queue.push(segment); this.pump();
    }
    finish(count) {
      if (this.stopped) return;
      if (count !== this.index) return this.stop('failed');
      this.finished = true; this.checkDone();
    }
    checkDone() {
      if (!this.stopped && !this.ended && this.finished && !this.pumping && !this.queue.length && !this.sources.size) {
        this.ended = true; this.report('completed'); this.status('Playback complete'); this.done();
      }
    }
    async pump() {
      if (this.pumping || this.stopped) return;
      this.pumping = true;
      try {
        await this.ready;
        while (this.queue.length && !this.stopped) {
          while (this.next - this.context.currentTime > 2 && !this.stopped)
            await new Promise(resolve => setTimeout(resolve, 25));
          if (this.stopped) break;
          if (this.context.state !== 'running') throw new Error('Audio context is not running');
          const segment = this.queue.shift();
          const response = await fetch(segment.url, { signal: this.abort.signal, credentials: 'same-origin' });
          if (!response.ok) throw new Error('Speech segment unavailable');
          const bytes = await response.arrayBuffer();
          if (bytes.byteLength !== segment.size) throw new Error('Invalid speech segment length');
          const buffer = await this.context.decodeAudioData(bytes);
          if (this.stopped) break;
          if (buffer.duration <= 0 || buffer.duration > .51 || buffer.numberOfChannels !== segment.channels)
            throw new Error('Invalid speech segment duration');
          const source = this.context.createBufferSource(); source.buffer = buffer; source.connect(this.context.destination);
          this.next = Math.max(this.next, this.context.currentTime + .04);
          this.sources.add(source);
          source.onended = () => { source.disconnect(); this.sources.delete(source); this.checkDone(); };
          source.start(this.next); this.next += buffer.duration;
          if (!this.started) { this.started = true; this.report('started'); this.status('Speaking'); }
        }
      } catch (e) { if (!this.stopped) this.stop('failed'); }
      finally { this.pumping = false; this.checkDone(); }
    }
    stop(state = 'stopped') {
      if (this.stopped || this.ended) return;
      this.stopped = true; this.abort.abort(); this.queue.length = 0;
      for (const source of this.sources) { source.onended = null; try { source.stop(); source.disconnect(); } catch {} }
      this.sources.clear(); this.report(this.started ? state : 'failed'); this.status(state === 'failed' ? 'Streaming playback failed; use the retained recording when available' : 'Playback stopped');
    }
  }
  window.AssisterSpeechPlayer = SpeechPlayer;
})();

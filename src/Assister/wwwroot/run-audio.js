// Keep media outside the frequently rebuilt trace details so polling never interrupts playback.
class RunAudioPanel {
    constructor(host) {
        this.host = host;
        this.generation = 0;
        this.slots = new Map();
    }

    clear() {
        this.generation++;
        for (const slot of this.slots.values()) slot.audio?.pause();
        this.slots.clear();
        this.run = null;
        this.host.replaceChildren();
        this.host.hidden = true;
    }

    select(run) {
        if (this.run?.runId === run.runId) { this.run = run; return; }
        this.clear();
        this.run = run;
        this.host.hidden = false;
        const heading = document.createElement('h2'); heading.textContent = 'Listen to this run';
        this.notice = document.createElement('p'); this.notice.className = 'muted'; this.notice.setAttribute('role', 'status');
        const grid = document.createElement('div'); grid.className = 'run-audio-grid';
        for (const direction of ['input', 'output']) {
            const card = document.createElement('section'); card.className = 'run-audio-card';
            const title = document.createElement('h3'); title.textContent = direction === 'input' ? 'Input · microphone' : 'Output · generated speech';
            const status = document.createElement('p'); status.className = 'muted'; status.textContent = 'Checking recording…';
            const media = document.createElement('div');
            card.append(title, status, media); grid.append(card);
            this.slots.set(direction, { status, media });
        }
        this.host.append(heading, this.notice, grid);
        this.refresh();
    }

    async refresh() {
        if (!this.run || this.loading === this.generation) return;
        const generation = this.generation;
        const run = this.run;
        this.loading = generation;
        const base = run.source === 'rich-client' ? '/api/client' : '/api/diagnostics';
        try {
            const response = await fetch(`${base}/runs/${encodeURIComponent(run.runId)}/audio`, { cache: 'no-store' });
            if (this.generation !== generation) return;
            this.notice.replaceChildren();
            if (response.status === 401 || response.status === 403) {
                for (const direction of this.slots.keys()) this.render({ direction, state: 'sign-in' });
                const link = document.createElement('a'); link.href = '/chat.html'; link.target = '_blank'; link.rel = 'noopener';
                link.textContent = 'Sign in to Chat';
                this.notice.append(link, ' as this run’s owner, then refresh.');
                return;
            }
            if (response.status === 404) {
                for (const direction of this.slots.keys()) this.render({ direction, state: 'unavailable' });
                return;
            }
            if (!response.ok) throw new Error('Audio details unavailable. Refresh to retry.');
            const clips = await response.json();
            if (this.generation !== generation) return;
            for (const clip of clips) this.render(clip);
        } catch (error) {
            if (this.generation === generation) this.notice.textContent = error.message;
        } finally {
            if (this.loading === generation) this.loading = null;
        }
    }

    render(clip) {
        const slot = this.slots.get(clip.direction);
        if (!slot) return;
        if (clip.state === 'available' && clip.url) {
            slot.status.textContent = `${clip.partial ? 'Partial recording · ' : ''}${(clip.size / 1024).toFixed(1)} KiB${clip.expiresAt ? ` · Retained until ${new Date(clip.expiresAt).toLocaleString()}` : ''}`;
            if (slot.url === clip.url && slot.audio) return;
            slot.audio?.pause();
            const audio = document.createElement('audio'); audio.controls = true; audio.preload = 'none'; audio.src = clip.url;
            audio.setAttribute('aria-label', `${clip.direction === 'input' ? 'Input' : 'Output'} recording`);
            audio.onplay = () => { for (const other of this.slots.values()) if (other !== slot) other.audio?.pause(); };
            audio.onerror = () => { slot.status.textContent = 'Recording unavailable or expired. Refresh to check again.'; };
            slot.media.replaceChildren(audio); slot.audio = audio; slot.url = clip.url;
            return;
        }
        slot.audio?.pause(); slot.audio = null; slot.url = null; slot.media.replaceChildren();
        const labels = {
            expired: 'Recording expired or was removed by retention limits.',
            disabled: 'Audio capture is disabled.',
            'too-large': 'Recording exceeded the capture size limit.',
            'unsupported-format': 'Recording format could not be retained.',
            'sign-in': 'Sign in to access this owner’s recording.',
            unavailable: 'Recording unavailable for this account, or interaction expired.',
        };
        slot.status.textContent = labels[clip.state] || (this.run?.finishedAt
            ? 'No recording captured. Text-only runs do not produce audio.' : 'No recording captured yet.');
    }
}
window.RunAudioPanel = RunAudioPanel;

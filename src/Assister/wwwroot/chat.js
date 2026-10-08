'use strict';
(() => {
  const $ = id => document.getElementById(id);
  let conversation, socket, reconnect, generation = 0, active, pendingSubmission;
  let attachments = [], uploads = 0, executing = false;
  let recording, pendingAudio, registrationTimer;
  const views = new Map();
  let toneQueue = Promise.resolve(), toneEpoch = 0, toneAudio, finishTone;
  function stopTones() { toneEpoch++; toneAudio?.pause(); finishTone?.(); toneQueue = Promise.resolve(); }
  function playTone(data, id) {
    const epoch = toneEpoch;
    toneQueue = toneQueue.then(() => {
      if (epoch !== toneEpoch || id !== active || !$('speak').checked || Date.parse(data.expiresAt) < Date.now()) return;
      return new Promise(resolve => {
        const audio = new Audio(data.url); toneAudio = audio;
        const done = () => { clearTimeout(timeout); if (toneAudio === audio) { toneAudio = null; finishTone = null; } resolve(); };
        finishTone = done; const timeout = setTimeout(() => { audio.pause(); done(); }, 5000);
        audio.onended = done; audio.onerror = done; audio.play().catch(done);
      });
    }).catch(() => {});
    return toneQueue;
  }
  function error(e) { $('error').textContent = e.message || String(e); }
  async function api(path, method = 'GET', body) {
    const response = await fetch('/api/client' + path, { method, headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    if (response.status === 401) { signedOut(); throw new Error('Connect with a valid client credential.'); }
    if (!response.ok) { const data = await response.json().catch(() => ({})); throw new Error(data.message || `Request failed (${response.status})`); }
    const text = await response.text(); return text ? JSON.parse(text) : null;
  }
  function closeStream() { generation++; clearTimeout(reconnect); if (socket) { socket.onclose = null; socket.close(); socket = null; } }
  function signedOut() { stopTones(); closeStream(); clearInterval(registrationTimer); if (recording) stopMic(false).catch(error); $('client').hidden = true; $('login').hidden = false; $('connection').textContent = 'Disconnected'; }
  function busy(value) { executing = value; $('send').disabled = value || uploads > 0 || !!recording; $('cancel').hidden = !value; $('files').disabled = value; $('mic').disabled = value || uploads > 0; }
  async function register() {
    const capabilities = ['text.input', 'text.output', 'attachments.file', 'attachments.image'];
    if (window.AudioContext && window.AudioWorkletNode && navigator.mediaDevices?.getUserMedia) capabilities.push('audio.input');
    if (window.HTMLAudioElement) capabilities.push('audio.output');
    if (window.HTMLAudioElement) capabilities.push('audio.tones');
    await api('/clients/register', 'POST', { clientType: 'web', deviceName: 'Web conversation client', capabilities });
  }
  async function startMic() {
    let stream, context;
    const current = generation;
    $('mic').disabled = true;
    try {
      if (!navigator.mediaDevices?.getUserMedia || !window.AudioWorkletNode) throw new Error('Microphone recording needs a secure browser context with AudioWorklet support.');
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1 }, video: false });
      context = new AudioContext(); await context.audioWorklet.addModule('/pcm-capture.js'); await context.resume();
      if (current !== generation || $('client').hidden) throw new Error('Recording was cancelled.');
      const node = new AudioWorkletNode(context, 'pcm-capture'); const source = context.createMediaStreamSource(stream);
      const capture = { stream, context, node, source, chunks: [], size: 0, selected: conversation, stopping: false };
      node.port.onmessage = e => {
        if (e.data.pcm) { capture.chunks.push(new Uint8Array(e.data.pcm)); capture.size += e.data.pcm.byteLength; }
        if (e.data.done) capture.done?.();
        if (e.data.limit) stopMic(true).catch(error);
      };
      source.connect(node); node.connect(context.destination); recording = capture; pendingAudio = null; pendingSubmission = null;
      capture.timer = setTimeout(() => stopMic(true).catch(error), 30000);
      $('mic').textContent = 'Stop & send'; $('connection').textContent = 'Listening'; busy(false);
    } catch (e) { stream?.getTracks().forEach(track => track.stop()); if (context) await context.close(); error(e); busy(false); }
  }
  async function stopMic(send) {
    const capture = recording; if (!capture || capture.stopping) return; capture.stopping = true; clearTimeout(capture.timer);
    try { await Promise.race([new Promise(resolve => { capture.done = resolve; capture.node.port.postMessage('stop'); }), new Promise(resolve => setTimeout(resolve, 2000))]); }
    finally { capture.stream.getTracks().forEach(track => track.stop()); capture.source.disconnect(); capture.node.disconnect(); await capture.context.close(); recording = null; $('mic').textContent = 'Record voice'; busy(false); }
    if (!send || capture.selected !== conversation) return;
    uploads++; busy(false); $('connection').textContent = 'Uploading voice';
    try {
      const bytes = new Uint8Array(capture.size); let offset = 0; for (const chunk of capture.chunks) { bytes.set(chunk, offset); offset += chunk.length; }
      const response = await fetch('/api/client/attachments?name=Voice.pcm&source=microphone', { method: 'POST', headers: { 'Content-Type': 'audio/pcm' }, body: bytes });
      const result = await response.json(); if (!response.ok) throw new Error(result.message || 'Voice upload failed.');
      pendingAudio = result.id; pendingSubmission = null; $('message').value = 'Voice input';
    } catch (e) { error(e); }
    finally { uploads--; busy(false); }
    if (pendingAudio) $('composer').requestSubmit();
  }
  function renderAttachments() {
    $('attachments').replaceChildren(...attachments.map(item => {
      const li = document.createElement('li'); li.append(`${item.name} · ${item.processing === 'ready' ? 'ready' : 'stored; processing unavailable'} `);
      const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove';
      remove.onclick = () => { attachments = attachments.filter(a => a.id !== item.id); pendingSubmission = null; renderAttachments(); }; li.append(remove); return li;
    }));
  }
  async function upload(files, source) {
    if (executing) return;
    const selected = conversation;
    uploads++; busy(executing); $('connection').textContent = 'Uploading';
    try {
      for (const file of files) {
        if (attachments.length >= 8) throw new Error('Attach at most eight files.');
        const type = /\.md$/i.test(file.name) ? 'text/markdown' : /\.json$/i.test(file.name) ? 'application/json' : /\.txt$/i.test(file.name) ? 'text/plain' : file.type || 'application/octet-stream';
        const response = await fetch(`/api/client/attachments?name=${encodeURIComponent(file.name || 'Pasted-image.png')}&source=${source}`, { method: 'POST', headers: { 'Content-Type': type }, body: file });
        if (!response.ok) { const data = await response.json().catch(() => ({})); throw new Error(data.message || `Upload failed (${response.status})`); }
        const item = await response.json();
        if (selected === conversation) { attachments.push(item); pendingSubmission = null; renderAttachments(); }
      }
    } catch (e) { error(e); }
    finally { uploads--; busy(executing); $('connection').textContent = 'Connected'; $('files').value = ''; }
  }
  function view(item) {
    const row = document.createElement('article'); row.className = 'message';
    const user = document.createElement('strong'); user.textContent = 'YOU';
    const input = document.createElement('div'); input.textContent = item.input;
    const inputText = document.createElement('span'); inputText.textContent = item.input; input.replaceChildren(inputText);
    const label = document.createElement('strong'); label.textContent = 'ASSISTER'; label.style.marginTop = '18px';
    const answer = document.createElement('div'); answer.className = 'answer';
    const status = document.createElement('p'); status.className = 'status'; status.textContent = item.status;
    const mediaStatus = document.createElement('p'); mediaStatus.className = 'status';
    const context = document.createElement('details'); const contextSummary = document.createElement('summary'); contextSummary.textContent = 'Context used';
    const contextBody = document.createElement('div'); context.append(contextSummary, contextBody);
    const contextItems = new Map();
    let contextRefresh;
    async function showContext() {
      try {
        const items = await api(`/interactions/${item.id}/context`);
        contextBody.replaceChildren(...items.map(c => { const p = document.createElement('pre'); p.textContent = `${c.name} · ${c.type}\nModel rounds: ${c.modelRounds.join(', ')}${c.truncated ? ' · inspection truncated' : ''}\n${c.content}\nProvenance: ${JSON.stringify(c.provenance)}`; return p; }));
        if (!items.length) contextBody.textContent = 'No context records selected yet.';
      } catch (e) { contextBody.textContent = e.message; }
    }
    context.ontoggle = () => { if (context.open) showContext(); };
    const details = document.createElement('details'); const summary = document.createElement('summary'); summary.textContent = 'Execution trace';
    const trace = document.createElement('pre'); details.append(summary, trace);
    details.ontoggle = async () => { if (details.open) { try { trace.textContent = JSON.stringify(await api(`/interactions/${item.id}/trace`), null, 2); } catch (e) { trace.textContent = e.message; } } };
    row.append(user, input, label, answer, status, mediaStatus, context, details); $('messages').append(row);
    api(`/interactions/${item.id}/attachments`).then(items => { for (const a of items) { const link = document.createElement('a'); link.href = `/api/client/attachments/${a.id}`; link.textContent = ` ${a.name}`; link.download = a.name; input.append(link); } }).catch(error);
    const scheduleContext = () => { clearTimeout(contextRefresh); contextRefresh = setTimeout(showContext, 100); };
    const result = { answer, inputText, row, status, mediaStatus, context, contextSummary, contextItems, scheduleContext, cursor: 0, steps: new Map(), terminal: false }; views.set(item.id, result); return result;
  }
  function apply(item, event, replaying = false) {
    const v = views.get(item.id);
    if (!event.sequence || event.sequence <= v.cursor) return;
    if (event.sequence !== v.cursor + 1) throw new Error('Event gap. Reconnect to recover.');
    v.cursor = event.sequence;
    const data = event.data;
    if (event.type === 'tone.play' && !replaying && item.id === active && $('speak').checked && Date.parse(data.expiresAt) >= Date.now()) {
      if (data.placement === 'after-response-audio') {
        if (v.audio?.ended) playTone({ ...data, expiresAt: new Date(Date.now() + 5000).toISOString() }, item.id);
        else v.goodbye = data;
      } else playTone(data, item.id);
    }
    if (event.type === 'stt.partial' || event.type === 'stt.final') { v.inputText.textContent = data.text; v.status.textContent = event.type === 'stt.final' ? 'Transcribed' : 'Transcribing'; }
    if (event.type === 'stt.started') v.status.textContent = 'Transcribing';
    if (event.type === 'response.started') v.status.textContent = 'Responding';
    if (event.type === 'tts.started') v.mediaStatus.textContent = 'Generating speech';
    if (event.type === 'tts.failed') v.mediaStatus.textContent = data.message;
    if (event.type === 'tts.audio' && !v.audio) {
      const audio = document.createElement('audio'); audio.controls = true; audio.src = data.url; v.row.append(audio); v.audio = audio;
      let playbackId;
      const report = state => api(`/interactions/${item.id}/playback`, 'POST', { state, playbackId }).catch(error);
      v.mediaStatus.textContent = 'Audio ready';
      audio.onplaying = () => { if (!playbackId) { playbackId = crypto.randomUUID(); report('started'); } v.mediaStatus.textContent = 'Speaking'; };
      audio.onended = () => { if (playbackId) report('completed'); playbackId = null; v.mediaStatus.textContent = 'Playback complete';
        if (v.goodbye) { const cue = v.goodbye; v.goodbye = null; playTone({ ...cue, expiresAt: new Date(Date.now() + 5000).toISOString() }, item.id); } };
      audio.onpause = () => { if (!audio.ended && playbackId) { v.goodbye = null; report('stopped'); playbackId = null; v.mediaStatus.textContent = 'Playback stopped'; } };
      audio.onerror = () => { playbackId ||= crypto.randomUUID(); report('failed'); playbackId = null; v.mediaStatus.textContent = 'Playback failed; text remains available'; };
      if (!replaying && item.id === active && $('speak').checked) {
        const epoch = toneEpoch;
        toneQueue.then(() => { if (epoch === toneEpoch && item.id === active && $('speak').checked) return audio.play(); })
          .catch(() => { v.mediaStatus.textContent = 'Audio ready — press play'; });
      }
    }
    if (event.type === 'context.added' || event.type === 'context.updated') {
      v.contextItems.set(data.id, data); v.contextSummary.textContent = `Context used (${v.contextItems.size})`;
      if (v.context.open) v.scheduleContext();
    }
    if (event.type === 'response.delta') v.answer.textContent += data.text;
    if (event.type === 'response.completed') v.answer.textContent = data.text;
    if (event.type === 'step.started') {
      const li = document.createElement('li'); li.textContent = data.label; li.dataset.status = 'running';
      if (data.parentStepId) li.style.marginLeft = '12px';
      v.steps.set(data.stepId, li); if (item.id === active) $('activity').append(li);
    }
    if (event.type === 'step.completed' || event.type === 'step.failed') {
      const li = v.steps.get(data.stepId);
      if (li) { li.dataset.status = event.type === 'step.failed' ? 'failed' : 'complete'; li.textContent = `${data.label} · ${data.status} (${Math.round(data.durationMs)} ms)`; }
    }
    if (event.type.startsWith('interaction.')) v.status.textContent = event.type.slice(12).replaceAll('_', ' ');
    if (['interaction.completed', 'interaction.failed', 'interaction.cancelled'].includes(event.type)) {
      if (event.type === 'interaction.cancelled') { if (item.id === active) stopTones(); v.goodbye = null; v.audio?.pause(); }
      v.terminal = true;
      if (data.message) v.status.textContent += ` · ${data.message}`;
      if (item.id === active) { busy(false); $('connection').textContent = 'Connected'; }
    }
  }
  async function replay(item, live = false) {
    const v = views.get(item.id);
    while (true) { const events = await api(`/interactions/${item.id}/events?afterSequence=${v.cursor}`); events.forEach(e => apply(item, e, !live)); if (events.length < 256) break; }
  }
  function connect(item) {
    closeStream(); const current = generation; const v = views.get(item.id);
    if (v.terminal) return;
    $('connection').textContent = 'Connecting';
    socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/client/interactions/${item.id}/stream?afterSequence=${v.cursor}`);
    socket.onopen = () => { $('connection').textContent = 'Live'; };
    socket.onmessage = message => {
      try {
        const event = JSON.parse(message.data);
        if (event.type === 'subscription.ready' && event.protocolVersion !== 1) { closeStream(); throw new Error('Unsupported server protocol version.'); }
        apply(item, event);
      } catch (e) { error(e); socket?.close(); }
    };
    socket.onclose = () => {
      if (current !== generation) return;
      if (v.terminal) {
        const selected = conversation;
        api(`/conversations/${selected}`).then(items => {
          if (current === generation && items.some(i => ['created', 'transcribing', 'running', 'responding'].includes(i.status))) return open(selected);
        }).catch(error);
        return;
      }
      $('connection').textContent = 'Reconnecting';
      reconnect = setTimeout(async () => { try { await api('/protocol'); if (current === generation) connect(item); } catch (e) { error(e); } }, 1000);
    };
  }
  async function open(id) {
    stopTones();
    if (recording) await stopMic(false);
    for (const v of views.values()) v.audio?.pause();
    closeStream(); conversation = id; active = null; pendingAudio = null; pendingSubmission = null; attachments = []; renderAttachments(); busy(false); views.clear(); $('messages').replaceChildren(); $('activity').replaceChildren();
    localStorage.setItem('assister-conversation', id);
    const items = await api(`/conversations/${id}`);
    for (const item of items) { view(item); }
    const running = items.filter(i => ['created', 'transcribing', 'running', 'responding'].includes(i.status));
    if (running.length) active = running[0].id;
    else if (items.length) active = items[items.length - 1].id;
    for (const item of items) await replay(item);
    if (running.length) { busy(true); connect(running[0]); }
  }
  async function load() {
    await api('/protocol'); $('login').hidden = true; $('client').hidden = false; $('connection').textContent = 'Connected'; $('error').textContent = '';
    await register(); clearInterval(registrationTimer); registrationTimer = setInterval(() => register().catch(error), 30000);
    let items = await api('/conversations');
    if (!items.length) { await api('/conversations', 'POST'); items = await api('/conversations'); }
    $('conversations').replaceChildren(...items.map(i => { const o = document.createElement('option'); o.value = i.id; o.textContent = i.title; return o; }));
    const saved = localStorage.getItem('assister-conversation'); const id = items.some(i => i.id === saved) ? saved : items[0].id;
    $('conversations').value = id; await open(id);
  }
  $('login').onsubmit = async e => { e.preventDefault(); const token = $('credential').value; $('credential').value = ''; try { await api('/session', 'POST', { token }); await load(); } catch (e) { error(e); } };
  $('logout').onclick = async () => { try { await api('/clients/current', 'DELETE'); await api('/session', 'DELETE'); signedOut(); } catch (e) { error(e); } };
  $('new').onclick = async () => { try { const item = await api('/conversations', 'POST'); localStorage.setItem('assister-conversation', item.id); await load(); } catch (e) { error(e); } };
  $('conversations').onchange = async e => { try { await open(e.target.value); } catch (e) { error(e); } };
  $('composer').onsubmit = async e => {
    e.preventDefault(); const message = $('message').value.trim(); if (!message) return;
    busy(true); $('error').textContent = '';
    stopTones();
    pendingSubmission = pendingSubmission?.message === message ? pendingSubmission : { message, idempotencyKey: crypto.randomUUID(), attachmentIds: attachments.map(a => a.id), audioAttachmentId: pendingAudio, speak: $('speak').checked };
    try {
      const item = await api(`/conversations/${conversation}/interactions`, 'POST', pendingSubmission);
      pendingSubmission = null; pendingAudio = null; attachments = []; renderAttachments(); $('message').value = ''; $('activity').replaceChildren(); active = item.id;
      if (!views.has(item.id)) view(item);
      await replay(item, true); connect(item);
    } catch (e) { busy(false); error(e); }
  };
  $('cancel').onclick = async () => { try { stopTones(); const v = views.get(active); if (v) v.goodbye = null; v?.audio?.pause(); await api(`/interactions/${active}/cancel`, 'POST'); $('connection').textContent = 'Cancelling'; } catch (e) { error(e); } };
  $('files').onchange = e => upload(Array.from(e.target.files), 'file_picker');
  $('mic').onclick = () => (recording ? stopMic(true) : startMic()).catch(error);
  $('speak').onchange = () => { pendingSubmission = null; if (!$('speak').checked) { stopTones(); for (const v of views.values()) v.goodbye = null; } };
  $('composer').ondragover = e => { e.preventDefault(); };
  $('composer').ondrop = e => { e.preventDefault(); upload(Array.from(e.dataTransfer.files), 'drop'); };
  $('message').onpaste = e => { const files = Array.from(e.clipboardData.files); if (files.length) { e.preventDefault(); upload(files, 'clipboard'); } };
  window.addEventListener('online', () => { if (active && !views.get(active)?.terminal) connect({ id: active }); });
  load().catch(error);
})();

const byId = id => document.getElementById(id);
const element = (tag, text) => { const e = document.createElement(tag); if (text != null) e.textContent = text; return e; };
const ownership = ['Unknown', 'Available', 'Owned by Assister', 'Conflict', 'Unsupported'];
const activities = ['Idle', 'Wake detected', 'Capturing audio', 'Speech complete', 'Transcribing', 'Routing', 'Generating response', 'Synthesizing', 'Waiting for playback', 'Playing response', 'Complete', 'Cancelled', 'Disconnected', 'STT failed', 'Routing failed', 'TTS failed', 'Playback failed'];
let selectedId = new URLSearchParams(location.search).get('id'), selectedTab = 'Status', devices = [], updating = false;
async function api(path, options) {
    const response = await fetch(path, options);
    if (!response.ok) { const body = await response.json().catch(() => ({})); throw Error(body.error || `Request failed (${response.status})`); }
    return response.status === 204 ? null : response.json().catch(() => null);
}
function facts(values) {
    const dl = element('dl'); dl.className = 'step-facts';
    for (const [name, value] of Object.entries(values)) dl.append(element('dt', name), element('dd', value == null || value === '' ? 'Unknown' : String(value)));
    return dl;
}
function action(label, handler, supported) {
    const button = element('button', label); button.disabled = !supported;
    button.onclick = async () => { updating = true; button.disabled = true; try { await handler(); byId('notice').textContent = 'Command submitted; device state will refresh.'; } catch (error) { byId('notice').textContent = error.message; } finally { updating = false; await refresh(); } };
    return button;
}
async function renderDetail() {
    const entry = devices.find(d => d.device.id === selectedId); byId('satellite-detail').hidden = !entry; if (!entry) return;
    const { device: d, runtime: r } = entry, panel = byId('device-panel'), path = `/api/satellites/${encodeURIComponent(d.id)}`;
    byId('device-title').textContent = d.name; panel.replaceChildren(); byId('device-tabs').replaceChildren();
    for (const title of ['Status', 'Voice', 'Playback', 'Diagnostics', 'Configuration']) {
        const tab = element('button', title); tab.setAttribute('aria-pressed', String(selectedTab === title));
        tab.onclick = () => { selectedTab = title; renderDetail().catch(error => byId('notice').textContent = error.message); }; byId('device-tabs').append(tab);
    }
    if (r.lastError || r.configurationDrift) { const alert = element('p', r.lastError || r.configurationDrift); alert.className = 'alert'; panel.append(alert); }
    const supported = r.connectionState === 'Online', config = r.voiceConfiguration;
    if (selectedTab === 'Status') {
        panel.append(facts({Connection:r.connectionState, Endpoint:d.endpoint, Provider:d.providerType, Device:r.deviceName, Model:r.model, Firmware:r.firmwareVersion, ESPHome:r.espHomeVersion, API:r.apiVersion, 'Voice ownership':ownership[r.voiceOwnership], 'Last seen':r.lastSeen, Activity:activities[r.activity]}));
        panel.append(element('h3', 'Observed capabilities'), element('p', Object.entries(r.capabilities).filter(([, v]) => v).map(([k]) => k.replace(/([a-z])([A-Z])/g, '$1 $2')).join(' · ') || 'Not yet observed'));
        if (r.voiceOwnership === 3) panel.append(action('Retry after releasing the other subscriber', () => api(path + '/retry-ownership', {method:'POST'}), supported));
    } else if (selectedTab === 'Voice') {
        panel.append(facts({'Active wake words': config.activeWakeWords.join(', '), 'Last detected wake word':r.wakeWord, 'Current session':r.currentVoiceSessionId, Conversation:r.conversationId, Microphone:r.activity === 2 ? 'Capturing audio' : 'Inactive', Stage:activities[r.activity]}));
        panel.append(element('h3', 'Available on device'));
        for (const word of config.availableWakeWords) panel.append(element('p', `${word.name} (${word.id})`));
    } else if (selectedTab === 'Playback') {
        panel.append(facts({State:r.currentPlaybackState, Volume:r.currentVolume == null ? null : `${Math.round(r.currentVolume * 100)}%`, Muted:r.muteState}));
        const message = element('textarea'); message.value = 'This is an Assister test announcement.'; message.maxLength = 500; message.setAttribute('aria-label', 'Test announcement message'); panel.append(message);
        panel.append(action('Play test announcement', () => api(path + '/announcement', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({message:message.value})}), supported && r.capabilities.announcementPlayback && !r.currentVoiceSessionId));
        panel.append(action('Stop announcement', () => api(path + '/stop', {method:'POST'}), supported && r.capabilities.announcementPlayback));
        const volume = element('input'); volume.type = 'range'; volume.min = '0'; volume.max = '100'; volume.value = Math.round((r.currentVolume ?? 0.6) * 100); volume.setAttribute('aria-label', 'Volume percent'); volume.disabled = !supported || !r.capabilities.volumeControl; panel.append(volume);
        panel.append(action('Set volume', () => api(path + '/volume', {method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({volume:Number(volume.value)/100})}), supported && r.capabilities.volumeControl));
    } else if (selectedTab === 'Configuration') {
        panel.append(element('h3', 'Runtime · active wake words'), element('p', `Maximum active models: ${config.maxActiveWakeWords}. Only models reported by the device can be selected.`));
        if (!r.capabilities.wakeWordConfiguration) panel.append(element('p', 'Runtime wake-word changes are unavailable through this device/provider. Physical wake detection may still work. Firmware changes are outside this page.'));
        const choices = [];
        for (const word of config.availableWakeWords) { const label = element('label'), checkbox = element('input'); checkbox.type = 'checkbox'; checkbox.checked = config.activeWakeWords.includes(word.id); checkbox.disabled = !supported || r.voiceOwnership !== 2; label.append(checkbox, document.createTextNode(word.name)); panel.append(label); choices.push({checkbox, id:word.id}); }
        panel.append(action('Apply wake words', () => { const words = choices.filter(c => c.checkbox.checked).map(c => c.id); if (words.length > config.maxActiveWakeWords) throw Error('Too many active wake words.'); return api(path + '/wake-words', {method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify(words)}); }, supported && r.capabilities.wakeWordConfiguration && r.voiceOwnership === 2));
        panel.append(element('h3', 'Device / firmware · read only'), facts({ESPHome:r.espHomeVersion, API:r.apiVersion, Firmware:r.firmwareVersion, 'Compiled models':config.availableWakeWords.map(w => w.name).join(', '), 'Multi-channel microphone':r.capabilities.multiChannelMicrophone, 'Desired configuration':d.configuration}));
    } else {
        const [events, traces] = await Promise.all([api(path + '/events'), api(path + '/traces')]);
        if (selectedId !== d.id || selectedTab !== 'Diagnostics') return;
        panel.append(element('h3', 'Recent requests'));
        for (const trace of traces.slice(0, 20)) { const link = element('a', `${trace.userText || trace.runId} · ${trace.outcome} · ${Math.round(trace.durationMilliseconds)} ms`); link.href = `/?run=${encodeURIComponent(trace.runId)}`; const line = element('p'); line.append(link); panel.append(line); }
        panel.append(element('h3', 'Bounded satellite events'));
        panel.append(action('Read device warnings for 60 seconds', () => api(path + '/logs', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({enabled:true})}), supported));
        panel.append(action('Stop device logs', () => api(path + '/logs', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({enabled:false})}), supported));
        for (const event of events.slice().reverse()) {
            const line = element('p', `${new Date(event.at).toLocaleString()} · ${event.type}${event.detail ? ' · ' + event.detail : ''}`);
            if (event.traceId) { const link = element('a', ' · Request trace'); link.href = `/?run=${encodeURIComponent(event.traceId)}`; line.append(link); }
            panel.append(line);
        }
    }
}
async function refresh() {
    if (updating) return;
    try {
        devices = await api('/api/satellites'); byId('satellites').replaceChildren();
        for (const {device:d, runtime:r} of devices) {
            const row = element('tr'), cell = element('td'), button = element('button', d.name);
            button.onclick = () => { selectedId = d.id; history.replaceState(null, '', `?id=${encodeURIComponent(d.id)}`); renderDetail().catch(error => byId('notice').textContent = error.message); };
            cell.append(button, element('small', r.model || r.deviceName || d.id)); row.append(cell);
            for (const text of [d.areaId || 'Unassigned',d.providerType,r.connectionState,ownership[r.voiceOwnership],r.voiceConfiguration.activeWakeWords.join(', ') || r.wakeWord || 'Unknown',activities[r.activity],r.lastSeen ? new Date(r.lastSeen).toLocaleString() : 'Never']) row.append(element('td',text));
            byId('satellites').append(row);
        }
        if (!devices.length) byId('notice').textContent = 'No satellites configured.';
        // Do not overwrite edits while a user is selecting runtime configuration or composing audio.
        if (!['Configuration','Playback'].includes(selectedTab) || !byId('device-panel').hasChildNodes()) await renderDetail();
    } catch (error) { byId('notice').textContent = error.message; }
}
byId('refresh').onclick = () => { byId('device-panel').replaceChildren(); refresh(); };
refresh(); setInterval(refresh, 5000);

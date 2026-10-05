const $ = id => document.getElementById(id);
let rebuildingCache = false, cacheRebuildMessage = '';
let runs = [], selected = null, currentRun = null, busy = false, quick = 'All', sending = false, detailSignature = '';
selected = new URLSearchParams(location.search).get('run');
const openRounds = new Set();
function node(tag, text, cls) { const e = document.createElement(tag); if (text != null) e.textContent = text; if (cls) e.className = cls; return e; }
function pretty(value) { return JSON.stringify(value, null, 2); }
function time(n) { return n >= 1000 ? `${(n / 1000).toFixed(2)} s` : `${Math.max(0, n || 0).toFixed(1)} ms`; }
function label(value) { return value.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('-', ' '); }
function badge(status) { return node('span', status === 'unmatched' ? 'unknown intent' : label(status || 'unknown'), 'badge' + (['succeeded','Healthy','Connected','matched','resolved'].includes(status) ? ' good' : ['failed','unavailable','rejected','stt-failed','playback-failed','interrupted-or-failed','stage-failure'].includes(status) ? ' bad' : '')); }
function copyButton(value, description) {
    const b = node('button', 'Copy', 'copy'); b.type = 'button'; b.setAttribute('aria-label', `Copy ${description}`);
    b.onclick = async () => { try { const text = typeof value === 'string' ? value : pretty(value); if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text); else { const area = node('textarea'); area.value = text; area.style.position = 'fixed'; area.style.opacity = '0'; document.body.append(area); area.select(); const ok = document.execCommand('copy'); area.remove(); if (!ok) throw Error(); } b.textContent = 'Copied'; setTimeout(() => b.textContent = 'Copy', 1200); } catch { b.textContent = 'Copy failed'; } }; return b;
}
function jsonBlock(title, value, collapsed = false, truncated = false) {
    const block = node(collapsed ? 'details' : 'div'); const heading = node(collapsed ? 'summary' : 'div', title + (truncated ? ' · truncated' : ''), 'payload-label');
    heading.append(copyButton(value, title)); block.append(heading, node('pre', pretty(value))); return block;
}
function facts(data) { const dl = node('dl', null, 'step-facts'); for (const [key, value] of Object.entries(data || {})) { if (value == null) continue; dl.append(node('dt', label(key)), node('dd', typeof value === 'object' ? pretty(value) : String(value))); } return dl; }
function selectPanel(chat) { $('chat-panel').hidden = !chat; $('explorer-panel').hidden = chat; $('chat-tab').setAttribute('aria-pressed', String(chat)); $('explorer-tab').setAttribute('aria-pressed', String(!chat)); }
function filteredRuns() {
    const query = $('filter').value.toLowerCase();
    return runs.filter(r => (!query || JSON.stringify(r).toLowerCase().includes(query)) && (!$('source').value || r.source === $('source').value)
        && (!$('outcome').value || r.outcome === $('outcome').value) && (!$('handled').value || r.handledBy === $('handled').value)
        && (! $('satellite-filter').value || (r.satelliteId || '').toLowerCase().includes($('satellite-filter').value.toLowerCase()))
        && (! $('conversation-filter').value || (r.conversationId || '').includes($('conversation-filter').value.toLowerCase()))
        && (! $('run-filter').value || r.runId.includes($('run-filter').value.toLowerCase()))
        && (quick === 'All' || quick === 'Failures' && (r.hasFailures || !['succeeded','running','no-speech'].includes(r.outcome))
        || quick === 'LLM' && r.handledBy === 'language-model' || quick === 'Direct' && r.handledBy === 'direct-intent'
        || quick === 'Voice' && r.source === 'voice' || quick === 'Debug' && r.source === 'debug'));
}
function renderList() {
    $('runs').replaceChildren(); const filtered = filteredRuns(); $('run-count').textContent = `${filtered.length} of ${runs.length} interactions`;
    for (const r of filtered) { const b = node('button', null, 'run' + (r.runId === selected ? ' selected' : '')); b.append(node('strong', r.userText || 'Waiting for transcript…'), node('small', `${r.source.toUpperCase()} · ${new Date(r.startedAt).toLocaleString()} · ${time(r.durationMilliseconds)}`), badge(r.outcome)); if(r.hasFailures)b.append(badge(r.outcome==='cancelled'?'stage-interrupted':'stage-failure')); b.onclick = () => inspect(r.runId); $('runs').append(b); }
    if (!filtered.length) $('runs').append(node('p', runs.length ? 'No matching interactions.' : 'No runs yet. Use Debug Chat to begin.', 'muted'));
}
function stepTitle(s) { const title = node('div', null, 'stage-title'); title.append(node('strong', s.name), badge(s.status), node('span', time(s.durationMilliseconds), 'duration')); return title; }
function renderStep(s, all) {
    const isLlm = s.kind === 'LanguageModel', isTool = s.kind === 'ToolCall';
    const e = node(isLlm ? 'details' : 'article', null, 'stage' + (isLlm ? ' llm' : isTool ? ' tool' : ['IntentClassification','EntityResolution','ToolSelection'].includes(s.kind) ? ' routing' : ''));
    if (isLlm) { const summary = node('summary'); summary.append(stepTitle(s)); e.append(summary); e.open = openRounds.has(s.id); e.ontoggle = () => e.open ? openRounds.add(s.id) : openRounds.delete(s.id); } else e.append(stepTitle(s));
    const body = node('div'); body.append(node('p', s.summary));
    if (s.kind === 'SpeechToText') { body.append(node('p', s.output?.transcript || '(No transcript)', 'transcript'), facts(s.metadata)); }
    else if (s.kind === 'IntentClassification') { body.append(facts({ originalInput: s.input?.originalInput, normalizedInput: s.input?.normalizedInput })); const slots = node('div', null, 'slots'); for (const [k,v] of Object.entries(s.output || {})) if (v != null && k !== 'reason') slots.append(node('span', `${label(k)}: ${v}`)); body.append(slots, node('p', s.output?.reason)); }
    else if (s.kind === 'EntityResolution') { body.append(facts({...s.input,...s.metadata})); for (const [name, entities] of [['Selected entities',s.output?.selected],['Alternatives',s.output?.alternatives]]) { if (!entities?.length) continue; body.append(node('h3',name)); for(const entity of entities){const row=node('div',entity.entityId,'entity');row.append(node('span',`${entity.name} · ${entity.areaName || 'No area'}`));body.append(row);} } }
    else if (s.kind === 'ToolSelection') { body.append(facts({selectedTools:s.metadata?.selectedTools,capabilities:s.metadata?.capabilities})); for(const reason of s.metadata?.reasons || []) body.append(node('p',reason)); }
    else if (isLlm) { body.append(facts(s.metadata)); body.append(jsonBlock('Prompt / messages',s.input,true,s.inputTruncated)); body.append(node('h3','Assistant content'), node('p',s.output?.assistantContent || '(empty)','response')); body.append(facts({finishReason:s.output?.finishReason})); if(s.output?.toolCalls?.length) body.append(jsonBlock('Proposed tool calls',s.output.toolCalls)); body.append(jsonBlock('Model output',s.output,false,s.outputTruncated)); }
    else if (isTool) { body.append(facts(s.metadata),jsonBlock('Tool arguments',s.input,false,s.inputTruncated),jsonBlock('Tool result',s.output,false,s.outputTruncated)); }
    else { if(s.input) body.append(jsonBlock('Input',s.input,s.kind==='Input',s.inputTruncated)); if(s.output) body.append(jsonBlock('Output',s.output,false,s.outputTruncated)); if(s.metadata) body.append(facts(s.metadata)); }
    const children = all.filter(child=>child.parentId===s.id); if(children.length){const nest=node('div',null,'children');for(const child of children)nest.append(renderStep(child,all));body.append(nest);}
    e.append(body); return e;
}
function renderDetail(r) {
    const signature = JSON.stringify(r); if(signature === detailSignature) return; detailSignature = signature;
    const d=$('detail');d.replaceChildren();const card=node('div',null,'summary-card'),top=node('div',null,'summary-top');top.append(node('span',`${r.source.toUpperCase()} / ${new Date(r.startedAt).toLocaleString()}`,'mono'),badge(r.outcome));if(r.hasFailures)top.append(badge(r.outcome==='cancelled'?'stage-interrupted':'stage-failure'));card.append(top,node('h2',r.userText || 'Waiting for transcript…'));
    const inputCopy=node('div',null,'mono');inputCopy.append(copyButton(r.userText || '','user input'));card.append(inputCopy);
    const responseGrid=node('div',null,'response-grid');for(const [name,value,cls]of [['Raw response',r.rawResponse,'response'],['Spoken / TTS-ready response',r.spokenResponse,'response spoken']]){const section=node('div');section.append(node('span',name,'mono'),node('div',value ?? '(Not produced yet)',cls));responseGrid.append(section);}card.append(responseGrid);
    const info=node('div',null,'facts');for(const [name,value]of [['RunId',r.runId],['Handled by',r.handledBy],['Satellite',r.satelliteId],['Area',r.area],['Conversation ID',r.conversationId],['Voice session ID',r.voiceSessionId],['Total duration',time(r.durationMilliseconds)],['Infrastructure trace',r.activityTraceId]]){const f=node('div',null,'fact');f.append(node('span',name),node('strong',value || '—'));if(name==='RunId')f.append(copyButton(r.runId,'RunId'));info.append(f);}card.append(info);d.append(card);
    const timings=node('section',null,'timings');
    const timingHeading=node('h3','Where the time went');
    timingHeading.append(node('span',`Total ${time(r.durationMilliseconds)}`,'timing-total'));
    timings.append(timingHeading,node('p','Audio capture and STT finalization are shown separately. Nested timings can overlap.','muted'));
    const audio=r.steps.find(s=>s.name==='Microphone audio'); const stt=r.steps.find(s=>s.kind==='SpeechToText');
    const lanes=[];
    if(audio){
        const ended=Date.parse(audio.output?.audioInputCompletedAt ?? stt?.metadata?.audioInputCompletedAt);
        const playback=r.steps.find(s=>s.name==='Satellite playback');
        const signaled=Date.parse(playback?.metadata?.playbackStartedAt);
        const estimated=!Number.isFinite(signaled);
        const began=estimated?Date.parse(playback?.metadata?.audioReadyAt ?? playback?.startedAt):signaled;
        if(Number.isFinite(ended)&&Number.isFinite(began)&&began>=ended)lanes.push({name:'Request to Response',duration:began-ended,offset:ended-Date.parse(r.startedAt),cls:'request-response',estimated,
            title:estimated?'Estimated from audio input completion to satellite delivery start; actual playback start was not recorded.':'From audio input completion to the satellite’s playback-start signal.'});
    }
    if(audio)lanes.push({name:'Audio (PCM)',duration:audio.output?.audioDurationMilliseconds || 0,offset:0,cls:''});
    if(stt&&stt.metadata?.postAudioLatencyMilliseconds!=null)lanes.push({name:'STT finalize',duration:stt.metadata.postAudioLatencyMilliseconds,offset:Date.parse(stt.metadata.audioInputCompletedAt)-Date.parse(r.startedAt),cls:'stt'});
    // Streaming spans include waiting for model output and playback, rather than isolated work.
    // Keep their trace details below, but omit these enclosing spans from the timing chart.
    for(const s of r.steps){
        if(s.kind==='Playback'&&s.name==='Streaming delivery'||s.kind==='TextToSpeech'&&s.name==='Streaming speech synthesis')continue;
        if(['IntentClassification','EntityResolution','ToolSelection','LanguageModel','ToolCall','TextToSpeech'].includes(s.kind)||s.kind==='Playback'&&s.name!=='Playback delivery')lanes.push({name:s.name,duration:s.durationMilliseconds,offset:Date.parse(s.startedAt)-Date.parse(r.startedAt),cls:s.kind==='ToolCall'?'tools':s.kind==='LanguageModel'?'stt':''});
    }
    for(const lane of lanes){
        const row=node('div',null,'timing-row '+lane.cls),track=node('div',null,'track'),bar=node('i');
        const percentage=100*lane.duration/Math.max(1,r.durationMilliseconds);
        const share=percentage<1?'<1%':`${Math.round(percentage)}%`;
        if(lane.title)row.title=lane.title;
        bar.style.marginLeft=`${Math.max(0,Math.min(99,100*lane.offset/Math.max(1,r.durationMilliseconds)))}%`;
        bar.style.width=`${Math.max(.2,Math.min(100,percentage))}%`;
        track.append(bar);
        row.append(node('span',lane.name),track,node('span',`${lane.estimated?'≈ ':''}${time(lane.duration)} (${share})`));
        timings.append(row);
    }d.append(timings);
    const groups=[['Input',['Input','SpeechToText']],['Routing',['IntentClassification','EntityResolution','ToolSelection','IntentExecution']],['LLM / Tools',['LanguageModel','ToolCall']],['Output',['ResponseFormatting','TextToSpeech','Playback','Error']]];
    let sectionNumber=0;for(const [name,kinds]of groups){const entries=r.steps.filter(s=>kinds.includes(s.kind));if(!entries.length)continue;
        // A cross-section parent is rendered at its own location with children, avoiding duplicate stages.
        const visible=entries.filter(s=>!s.parentId||!r.steps.some(parent=>parent.id===s.parentId));if(!visible.length)continue;
        const section=node('section',null,'pipeline-section'),heading=node('div',null,'section-heading');heading.append(node('span',String(++sectionNumber).padStart(2,'0'),'ordinal'),node('h2',name));section.append(heading);for(const s of visible)section.append(renderStep(s,r.steps));d.append(section);
    }
}
async function loadSelected(){if(!selected)return;const id=selected;try{const response=await fetch(`/api/diagnostics/runs/${encodeURIComponent(id)}`,{cache:'no-store'});if(response.status===404){if(selected===id){currentRun=null;detailSignature='';$('detail').replaceChildren(node('div','This run is no longer retained.','empty'));}return;}if(!response.ok)throw Error();const r=await response.json();if(selected===id){currentRun=r;renderDetail(r);}}catch{$('connection').textContent='Run details unavailable · retrying';}}
async function inspect(id){selected=id;detailSignature='';selectPanel(false);renderList();await loadSelected();}
async function rebuildHomeAssistantCache(){
    if(rebuildingCache)return;
    rebuildingCache=true;cacheRebuildMessage='Reloading Home Assistant cache…';
    const button=document.querySelector('#health button');
    if(button){button.disabled=true;button.textContent='Rebuilding…';}
    try{
        const response=await fetch('/api/homeassistant/cache/rebuild',{method:'POST'});
        if(!response.ok)throw Error();
        const result=await response.json();
        cacheRebuildMessage=`Cache rebuilt · ${result.entityCount} entities`;
    }catch{cacheRebuildMessage='Cache rebuild failed. Check Home Assistant connectivity and retry.';}
    finally{rebuildingCache=false;await refresh();}
}
function addCacheRebuildControl(){for(const card of $('health').children){if(card.querySelector('strong')?.textContent!=='Home Assistant')continue;const button=node('button',rebuildingCache?'Rebuilding…':'Rebuild cache');button.disabled=rebuildingCache;button.onclick=rebuildHomeAssistantCache;card.append(button);if(cacheRebuildMessage)card.append(node('p',cacheRebuildMessage));}}
async function refresh(){if(busy)return;busy=true;try{const responses=await Promise.all([fetch('/api/diagnostics/runs',{cache:'no-store'}),fetch('/api/diagnostics/health',{cache:'no-store'})]);if(responses.some(r=>!r.ok))throw Error();const [history,health]=await Promise.all(responses.map(r=>r.json()));runs=history;if(!selected&&runs.length)selected=runs[0].runId;$('health').replaceChildren();for(const c of health.components){const e=node('article',null,'component');e.append(node('strong',c.name),badge(c.status),node('p',c.detail));$('health').append(e);}$('connection').textContent=`Live · ${new Date(health.checkedAt).toLocaleTimeString()}`;addCacheRebuildControl();renderList();if(selected&&(!currentRun||currentRun.runId!==selected||!currentRun.finishedAt))await loadSelected();}catch{$('connection').textContent='Offline · retrying';}finally{busy=false;}}
for(const name of ['All','Failures','LLM','Direct','Voice','Debug']){const b=node('button',name,name===quick?'selected':'');b.onclick=()=>{quick=name;for(const child of $('quick-filters').children)child.classList.toggle('selected',child.textContent===name);renderList();};$('quick-filters').append(b);}
for(const id of ['filter','source','outcome','handled','satellite-filter','conversation-filter','run-filter'])$(id).oninput=renderList;
function sessionGet(key){try{return sessionStorage.getItem(key);}catch{return null;}}
function sessionSet(key,value){try{value==null?sessionStorage.removeItem(key):sessionStorage.setItem(key,value);}catch{}}
const bytes=new Uint8Array(12);crypto.getRandomValues(bytes);const satellite=sessionGet('assister-debug-satellite')||'debug-ui-'+Array.from(bytes,b=>b.toString(16).padStart(2,'0')).join('');sessionSet('assister-debug-satellite',satellite);$('chat-satellite').value=satellite;
let conversation=sessionGet('assister-debug-conversation'),newConversation=!conversation;
function showConversation(){$('chat-conversation').textContent=conversation||'New conversation';sessionSet('assister-debug-conversation',conversation);}
function bubble(who,text){if(!$('chat-messages').querySelector('.bubble'))$('chat-messages').replaceChildren();const b=node('div',null,'bubble '+(who==='User'?'user':'assistant'));b.append(node('div',who,'who'),node('p',text));$('chat-messages').append(b);$('chat-messages').scrollTop=$('chat-messages').scrollHeight;return b;}
$('chat-form').onsubmit=async event=>{event.preventDefault();if(sending)return;const message=$('chat-message').value.trim();if(!message)return;const satelliteId=$('chat-satellite').value.trim();if(!satelliteId){$('chat-status').textContent='A satellite ID is required.';return;}sending=true;$('chat-send').disabled=true;$('new-conversation').disabled=true;$('chat-satellite').disabled=true;$('chat-area').disabled=true;bubble('User',message);$('chat-message').value='';$('chat-status').textContent='Processing through Assister…';try{const response=await fetch('/api/test/message',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({message,satelliteId,area:$('chat-area').value.trim()||null,conversationId:conversation||null,newConversation})});const result=await response.json();if(!result.runId||!result.response)throw Error('No assistant response');conversation=result.conversationId;newConversation=false;showConversation();const b=bubble('Assister',result.response);b.append(node('small',`Handled by: ${result.handledBy} · Outcome: ${result.outcome} · Duration: ${time(result.durationMilliseconds)}\nRun: ${result.runId}`));if(result.spokenResponse!==result.response)b.append(node('small',`Spoken: ${result.spokenResponse}`));const inspectButton=node('button','Inspect Run');inspectButton.onclick=()=>inspect(result.runId);b.append(inspectButton);await refresh();$('chat-status').textContent='Enter to send · Shift+Enter for newline';}catch{bubble('Assister','The request failed or the service is unreachable. Check component health and the run list.');$('chat-status').textContent='Request failed.';}finally{sending=false;$('chat-send').disabled=false;$('new-conversation').disabled=false;$('chat-satellite').disabled=false;$('chat-area').disabled=false;}};
$('chat-message').onkeydown=event=>{if(event.key==='Enter'&&!event.shiftKey){event.preventDefault();$('chat-form').requestSubmit();}};
$('new-conversation').onclick=()=>{conversation=null;newConversation=true;showConversation();$('chat-messages').replaceChildren(node('div','New conversation started.','empty'));};
$('chat-satellite').onchange=()=>{sessionSet('assister-debug-satellite',$('chat-satellite').value.trim());conversation=null;newConversation=true;showConversation();};
$('chat-tab').onclick=()=>selectPanel(true);$('explorer-tab').onclick=()=>selectPanel(false);$('refresh').onclick=refresh;showConversation();refresh();setInterval(refresh,3000);

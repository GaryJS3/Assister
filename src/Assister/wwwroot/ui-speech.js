'use strict';
// Keep realtime media in the browser: Blazor owns page state, not audio bytes or scheduling.
window.assisterSpeech = (() => {
  let context, current;
  const identifier = () => crypto.randomUUID?.() || Array.from(crypto.getRandomValues(new Uint8Array(16)), x => x.toString(16).padStart(2,'0')).join('');
  const report = (id, playbackId, state) => assisterApi.request('POST', `/api/client/interactions/${id}/playback`, {playbackId,state}).catch(() => {});
  async function prepare() {
    try { context ||= new AudioContext(); await context.resume(); return context.state === 'running'; }
    catch { return false; }
  }
  function stop() {
    if (!current) return;
    const old=current; current=null; clearTimeout(old.retry); old.socket?.close(); old.player?.stop(); old.audio?.pause(); old.tone?.pause(); old.finishTone?.();
  }
  function start(id) {
    stop();
    const run=current={id,cursor:0,terminal:false,tones:Promise.resolve()};
    function connect() {
      if(current!==run || run.terminal)return;
      const socket=run.socket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/client/interactions/${id}/stream?afterSequence=${run.cursor}`);
      socket.onmessage=message=>{
        if(current!==run)return;
        try {
          const ev=JSON.parse(message.data), data=ev.data;
          if(!ev.sequence || ev.sequence<=run.cursor)return;
          if(ev.sequence!==run.cursor+1) {socket.close();return;}
          run.cursor=ev.sequence;
          if(ev.type==='tone.play' && Date.parse(data.expiresAt)>=Date.now()) {
            const play=()=>new Promise(resolve=>{
              if(current!==run || run.cancelled || Date.parse(data.expiresAt)<Date.now())return resolve();
              const tone=run.tone=new Audio(data.url);
              const finish=()=>{clearTimeout(timeout);run.finishTone=null;resolve();};
              const timeout=setTimeout(()=>{tone.pause();finish();},5000);
              run.finishTone=finish;
              tone.onended=finish;tone.onerror=finish;
              tone.play().catch(finish);
            });
            if(data.placement==='after-response-audio'){if(run.playbackCompleted)void play();else run.goodbye=play;}
            else run.tones=run.tones.then(play);
          }
          if(ev.type==='tts.segment' && context?.state==='running') {
            if(!run.player) { const playbackId=identifier(); run.player=new AssisterSpeechPlayer(context,{ready:run.tones,report:state=>{report(id,playbackId,state);if(state==='completed'){run.playbackCompleted=true;void run.goodbye?.();}}}); }
            run.player.add(data);
          }
          if(ev.type==='tts.completed' && data.streaming)run.player?.finish(data.segmentCount);
          if(ev.type==='tts.audio' && !run.player) {
            const audio=run.audio=new Audio(data.url), playbackId=identifier();
            audio.onplaying=()=>report(id,playbackId,'started');
            audio.onended=()=>{report(id,playbackId,'completed');run.playbackCompleted=true;void run.goodbye?.();};
            audio.onpause=()=>{if(!audio.ended)report(id,playbackId,'stopped');};
            audio.onerror=()=>report(id,playbackId,'failed');
            void run.tones.then(()=>current===run && !run.cancelled && audio.play().catch(()=>{})); // Retained controls remain available if autoplay is blocked.
          }
          if(['tts.failed','interaction.failed','interaction.cancelled','interaction.cancel_requested'].includes(ev.type)){run.cancelled=true;run.tone?.pause();run.finishTone?.();run.player?.stop('failed');run.audio?.pause();}
          if(['interaction.completed','interaction.failed','interaction.cancelled'].includes(ev.type)){run.terminal=true;socket.close();}
        } catch {socket.close();}
      };
      socket.onclose=()=>{if(current===run&&!run.terminal)run.retry=setTimeout(connect,1000);};
    }
    connect();
  }
  // Resume within the user gesture, before the server-side click handler round trip.
  document.addEventListener('click', e=>{if(e.target.closest('#chat-send'))void prepare();},true);
  // Manual replay also refreshes conversation completion time and stops overlapping streamed audio.
  const replays=new WeakMap();
  for(const type of ['playing','ended','pause','error'])document.addEventListener(type,e=>{
    const audio=e.target,id=audio.dataset?.interaction;if(!id)return;
    let playbackId=replays.get(audio);
    if(type==='playing') {stop();playbackId ||= identifier();replays.set(audio,playbackId);report(id,playbackId,'started');}
    else if(playbackId) {report(id,playbackId,type==='ended'?'completed':type==='error'?'failed':'stopped');replays.delete(audio);}
  },true);
  return {prepare,start,stop};
})();

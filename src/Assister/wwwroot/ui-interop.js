"use strict";
window.assisterApi = {
  async request(method,path,body,token,ifMatch) {
    if(!path.startsWith('/api/'))throw new Error('Invalid API path.');
    const headers={};if(body!==null&&body!==undefined)headers['Content-Type']='application/json';
    if(token)headers.Authorization='Bearer '+token;
    if(ifMatch)headers['If-Match']=ifMatch;
    const r=await fetch(path,{method,headers,credentials:'same-origin',cache:'no-store',body:body===null||body===undefined?undefined:JSON.stringify(body)});
    if(!r.ok){const d=await r.json().catch(()=>null);throw new Error(d?.error||d?.message||d?.detail||'Request failed: '+path+' (HTTP '+r.status+').' );}
    if(r.status===204)return null;
    const s=await r.text();return s?JSON.parse(s):null;
  },
  async upload(path,id,token){
    const file=document.getElementById(id)?.files?.[0];if(!file||file.size<1||file.size>512*1048576)throw new Error('Choose a nonempty package up to 512 MiB.');
    const url=new URL(path,location.origin);url.searchParams.set('name',file.name);
    const r=await fetch(url,{method:'PUT',credentials:'same-origin',headers:{Authorization:'Bearer '+token,'Content-Type':'application/octet-stream'},body:file});
    if(!r.ok){const d=await r.json().catch(()=>null);throw new Error(d?.error||d?.detail||'Upload failed (HTTP '+r.status+').');}
    return r.status===204?null:await r.json().catch(()=>null);
  },
  storage(key){return localStorage.getItem(key);},
  setStorage(key,value){if(value==null)localStorage.removeItem(key);else localStorage.setItem(key,value);},
  copy(value){return navigator.clipboard.writeText(value);},
  confirm(message){return window.confirm(message);}
};

// Rich client attachments remain browser-to-API uploads to avoid routing large files through Blazor's SignalR circuit.
window.assisterApi.uploadAttachments = async function(inputId) {
  const input=document.getElementById(inputId), files=Array.from(input?.files||[]);
  const result=[];
  for(const file of files.slice(0,8)) {
    const mime=/\.md$/i.test(file.name)?'text/markdown':/\.json$/i.test(file.name)?'application/json':/\.txt$/i.test(file.name)?'text/plain':file.type||'application/octet-stream';
    const response=await fetch('/api/client/attachments?name='+encodeURIComponent(file.name)+'&source=file_picker',
      {method:'POST',headers:{'Content-Type':mime},credentials:'same-origin',body:file});
    if(!response.ok){const error=await response.json().catch(()=>null);throw new Error(error?.message||'Attachment upload failed (HTTP '+response.status+')');}
    result.push(await response.json());
  }
  if(input)input.value='';
  return result;
};
let voiceCapture=null;
window.assisterApi.voiceStart=async function() {
  if(voiceCapture)throw new Error('Already recording.');
  if(!navigator.mediaDevices?.getUserMedia||!window.AudioWorkletNode)throw new Error('Microphone requires HTTPS or localhost and AudioWorklet support.');
  const stream=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1},video:false});
  try{
    const context=new AudioContext();
    await context.audioWorklet.addModule('/pcm-capture.js');
    await context.resume();
    const node=new AudioWorkletNode(context,'pcm-capture');
    const source=context.createMediaStreamSource(stream);
    const capture={stream,context,node,source,chunks:[],size:0};
    node.port.onmessage=e=>{
      if(e.data.pcm){capture.chunks.push(new Uint8Array(e.data.pcm));capture.size+=e.data.pcm.byteLength;}
      if(e.data.done)capture.done?.();
    };
    source.connect(node);node.connect(context.destination);voiceCapture=capture;
  }catch(e){stream.getTracks().forEach(track=>track.stop());throw e;}
};
window.assisterApi.voiceStop=async function() {
  const capture=voiceCapture;if(!capture)return null;voiceCapture=null;
  try{
    await Promise.race([new Promise(resolve=>{capture.done=resolve;capture.node.port.postMessage('stop');}),new Promise(resolve=>setTimeout(resolve,2000))]);
  }finally{
    capture.stream.getTracks().forEach(track=>track.stop());capture.source.disconnect();capture.node.disconnect();await capture.context.close();
  }
  const bytes=new Uint8Array(capture.size);let position=0;
  for(const chunk of capture.chunks){bytes.set(chunk,position);position+=chunk.length;}
  if(!bytes.length)throw new Error('No speech recorded.');
  const response=await fetch('/api/client/attachments?name=Voice.pcm&source=microphone',
    {method:'POST',credentials:'same-origin',headers:{'Content-Type':'audio/pcm'},body:bytes});
  if(!response.ok)throw new Error('Voice upload failed (HTTP '+response.status+').');
  return (await response.json()).id;
};
window.assisterApi.voiceCancel=async function() {
  const capture=voiceCapture;if(!capture)return;voiceCapture=null;
  capture.stream.getTracks().forEach(track=>track.stop());capture.source.disconnect();capture.node.disconnect();await capture.context.close();
};

window.assisterApi.toIso = value => new Date(value).toISOString();

// Chunk large JSON responses over interop while retaining browser-origin cookie authorization.
assisterApi.requestStream = async (...args) => new Blob([JSON.stringify(await assisterApi.request(...args))], {type:'application/json'});
assisterApi.uploadStream = async (...args) => new Blob([JSON.stringify(await assisterApi.upload(...args))], {type:'application/json'});

assisterApi.timeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;
assisterApi.locale = () => navigator.language || 'en-US';

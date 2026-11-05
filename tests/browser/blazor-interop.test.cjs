const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
test('API results use a blob for chunked interop, including payloads above the hub limit',async()=>{
 const calls=[];
 const scope={window:{},Blob,fetch:async(path,options)=>{calls.push({path,options});return {ok:true,status:200,text:async()=>JSON.stringify({text:'x'.repeat(100000)})};}};
 scope.window=scope;vm.createContext(scope);vm.runInContext(fs.readFileSync('src/Assister/wwwroot/ui-interop.js','utf8'),scope);
 const blob=await scope.assisterApi.requestStream('GET','/api/diagnostics/runs');
 assert.ok(blob instanceof Blob);assert.equal(JSON.parse(await blob.text()).text.length,100000);
 assert.equal(calls[0].options.credentials,'same-origin');
});
test('streamed speech deduplicates replayed events, finishes after terminal events, and reports playback',async()=>{
 const sockets=[],reports=[],players=[];
 const scope={window:{},document:{addEventListener(){}},crypto:{randomUUID:()=> 'playback-id'},location:{protocol:'http:',host:'localhost'},setTimeout,clearTimeout,
 AudioContext:class{state='running';async resume(){}},
 WebSocket:class{constructor(){sockets.push(this);}close(){}},
 AssisterSpeechPlayer:class{constructor(ctx,options){this.options=options;this.segments=[];players.push(this);}add(data){this.segments.push(data);this.options.report('started');}finish(count){this.count=count;}stop(){this.stopped=true;}},
 assisterApi:{request:async(...args)=>reports.push(args)}};
 scope.window=scope;vm.createContext(scope);vm.runInContext(fs.readFileSync('src/Assister/wwwroot/ui-speech.js','utf8'),scope);
 await scope.assisterSpeech.prepare();scope.assisterSpeech.start('run');
 const send=(sequence,type,data={})=>sockets[0].onmessage({data:JSON.stringify({sequence,type,data})});
 send(1,'tts.segment',{index:0});send(1,'tts.segment',{index:0});send(2,'tts.completed',{streaming:true,segmentCount:1});send(3,'interaction.completed');
 assert.equal(players[0].segments.length,1);assert.equal(players[0].count,1);assert.ok(!players[0].stopped);
 players[0].options.report('completed');assert.deepEqual(reports.map(x=>x[2].state),['started','completed']);
 scope.assisterSpeech.stop();assert.ok(players[0].stopped);
});

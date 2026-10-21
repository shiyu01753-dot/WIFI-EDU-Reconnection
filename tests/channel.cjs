'use strict';
const assert=require('node:assert/strict'),http=require('node:http');
const {selectAdapter,createChannel}=require('../channel.cjs');
const {request,selectedChannel}=require('../reconnect.cjs');
const wired={kind:'wired',up:true,guid:'synthetic-wired',index:4,addresses:['127.0.0.2'],gateway:'192.0.2.1',dns:[]};
const wireless={...wired,kind:'wireless',guid:'synthetic-wireless',index:5};
async function verify(){
  assert.equal(selectAdapter([wired,wireless],'wired').guid,wired.guid);
  assert.equal(selectAdapter([wired,wireless],'wireless').guid,wireless.guid);
  assert.throws(()=>selectAdapter([wired,{...wired,guid:'other'}],'wired'));
  assert.throws(()=>selectAdapter([{...wired,up:false},wireless],'wired'));
  assert.throws(()=>selectAdapter([wired],'wired',{guid:wired.guid,gateway:'192.0.2.2'}));
  let requests=0,source='',wrong=false;
  const server=http.createServer((req,res)=>{requests++;source=req.socket.remoteAddress;res.end('OK');});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try{
    const channel=await createChannel({channel:'wired'},null,request,{inspect:async mode=>mode==='route'?
      {...wired,address:wired.addresses[0],index:wrong?5:4}:[wired,wireless]});
    const url='http://127.0.0.1:'+server.address().port;
    assert.equal((await channel.request(url)).body,'OK');assert.equal(source,'127.0.0.2');
    wrong=true;await assert.rejects(()=>channel.request(url));assert.equal(requests,1);
  }finally{await new Promise(resolve=>server.close(resolve));}
  const config={channel:'wired',engine:'srun',portalUrl:'http://192.0.2.10',acId:'12',username:'synthetic-user',wifiProfile:'OtherWifi'};
  const calls=[],states=[];let loggedIn=true,account='synthetic-user';
  const fetch=async url=>{
    const parsed=new URL(url);calls.push(parsed);
    if(parsed.hostname==='www.msftconnecttest.com')return {status:200,body:'Microsoft Connect Test'};
    if(parsed.pathname.endsWith('rad_user_info'))return {status:200,body:JSON.stringify(loggedIn?{error:'ok',user_name:account}:{error:'not_online'})};
    if(parsed.pathname.endsWith('get_challenge'))return {status:200,body:JSON.stringify({error:'ok',challenge:'synthetic-token',client_ip:'192.0.2.20'})};
    if(parsed.pathname.endsWith('srun_portal')){loggedIn=parsed.searchParams.get('action')==='login';return {status:200,body:'{"error":"ok"}'};}
    return {status:503,body:''};
  };
  const deps={sleep:async()=>{},wifiState:async()=>{throw Error('wired must not inspect Wi-Fi');},
    createChannel:async chosen=>{assert.equal(chosen.channel,'wired');assert.equal(chosen.wifiProfile,'');return {request:fetch,binding:{address:'192.0.2.20'}};}};
  const write=(message,state)=>states.push(state);
  await selectedChannel(config,'synthetic-password',write,null,true,deps);
  assert.equal(calls.filter(u=>u.searchParams.get('action')==='logout').length,1);
  assert.equal(calls.filter(u=>u.searchParams.get('action')==='login').length,1);assert.equal(states.at(-1),'online');
  calls.length=0;account='other-user';loggedIn=true;
  await selectedChannel(config,'synthetic-password',write,null,true,deps);
  assert.equal(calls.filter(u=>u.pathname.endsWith('srun_portal')).length,0);
  calls.length=0;
  await selectedChannel(config,'synthetic-password',write,null,false,{...deps,createChannel:async()=>{throw Error('Cable removed');}});
  assert.equal(calls.length,0);assert.equal(states.at(-1),'waiting');
  calls.length=0;
  await selectedChannel({...config,engine:'form'},'synthetic-password',write,null,true,deps);
  assert.equal(calls.length,0);
  console.log('PASS: channel isolation, physical adapter selection, gateway changes, real bound socket, wrong-route rejection, wired logout/login, unrelated-account protection, cable removal, unsupported logout');
}
verify().catch(error=>{console.error(error);process.exitCode=1;});

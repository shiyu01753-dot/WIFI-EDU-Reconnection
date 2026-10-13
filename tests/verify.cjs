'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {loginParams,parseJsonp,tick,networkProfile,parseAcId,discoverAcId,runEngine} = require('../reconnect.cjs');
const {vectors} = JSON.parse(fs.readFileSync(path.join(__dirname,'srun-vectors.json'),'utf8'));
const token = vectors[0].token;
for (const sample of vectors) {
  const actual=loginParams(sample.username,sample.password,sample.token,sample.ip,sample.acid);
  for (const key of ['username','password','info','chksum','ip','ac_id','n','type','double_stack'])
    assert.equal(String(actual[key]),String(sample.expected[key]),key);
}
assert.deepEqual(parseJsonp('campusCallback({"error":"ok"});'),{error:'ok'});
assert.throws(()=>parseJsonp('evil();'));
const portal='http://192.0.2.1';
const config={portalUrl:portal,acId:'12',username:'test-user',wifiProfile:'ExampleCampus'};
const states=[];
const write=(message,state)=>states.push(state);
const base={internet:async()=>false,sleep:async()=>{},wifiState:async()=>({known:true,ssid:'ExampleCampus'})};
async function verify() {
  assert.equal(networkProfile({...config,portalUrl:portal+'/login?ac_id=12'}).portal,portal);
  for (const url of ['', 'file:///test','http://user:secret@example.invalid']) assert.throws(()=>networkProfile({...config,portalUrl:url}));
  assert.throws(()=>networkProfile({...config,acId:'bad'}));
  assert.equal(parseAcId('/login?ac_id=80&theme=sample'),'80');
  assert.equal(await discoverAcId(networkProfile(config),()=>{throw Error('manual mode should not fetch');}),'12');
  const profile=networkProfile({...config,acId:''});
  assert.equal(await discoverAcId(profile,async()=>({status:302,body:'',location:'/login?ac_id=80'})),'80');
  await assert.rejects(discoverAcId(profile,async()=>({status:302,body:'',location:'https://other.invalid/?ac_id=80'})));
  await assert.rejects(discoverAcId(profile,async()=>({status:200,body:'Unknown page',location:''})));
  let requests=0;
  await tick(config,'synthetic',write,{...base,internet:async()=>true,api:async()=>{requests++;}});
  assert.equal(requests,0);
  await tick(config,'synthetic',write,{...base,api:async()=>{requests++;return{error:'ok'};}});
  assert.equal(requests,1);
  requests=0;
  await tick(config,'synthetic',write,{...base,wifiState:async()=>({known:true,ssid:'OtherWiFi'}),api:async()=>{requests++;}});
  assert.equal(requests,0);
  let connections=0,checks=0,submitted=0;
  const api=async(endpoint,params)=>{
    if(endpoint.endsWith('rad_user_info'))return{error:'not_online_error'};
    if(endpoint.endsWith('get_challenge'))return{error:'ok',challenge:token,client_ip:'192.0.2.2'};
    submitted++;assert.equal(params.ac_id,'12');return{error:'password_error'};
  };
  assert.equal(await tick(config,'synthetic',write,{...base,api,wifiState:async()=>++checks===1?{known:true,disconnected:true}:{known:true,ssid:'ExampleCampus'},
    connectWifi:async name=>{assert.equal(name,'ExampleCampus');connections++;}}),-1);
  assert.equal(connections,1);assert.equal(submitted,1);assert.equal(states.at(-1),'blocked');
  submitted=0;
  await tick({...config,acId:''},'synthetic',write,{...base,api,discoverAcId:async()=>{throw Error('unknown gateway');}});
  assert.equal(submitted,0,'failed discovery sends no login');
  await tick(config,'synthetic',write,{...base,api:async()=>({error:'server_error'})});
  assert.equal(submitted,0);
  checks=0;
  assert.equal(await runEngine({...config,engine:'system'},'',write,{...base,internet:async()=>true,
    wifiState:async()=>++checks===1?{known:true,disconnected:true}:{known:true,ssid:'ExampleCampus'},connectWifi:async()=>{connections++;}}),30000);
  assert.equal(await runEngine({engine:'system',wifiProfile:''},'',write,base),-1);
  assert.equal(await runEngine({engine:'system',wifiProfile:'SavedAlias',wifiSsid:'ExampleCampus'},'',write,{...base,internet:async()=>true}),30000);
  assert.equal(await runEngine({...config,engine:'unknown'},'',write,base),-1);
  // Real HTTP transport, loopback fixture, synthetic credentials only.
  let posts=0,actualBody='',responseText='SUCCESS';
  const server=http.createServer((req,res)=>{
    assert.equal(req.method,'POST');assert.equal(req.url,'/session');posts++;
    req.on('data',chunk=>actualBody+=chunk);req.on('end',()=>{res.end(responseText);});
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    const form={engine:'form',portalUrl:'http://127.0.0.1:'+server.address().port,username:'synthetic-user',wifiProfile:'',formLoginPath:'/session',
      formUsernameField:'login',formPasswordField:'secret',formSuccessMarker:'SUCCESS'};
    let probes=0;
    assert.equal(await runEngine(form,'synthetic&secret',write,{...base,internet:async()=>++probes>=3}),30000);
    assert.equal(new URLSearchParams(actualBody).get('secret'),'synthetic&secret');assert.equal(posts,1);
    responseText='DENIED';actualBody='';
    assert.equal(await runEngine(form,'synthetic',write,base),-1);
    const before=posts;
    for(const bad of ['//other.invalid/login','/session?token=synthetic']) assert.equal(await runEngine({...form,formLoginPath:bad},'synthetic',write,base),-1);
    assert.equal(posts,before,'unsafe targets receive no credentials');
  } finally { await new Promise(resolve=>server.close(resolve)); }
  console.log('PASS: reference vectors, editable endpoint validation, same-origin discovery, no login while online or on another Wi-Fi, bounded rejection, three engine dispatch, real HTTP form transport');
}
verify().catch(error=>{console.error(error);process.exitCode=1;});

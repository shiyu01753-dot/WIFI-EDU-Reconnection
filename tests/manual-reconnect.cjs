'use strict';
const assert = require('node:assert/strict');
const {manualReconnect} = require('../reconnect.cjs');
async function verify() {
  const config = {engine:'system',wifiProfile:'SavedProfile',wifiSsid:'ExampleCampus'};
  let state = {known:true,ssid:'ExampleCampus',interfaceName:'Wi-Fi'};
  const calls = [], results = [];
  const deps = {
    wifiState:async()=>state, sleep:async()=>{}, internet:async()=>true,
    disconnectWifi:async name=>{calls.push(['disconnect',name]);state={known:true,disconnected:true,interfaceName:name};},
    connectWifi:async (profile,name)=>{calls.push(['connect',profile,name]);state={known:true,ssid:'ExampleCampus',interfaceName:name};}
  };
  const write = (message,status)=>results.push(status);
  await manualReconnect(config,'',write,deps);
  assert.deepEqual(calls,[['disconnect','Wi-Fi'],['connect','SavedProfile','Wi-Fi']]);
  assert.equal(results.at(-1),'online');
  for (const unsafe of [{known:false},{known:true,ssid:'OtherNetwork',interfaceName:'Wi-Fi'},
    {known:true,ssid:'ExampleCampus'}]) {
    state=unsafe; calls.length=0;
    await manualReconnect(config,'',write,deps);
    assert.equal(calls.length,0,'unconfirmed or unrelated networks must not be disconnected');
  }
  state={known:true,disconnected:true,interfaceName:'Wi-Fi'}; calls.length=0;
  await manualReconnect(config,'',write,deps);
  assert.deepEqual(calls,[['connect','SavedProfile','Wi-Fi']]);
  state={known:true,ssid:'ExampleCampus',interfaceName:'Wi-Fi'};
  const noRestore={...deps,connectWifi:async()=>{calls.push(['failed-connect']);}};
  calls.length=0;
  await manualReconnect(config,'',write,noRestore);
  assert.equal(results.at(-1),'waiting');
  console.log('PASS: targeted manual reconnect, saved profile/SSID mapping, disconnected recovery, unrelated-network protection, failed recovery');
}
verify().catch(error=>{console.error(error);process.exitCode=1;});

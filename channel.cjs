'use strict';
const fs=require('node:fs'),path=require('node:path'),net=require('node:net');
const {Resolver}=require('node:dns').promises;
const {execFile}=require('node:child_process'),{promisify}=require('node:util');
const run=promisify(execFile);
async function inspect(mode='inventory',binding,remote) {
  const args=['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',path.join(__dirname,'network-channel.ps1'),'-Mode',mode];
  if(binding)args.push('-Guid',binding.guid,'-LocalAddress',binding.address,'-RemoteAddress',remote);
  const {stdout}=await run('powershell.exe',args,{windowsHide:true,timeout:15000,encoding:'utf8'});
  return JSON.parse(stdout.replace(/^\uFEFF/,''));
}
function selectAdapter(adapters,kind,previous) {
  if(!['wired','wireless'].includes(kind))throw Error('Unknown channel');
  const candidates=adapters.filter(a=>a.kind===kind&&a.up&&a.addresses.length===1&&a.gateway);
  const matches=previous?candidates.filter(a=>a.guid===previous.guid&&a.gateway===previous.gateway):candidates;
  if(matches.length!==1)throw Error('Channel missing, changed or ambiguous');
  return {...matches[0],address:matches[0].addresses[0]};
}
async function createChannel(config,directory,transport,deps={}) {
  const kind=config.channel||'wireless',inventory=deps.inspect||inspect;
  const signature=JSON.stringify([kind,config.portalUrl||'',config.wifiProfile||'',config.bindingRevision||'']);
  const file=directory?path.join(directory,'channel-binding.json'):null;
  let previous;
  if(file&&fs.existsSync(file)) {
    const stored=JSON.parse(fs.readFileSync(file,'utf8'));
    if(stored.signature===signature)previous=stored.binding;
  }
  const binding=selectAdapter(await inventory(),kind,previous);
  async function resolve(host) {
    if(net.isIP(host)===4)return host;
    const servers=binding.dns.filter(address=>net.isIP(address)===4);
    if(net.isIP(host)||!servers.length)throw Error('IPv4 DNS unavailable');
    const resolver=new Resolver();resolver.setLocalAddress(binding.address);resolver.setServers(servers);
    const timer=setTimeout(()=>resolver.cancel(),5000);
    try {const addresses=await resolver.resolve4(host);if(!addresses.length)throw Error('No address');return addresses[0];}
    finally{clearTimeout(timer);}
  }
  async function request(url,timeout=7000,options={}) {
    if(kind==='wireless'&&config.wifiProfile) {
      const state=await require('./reconnect.cjs').wifiState();
      if(state.ssid!==(config.wifiSsid||config.wifiProfile))throw Error('Wireless network changed');
    }
    const target=new URL(url),remote=await (deps.resolve||resolve)(target.hostname);
    if(net.isIP(remote)!==4)throw Error('Invalid target address');
    const route=await inventory('route',binding,remote);
    if(route.index!==binding.index||route.guid!==binding.guid||route.address!==binding.address||route.gateway!==binding.gateway)
      throw Error('Selected channel changed');
    return transport(url,timeout,{...options,localAddress:binding.address,family:4,agent:false,
      lookup:(_host,_options,callback)=>callback(null,remote,4)});
  }
  // Local binding contains no credentials and is never included in a release.
  if(file)fs.writeFileSync(file,JSON.stringify({signature,binding:{guid:binding.guid,gateway:binding.gateway}}),'utf8');
  return {request,binding};
}
module.exports={inspect,selectAdapter,createChannel};

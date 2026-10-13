'use strict';
const assert=require('node:assert/strict');
const http=require('node:http');
const {discoverNetwork,identifyPage,cleanUrl,pageRedirect}=require('../discovery.cjs');
const wifiState=async()=>({ssid:'ExampleCampus',known:true});
async function verify(){
  assert.equal(cleanUrl('http://192.0.2.1/login?ac_id=12&password=synthetic&token=synthetic#fragment').href,'http://192.0.2.1/login?ac_id=12');
  for(const url of ['file:///test','http://user:synthetic@example.invalid','http://192.0.2.1/logout.php','http://192.0.2.1/?action=login'])assert.throws(()=>cleanUrl(url));
  assert.equal(pageRedirect('<script>function logout(){window.location="/logout";}</script>'),null);
  assert.equal(pageRedirect('<meta http-equiv="refresh" content="0; url=/entry">'),'/entry');
  assert.equal(identifyPage('<script src="/jquery.srun.portal.js"></script>','http://192.0.2.1/srun_portal_pc?ac_id=12').engine,'srun');
  const form='<form action="/session" method="POST"><input name="login" type="text"><input name="secret" type="password"></form>';
  const found=identifyPage(form,'http://192.0.2.1/login');
  assert.equal(found.engine,'form');assert.equal(found.formLoginPath,'/session');assert.equal(found.formUsernameField,'login');assert.equal(found.ready,false);
  assert.equal(identifyPage(form.replace('/session','http://other.invalid/session'),'http://192.0.2.1/login').recognized,false);
  assert.equal(identifyPage(form.replace('</form>','<input type="hidden" name="csrf" value="synthetic"></form>'),'http://192.0.2.1/login').recognized,false);
  assert.equal(identifyPage(form.replace('</form>','<img src="/captcha"></form>'),'http://192.0.2.1/login').recognized,false);
  const requests=[];
  const auto=await discoverNetwork('',{wifiState,request:async(url,timeout,options)=>{
    requests.push(url);assert.ok(!options||!options.method||options.method==='GET');
    if(url.includes('msftconnecttest'))return{status:302,body:'',location:'http://192.0.2.1/srun_portal_pc?ac_id=12'};
    return{status:200,body:'<h1>Authentication</h1>',location:''};
  }});
  assert.equal(auto.engine,'srun');assert.equal(auto.acId,'12');assert.equal(auto.wifiProfile,'ExampleCampus');assert.equal(auto.portalUrl,'http://192.0.2.1');
  assert.equal(requests.length,2);
  const alias=await discoverNetwork('http://192.0.2.1/srun_portal_pc?ac_id=12',{
    wifiState:async()=>({ssid:'ExampleSSID',profile:'SavedNetworkAlias'}),request:async()=>({status:200,body:'',location:''})});
  assert.equal(alias.wifiProfile,'SavedNetworkAlias');assert.equal(alias.wifiSsid,'ExampleSSID');
  const enterprise=await discoverNetwork('',{wifiState:async()=>({ssid:'ExampleSecure',enterprise:true}),request:()=>{throw Error('system network should not query portal');}});
  assert.equal(enterprise.engine,'system');assert.equal(enterprise.portalUrl,'');
  const online=await discoverNetwork('',{wifiState,request:async url=>({status:url.includes('gstatic')?204:200,body:'Microsoft Connect Test',location:''})});
  assert.equal(online.recognized,false,'do not misclassify already-online access as a portal');
  let loops=0;
  const loop=await discoverNetwork('http://192.0.2.1/a',{wifiState,request:async()=>{loops++;return{status:302,body:'',location:'/a'};}});
  assert.equal(loop.recognized,false);assert.equal(loops,1);
  const script=await discoverNetwork('http://192.0.2.1/',{wifiState,request:async url=>url.endsWith('/app.js')?
    {status:200,body:'/cgi-bin/get_challenge /cgi-bin/srun_portal',location:''}:
    {status:200,body:'<script src="/app.js"></script>',location:''}});
  assert.equal(script.engine,'srun');
  const anon=await discoverNetwork('http://192.0.2.1/',{wifiState,request:async url=>({status:200,location:'',body:url.includes('rad_user_info')?'discovery({"error":"not_online_error"});':'<h1>Login</h1>'})});
  assert.equal(anon.engine,'srun');
  // Real GET redirect transport with no credentials or login requests.
  let actualRequests=0;
  const server=http.createServer((req,res)=>{
    actualRequests++;assert.equal(req.method,'GET');assert.ok(!req.url.includes('password='));
    if(req.url.startsWith('/start')){res.writeHead(302,{Location:'/srun_portal_pc?ac_id=18'});res.end();}
    else res.end('<h1>Portal</h1>');
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try{
    const local=await discoverNetwork('http://127.0.0.1:'+server.address().port+'/start?password=synthetic',{wifiState});
    assert.equal(local.engine,'srun');assert.equal(local.acId,'18');assert.equal(actualRequests,2);
    const {stdout}=await require('node:util').promisify(require('node:child_process').execFile)(process.execPath,
      [require('node:path').join(__dirname,'../discovery.cjs'),'http://127.0.0.1:'+server.address().port+'/start'],{windowsHide:true,timeout:35000});
    const cli=JSON.parse(stdout);assert.equal(cli.engine,'srun');assert.equal(cli.acId,'18');assert.equal(actualRequests,4);
    const pipe=await new Promise((resolve,reject)=>{
      const child=require('node:child_process').execFile(process.execPath,[require('node:path').join(__dirname,'../discovery.cjs'),'--stdin'],
        {windowsHide:true,timeout:35000},(error,output)=>{if(error)reject(error);else{try{resolve(JSON.parse(output));}catch(error){reject(error);}}});
      child.stdin.end('http://127.0.0.1:'+server.address().port+'/start?password=synthetic');
    });
    assert.equal(pipe.engine,'srun');assert.equal(pipe.acId,'18');assert.equal(actualRequests,6);
  }finally{await new Promise(resolve=>server.close(resolve));}
  console.log('PASS: automatic discovery, redirects, script fingerprints, anonymous status, field mapping, enterprise Wi-Fi, already-online boundary, captcha/CSRF/cross-host form rejection, loop limits, credential stripping, real GET transport');
}
verify().catch(error=>{console.error(error);process.exitCode=1;});

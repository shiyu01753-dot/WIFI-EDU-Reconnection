'use strict';
const {request,wifiState,parseAcId,parseJsonp}=require('./reconnect.cjs');
const SECRET_KEYS=/^(?:password|user_password|passwd|pass|token|access_token|ticket|authorization|username|user_account)$/i;
const PROBE_HOSTS=new Set(['www.msftconnecttest.com','connectivitycheck.gstatic.com']);
function cleanUrl(value,base) {
  const url=new URL(value,base);
  if(!['http:','https:'].includes(url.protocol)||url.username||url.password) throw Error('Invalid discovery URL');
  if(/(?:^|\/)(?:logout|logoff|signout|unbind)(?:\.[\w]+)?(?:\/|$)/i.test(url.pathname)||
      ['action','method','operation'].some(key=>/^(?:logout|logoff|login|delete|remove|unbind)$/i.test(url.searchParams.get(key)||'')))throw Error('State-changing discovery URL');
  for(const key of [...url.searchParams.keys()]) if(SECRET_KEYS.test(key))url.searchParams.delete(key);
  url.hash='';return url;
}
function attributes(tag) {
  const result={};
  const pattern=/([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/g;
  for(const m of tag.matchAll(pattern))result[m[1].toLowerCase()]=(m[2]??m[3]??m[4]).replace(/&amp;/gi,'&');
  return result;
}
function pageRedirect(body) {
  const meta=body.match(/<meta\b[^>]*http-equiv\s*=\s*["']?refresh["']?[^>]*>/i);
  if(meta){const match=(attributes(meta[0]).content||'').match(/\burl\s*=\s*['"]?(.+?)['"]?\s*$/i);if(match)return match[1];}
  // Only read a literal URL; never evaluate scripts.
  const literal=body.match(/<script\b[^>]*>\s*(?:window\.)?location(?:\.href)?\s*=\s*['"]([^'"<>]+)['"]\s*;?\s*<\/script>/i);
  return literal?literal[1]:null;
}
function identifyPage(body,url) {
  const page=cleanUrl(url), acid=parseAcId(page.href+' '+body)||'';
  if(/\/srun_portal_(?:pc|success|phone)\b/i.test(page.pathname)||/jquery\.srun\.portal|\/cgi-bin\/(?:get_challenge|srun_portal)/i.test(body))
    return {recognized:true,engine:'srun',portalUrl:page.origin,acId:acid,ready:true,message:'已识别认证方式'};
  const forms=[...body.matchAll(/<form\b([^>]*)>([\s\S]*?)<\/form\s*>/gi)];
  const candidates=[];
  for(const form of forms){
    const attr=attributes(form[1]);if((attr.method||'get').toLowerCase()!=='post')continue;
    const inputs=[...form[2].matchAll(/<input\b[^>]*>/gi)].map(m=>attributes(m[0]));
    const passwords=inputs.filter(i=>(i.type||'').toLowerCase()==='password'&&i.name);
    const users=inputs.filter(i=>i.name&&['text','email',''].includes((i.type||'').toLowerCase()));
    if(passwords.length!==1||users.length!==1)continue;
    if(/captcha|recaptcha|hcaptcha|验证码/i.test(form[2])||inputs.some(i=>(i.type||'').toLowerCase()==='hidden'&&i.name))
      return {recognized:false,portalUrl:page.origin,acId:acid,engine:'unknown',message:'该页面需要额外认证步骤，请使用浏览器'};
    let target;try{target=cleanUrl(attr.action||page.href,page.href);}catch{continue;}
    if(target.origin!==page.origin||target.search)continue;
    candidates.push({recognized:true,engine:'form',portalUrl:page.origin,acId:'',ready:false,formLoginPath:target.pathname,
      formUsernameField:users[0].name,formPasswordField:passwords[0].name,formSuccessMarker:'',message:'已识别表单；请补充成功标记'});
  }
  if(candidates.length===1)return candidates[0];
  return {recognized:false,engine:'unknown',portalUrl:page.origin,acId:acid,message:'未识别认证方式，请检查网址'};
}
async function discoverNetwork(input='',deps={}) {
  const fetch=deps.request||request, wifi=deps.wifiState||wifiState;
  const deadline=Date.now()+25000;let count=0,wifiProfile='',wifiSsid='';
  try{const state=await wifi();wifiSsid=state.ssid||'';wifiProfile=state.profile||wifiSsid;
    if(!input&&state.enterprise&&wifiProfile)return {recognized:true,engine:'system',portalUrl:'',acId:'',wifiProfile,wifiSsid,ready:true,message:'已读取系统网络配置'};
  }catch{}
  const seeds=input?[cleanUrl(input).href]:['http://www.msftconnecttest.com/connecttest.txt','http://connectivitycheck.gstatic.com/generate_204'];
  let last={recognized:false,engine:'unknown',message:'未发现认证页面；请粘贴认证网址'};
  async function read(url){
    if(++count>10||Date.now()>=deadline)throw Error('Discovery limit');
    return fetch(url,Math.max(1,Math.min(4000,deadline-Date.now())));
  }
  for(const seed of seeds){
    let url=cleanUrl(seed);const seen=new Set();let observedOrigin=url.origin,observedAcId='';
    try{
      for(let hop=0;hop<5;hop++){
        if(seen.has(url.href))break;seen.add(url.href);
        if(observedOrigin!==url.origin){observedOrigin=url.origin;observedAcId='';}
        observedAcId=parseAcId(url.href)||observedAcId;
        const response=await read(url.href);
        if(response.status===204||(PROBE_HOSTS.has(url.hostname)&&response.status===200&&response.body.trim()==='Microsoft Connect Test'))break;
        if(response.status<200||response.status>=400)break;
        const next=response.location||pageRedirect(response.body);
        if(next){url=cleanUrl(next,url.href);continue;}
        if(PROBE_HOSTS.has(url.hostname))break;
        last=identifyPage(response.body,url.href);
        if(last.engine==='srun'&&!last.acId)last.acId=parseAcId(response.body)||observedAcId;
        if(!last.recognized){
          const scripts=[...response.body.matchAll(/<script\b[^>]*src\s*=\s*["']([^"']+)["'][^>]*>/gi)].slice(0,3);
          for(const script of scripts){
            const scriptUrl=cleanUrl(script[1],url.href);if(scriptUrl.origin!==url.origin)continue;
            const code=await read(scriptUrl.href);
            if(code.status===200&&/\/cgi-bin\/get_challenge/.test(code.body)&&/\/cgi-bin\/srun_portal/.test(code.body)){
              last={recognized:true,engine:'srun',portalUrl:url.origin,acId:parseAcId(url.href+' '+response.body+' '+code.body)||'',ready:true,message:'已识别认证方式'};break;
            }
          }
        }
        if(!last.recognized&&last.engine==='unknown'&&!/额外认证/.test(last.message)){
          // Anonymous read-only status probe confirms the protocol; no username or challenge request.
          const status=await read(url.origin+'/cgi-bin/rad_user_info?callback=discovery');
          if(status.status===200){try{
            const result=parseJsonp(status.body),code=result.error||result.res;
            if(['not_online_error','not_online','user_not_online'].includes(code)||(code==='ok'&&['user_name','online_ip','client_ip','sum_bytes'].some(key=>Object.hasOwn(result,key))))
              last={recognized:true,engine:'srun',portalUrl:url.origin,acId:parseAcId(url.href+' '+response.body)||'',ready:true,message:'已识别认证方式'};
          }catch{}}
        }
        if(last.recognized||input)return {...last,wifiProfile,wifiSsid};
        break;
      }
    }catch{/* Avoid returning URLs or server contents in errors. */}
  }
  return {...last,wifiProfile,wifiSsid};
}
module.exports={discoverNetwork,identifyPage,cleanUrl,pageRedirect};
async function main(){
  let input=process.argv[2]||'';
  if(input==='--stdin'){
    input='';process.stdin.setEncoding('utf8');
    for await(const chunk of process.stdin){input+=chunk;if(input.length>8192)throw Error('Input too long');}
  }
  return discoverNetwork(input);
}
if(require.main===module)main().then(result=>process.stdout.write(JSON.stringify(result))).catch(()=>{
  process.stdout.write(JSON.stringify({recognized:false,engine:'unknown',message:'无法读取认证页面，请检查网络或网址'}));
});

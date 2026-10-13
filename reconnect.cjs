'use strict';
// Local network recovery worker.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const run = promisify(execFile);
function networkProfile(config) {
  let url;
  try { url = new URL(config.portalUrl); } catch { throw new Error('请填写认证地址'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error('认证地址格式无效');
  const acid = String(config.acId || '');
  if (acid && !/^\d{1,6}$/.test(acid)) throw new Error('认证编号无效');
  return { portal: url.origin, acid: acid || null };
}
const ALPHA = 'LVoJPiCN2R8G90yg+hmFHuacZ1OWMnrsSTXkYpUq/3dlbfKwv6xztjI7DeBE45QA';
const NORMAL = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const sleep = ms => new Promise(r => setTimeout(r, ms));

function xEncode(str, key) {
  if (!str) return '';
  function words(s, length) {
    const v = [];
    for (let i = 0; i < s.length; i += 4)
      v.push(s.charCodeAt(i) | s.charCodeAt(i + 1) << 8 | s.charCodeAt(i + 2) << 16 | s.charCodeAt(i + 3) << 24);
    if (length) v.push(s.length);
    return v;
  }
  const v = words(str, true), k = words(key, false);
  while (k.length < 4) k.push(0);
  const n = v.length - 1;
  let z = v[n], y = v[0], d = 0, q = Math.floor(6 + 52 / (n + 1));
  while (q-- > 0) {
    d = (d + 0x9e3779b9) | 0;
    const e = d >>> 2 & 3;
    for (let p = 0; p <= n; p++) {
      y = v[p < n ? p + 1 : 0];
      let m = z >>> 5 ^ y << 2;
      m += (y >>> 3 ^ z << 4) ^ (d ^ y);
      m += k[(p & 3) ^ e] ^ z;
      z = v[p] = (v[p] + m) | 0;
    }
  }
  return v.map(a => String.fromCharCode(a & 255, a >>> 8 & 255, a >>> 16 & 255, a >>> 24 & 255)).join('');
}

function loginParams(username, password, token, ip, acid) {
  if (!/^\d{1,6}$/.test(String(acid))) throw new Error('认证编号无效');
  const raw = JSON.stringify({ username, password, ip, acid: String(acid), enc_ver: 'srun_bx1' });
  const encoded = Buffer.from(xEncode(raw, token), 'latin1').toString('base64')
    .replace(/[A-Za-z0-9+/]/g, c => ALPHA[NORMAL.indexOf(c)]);
  const info = '{SRBX1}' + encoded;
  const hmd5 = crypto.createHmac('md5', token).update(password).digest('hex');
  const sum = [username, hmd5, String(acid), ip, '200', '1', info].map(v => token + v).join('');
  return { action: 'login', username, password: '{MD5}' + hmd5, ac_id: String(acid), ip,
    chksum: crypto.createHash('sha1').update(sum).digest('hex'), info, n: '200', type: '1',
    os: 'Windows 10', name: 'Windows', double_stack: '0' };
}

function parseJsonp(body) {
  const s = body.trim();
  if (s.startsWith('{')) return JSON.parse(s);
  const m = s.match(/^[\w$.]+\s*\(([\s\S]*)\)\s*;?$/);
  if (!m) throw new Error('认证服务器返回了无法识别的内容');
  return JSON.parse(m[1]); // Never execute a JSONP response.
}

function request(url, timeout = 7000, options = {}) {
  return new Promise((resolve, reject) => {
    const transport = url.startsWith('https:') ? https : http;
    const req = transport.request(url, { method: options.method || 'GET', headers: { 'User-Agent': 'CampusReconnect/1.0', 'Cache-Control': 'no-cache', ...options.headers } }, res => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', chunk => {
        body += chunk;
        if (body.length > 1024 * 1024) req.destroy(new Error('服务器返回内容过大'));
      });
      res.on('end', () => resolve({ status: res.statusCode, body, location: res.headers.location || '' }));
      res.on('error', () => reject(new Error('网络响应中断')));
    });
    req.setTimeout(timeout, () => req.destroy(new Error('网络请求超时')));
    // Do not include request URLs, which can contain authentication material, in errors.
    req.on('error', () => reject(new Error('无法连接网络或认证服务器')));
    req.end(options.body);
  });
}

async function api(endpoint, params = {}, portal) {
  const query = new URLSearchParams({ ...params, callback: 'campusCallback', _: String(Date.now()) });
  const r = await request(portal + endpoint + '?' + query);
  if (r.status !== 200) throw new Error('认证服务器暂时不可用');
  return parseJsonp(r.body);
}
function parseAcId(text) {
  const match = text.match(/(?:[?&]|&amp;)ac_id=(\d{1,6})(?!\d)/i) ||
    text.match(/\b(?:ac_id|acid)\s*[:=]\s*["']?(\d{1,6})(?!\d)/i);
  return match ? match[1] : null;
}
async function discoverAcId(profile, fetch = request) {
  if (profile.acid) return profile.acid;
  let url = profile.portal + '/';
  for (let attempt = 0; attempt < 3; attempt++) {
    const response = await fetch(url);
    if (response.status < 200 || response.status >= 400) throw new Error('无法读取认证页面');
    const acid = parseAcId(url + ' ' + response.location + ' ' + response.body);
    if (response.location) {
      const next = new URL(response.location, url);
      // Never follow redirects to an unrelated host or send credentials to it.
      if (next.origin !== new URL(profile.portal).origin) throw new Error('认证入口已变化，请重新配置');
      if (acid) return acid;
      url = next.href;
    } else {
      if (acid) return acid;
      break;
    }
  }
  throw new Error('无法确认认证参数，请检查网络配置');
}

async function internet() {
  const checks = [
    ['http://www.msftconnecttest.com/connecttest.txt', r => r.status === 200 && r.body.trim() === 'Microsoft Connect Test'],
    ['https://www.baidu.com/', r => r.status === 200 && /baidu/i.test(r.body)]
  ];
  const results = await Promise.all(checks.map(async ([u, valid]) => {
    try { return valid(await request(u, 5000)); } catch { return false; }
  }));
  return results.some(Boolean);
}

async function wifiState() {
  const { stdout } = await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    '[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); netsh wlan show interfaces'],
  { windowsHide: true, timeout: 10000, encoding: 'utf8' });
  const ssid = stdout.match(/^\s*SSID\s*:\s*(.+)\r?$/m)?.[1]?.trim();
  const profile = stdout.match(/^\s*(?:Profile|配置文件)\s*:\s*(.+)\r?$/im)?.[1]?.trim() || '';
  const disconnected = /(?:State|状态)\s*:\s*(?:disconnected|已断开连接|断开连接)/i.test(stdout);
  const authentication = stdout.match(/^\s*(?:Authentication|身份验证|验证)\s*:\s*(.+)\r?$/im)?.[1]?.trim() || '';
  const enterprise = /WPA(?:2|3)?[\s-]*(?:Enterprise|企业)|802\.1X/i.test(authentication);
  return { ssid, profile, disconnected, known: Boolean(ssid || disconnected), enterprise };
}

async function connectWifi(profile) {
  return run('netsh.exe', ['wlan', 'connect', 'name=' + profile], { windowsHide: true, timeout: 10000 });
}

function responseCode(response) { return response.error || response.res; }
function isOnline(response) { return responseCode(response) === 'ok'; }
function isOffline(response) {
  // Unknown server failures must not trigger repeated authentication attempts.
  return ['not_online', 'not_online_error', 'user_not_online'].includes(responseCode(response));
}

function stateWriter(directory) {
  return (message, state = 'waiting') => {
    const record = { message, state, pid: process.pid, updated: new Date().toISOString() };
    fs.writeFileSync(path.join(directory, 'status.json'), JSON.stringify(record), 'utf8');
    const log = path.join(directory, 'activity.log');
    if (fs.existsSync(log) && fs.statSync(log).size > 300000) fs.writeFileSync(log, '', 'utf8');
    fs.appendFileSync(log, `${record.updated} ${state} ${message}\n`, 'utf8');
  };
}

async function tick(config, password, write, deps = {}) {
  const profile = networkProfile(config);
  const expectedSsid = config.wifiSsid || config.wifiProfile;
  const online = deps.internet || internet, portalApi = deps.api || ((endpoint, params) => api(endpoint, params, profile.portal));
  const wifi = deps.wifiState || wifiState, connect = deps.connectWifi || connectWifi;
  const wait = deps.sleep || sleep;
  if (await online()) { write('网络正常，继续监测', 'online'); return 30000; }
  await wait(3000);
  if (await online()) { write('短暂网络波动已恢复', 'online'); return 30000; }
  if (config.wifiProfile) {
    let w;
    try { w = await wifi(); } catch { w = { known: false }; }
    if (w.ssid && w.ssid !== expectedSsid) {
      write('当前连接其他 Wi-Fi，暂停校园网认证', 'waiting'); return 60000;
    }
    if (w.disconnected) {
      write('Wi-Fi 已断开，尝试连接保存的校园网络', 'connecting');
      try { await connect(config.wifiProfile); } catch {
        write('Wi-Fi 连接失败，请检查校园网络是否在附近', 'waiting'); return 120000;
      }
      await wait(8000);
      // Do not send credentials until the chosen Wi-Fi is actually connected.
      try { w = await wifi(); } catch { w = { known: false }; }
      if (w.ssid !== expectedSsid) {
        write('尚未连上校园 Wi-Fi，稍后重试', 'waiting'); return 60000;
      }
    } else if (!w.known) {
      write('无法读取 Wi-Fi 状态；可检查 Windows 的位置权限，或清空 Wi-Fi 名称仅做认证', 'waiting');
      return 120000;
    }
  }
  let status;
  try { status = await portalApi('/cgi-bin/rad_user_info'); } catch {
    write('校园认证服务器不可达，等待网络恢复', 'waiting'); return 60000;
  }
  if (isOnline(status)) {
    write('校园认证仍有效，但外网检测失败；暂不重复登录', 'waiting'); return 60000;
  }
  if (!isOffline(status)) {
    write('认证状态暂时无法确认，稍后检查', 'waiting'); return 120000;
  }
  let acid;
  try { acid = await (deps.discoverAcId || discoverAcId)(profile); } catch {
    write('无法确认认证参数，请检查本机网络配置', 'waiting'); return 120000;
  }
  write('确认认证已掉线，正在重新登录', 'authenticating');
  const challenge = await portalApi('/cgi-bin/get_challenge', { username: config.username, ip: '' });
  if (responseCode(challenge) !== 'ok' || !challenge.challenge || !challenge.client_ip) {
    write('未取得有效认证参数，稍后重试', 'waiting'); return 120000;
  }
  const result = await portalApi('/cgi-bin/srun_portal',
    loginParams(config.username, password, challenge.challenge, challenge.client_ip, acid));
  if (responseCode(result) !== 'ok') {
    // Stop after rejected credentials/policy instead of retrying until account lockout.
    write('认证被拒绝，请检查账号密码或账号限制。已暂停自动登录', 'blocked');
    return -1;
  }
  await wait(3000);
  if (await online()) { write('重新认证成功，网络已恢复', 'online'); return 30000; }
  const verify = await portalApi('/cgi-bin/rad_user_info');
  write(isOnline(verify) ? '认证成功，等待外网恢复' : '认证返回成功但状态未确认，稍后检查', 'waiting');
  return 60000;
}

async function restoreLink(config, write, deps = {}) {
  const wifi = deps.wifiState || wifiState, connect = deps.connectWifi || connectWifi, wait = deps.sleep || sleep;
  if (!config.wifiProfile) return true; // Wired mode: leave the link to Windows.
  const expectedSsid = config.wifiSsid || config.wifiProfile;
  let state;
  try { state = await wifi(); } catch { write('无法读取已保存网络的状态', 'waiting'); return false; }
  if (state.ssid && state.ssid !== expectedSsid) { write('当前连接其他网络，等待用户切换', 'waiting'); return false; }
  if (state.disconnected) {
    try { await connect(config.wifiProfile); await wait(8000); state = await wifi(); }
    catch { write('已保存网络暂时无法连接', 'waiting'); return false; }
  }
  if (state.ssid !== expectedSsid) { write('尚未连上指定网络', 'waiting'); return false; }
  return true;
}
async function systemTick(config, password, write, deps = {}) {
  if (!config.wifiProfile) { write('系统网络恢复需要填写已保存的 Wi-Fi 名称', 'blocked'); return -1; }
  if (!await restoreLink(config, write, deps)) return 60000;
  const online = await (deps.internet || internet)();
  write(online ? '网络已恢复' : '网络已连接，可能需要手动认证', online ? 'online' : 'waiting');
  return online ? 30000 : 60000;
}
async function formTick(config, password, write, deps = {}) {
  const profile = networkProfile(config), online = deps.internet || internet;
  const marker = String(config.formSuccessMarker || '');
  const userField = String(config.formUsernameField || ''), passwordField = String(config.formPasswordField || '');
  const loginPath = String(config.formLoginPath || '');
  if (!marker || !userField || !passwordField || userField === passwordField || !loginPath.startsWith('/') || loginPath.startsWith('//')) {
    write('请填写完整的表单字段映射和成功标记', 'blocked'); return -1;
  }
  const target = new URL(loginPath, profile.portal);
  if (target.origin !== profile.portal || target.search || target.hash || target.username || target.password) {
    write('表单路径只允许当前服务器内的路径', 'blocked'); return -1;
  }
  if (await online()) { write('网络正常', 'online'); return 30000; }
  await (deps.sleep || sleep)(3000);
  if (await online()) { write('短暂网络波动已恢复', 'online'); return 30000; }
  if (!await restoreLink(config, write, deps)) return 60000;
  // A form may require CSRF, cookies, captcha or extra parameters. Such flows
  // need another provider; this engine deliberately supports only a simple POST.
  const fetch = deps.request || request;
  const body = new URLSearchParams({[userField]: config.username, [passwordField]: password}).toString();
  const response = await fetch(target.href, 7000, {method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','Content-Length':Buffer.byteLength(body)},body});
  if (response.status !== 200 || response.location || !response.body.includes(marker)) {
    write('表单响应未确认成功，已暂停自动认证', 'blocked'); return -1;
  }
  const recovered = await online();
  write(recovered ? '认证完成，网络已恢复' : '收到成功标记，但网络未恢复；已暂停，请检查', recovered ? 'online' : 'blocked');
  return recovered ? 30000 : -1;
}
const ENGINES = Object.freeze({srun:tick,form:formTick,system:systemTick});
async function runEngine(config, password, write, deps = {}) {
  const id = config.engine || 'srun';
  if (!Object.hasOwn(ENGINES,id)) { write('检测引擎无法识别，请修改本机配置', 'blocked'); return -1; }
  return ENGINES[id](config,password,write,deps);
}

async function main() {
  if (process.argv.includes('--diagnose')) {
    console.log('请通过本机配置窗口和立即检测进行诊断');
    return;
  }
  const directory = process.argv[2];
  if (!directory || process.platform !== 'win32') throw new Error('请通过本地启动窗口运行');
  const configFile = path.join(directory, 'config.json');
  const lockFile = path.join(directory, 'worker.lock');
  const stopFile = path.join(directory, 'stop');
  let fd;
  try { fd = fs.openSync(lockFile, 'wx'); } catch (e) {
    if (e.code !== 'EEXIST') throw e;
    let alive = false;
    try { process.kill(Number(fs.readFileSync(lockFile, 'utf8')), 0); alive = true; } catch {}
    if (alive) return;
    fs.unlinkSync(lockFile);
    fd = fs.openSync(lockFile, 'wx');
  }
  fs.writeFileSync(fd, String(process.pid)); fs.closeSync(fd);
  const cleanup = () => { try { fs.unlinkSync(lockFile); } catch {} };
  process.on('exit', cleanup);
  fs.rmSync(stopFile, { force: true });
  const write = stateWriter(directory);
  try {
    const config = JSON.parse(fs.readFileSync(configFile, 'utf8').replace(/^\uFEFF/, ''));
    let password = '';
    if (config.engine !== 'system') {
      const { stdout } = await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        path.join(__dirname, 'decrypt.ps1'), '-ConfigFile', configFile],
      { windowsHide: true, timeout: 10000, encoding: 'utf8' });
      password = JSON.parse(stdout.replace(/^\uFEFF/, '').trim()).password;
      if (!config.username || !password) throw new Error('请先保存账号密码');
    }
    write('自动重连已启动', 'waiting');
    // One detection only. The native tray app owns the selected hourly timer, so Node
    // does not stay resident or wake every second while the app is idle.
    if (!fs.existsSync(stopFile)) {
      try { await runEngine(config, password, write); }
      catch { write('网络通信失败，等待下一次检测或手动重试', 'waiting'); }
    }
  } catch {
    write('启动失败，请重新保存配置；加密密码只能由保存它的 Windows 用户使用', 'blocked');
  }
}
module.exports = { xEncode, loginParams, parseJsonp, tick, isOnline, isOffline, networkProfile, parseAcId, discoverAcId, runEngine, request, wifiState };
if (require.main === module) main().catch(() => { console.error('本地工具启动失败'); process.exitCode = 1; });

# WIFI-EDU-Reconnection

**校园网本地重连程序，专为校园网断连而生。**

用于减少校园网掉线后手动连接、打开认证网页的重复操作。程序在 Windows 托盘中运行，按设定周期检查网络，并在电脑从休眠恢复后额外检查；能确认认证方式与参数时，尝试恢复连接或重新认证。

## 主要功能

- **一键配置**：读取当前 Wi-Fi 和认证跳转页面，填写能确认的网络参数。
- **网址识别**：粘贴认证网址后自动判断处理方式，无需选择引擎。
- **定时检查**：提供 6、4、2 小时三个检测间隔，也可随时立即检测。
- **托管模式**：勾选后自动切换检测模式，取消后恢复手动选择。
- **断线重连**：手动重新连接已保存的校园 Wi-Fi，并按需认证。
- **连接渠道**：选择有线或无线，检测与认证绑定所选渠道；无法确认连接时停止操作。
- **唤醒检查**：休眠恢复后等待网络就绪，再进行有限次数的检查。
- **账号管理**：保存多组账号，编辑、切换和设置默认账号，密码在本机加密保存。
- **后台运行**：关闭窗口后留在托盘，可暂停或退出，开机启动由用户选择。

无法确认认证方式，或需要验证码、扫码等额外步骤时，会提示用户处理。

## 使用

完整解压 Windows x64 包，运行 `CampusReconnect.exe`。

1. 主界面点击“网络配置”。
2. 点击“一键配置”，或粘贴认证网址等待自动识别。检查自动填写的参数后保存。
3. 网页认证引擎需要填写自己的账号密码；系统网络恢复使用 Windows 已保存的 Wi-Fi 配置。
4. 点击“保存并启用”。关闭窗口后仍在托盘运行，右键可立即检测、暂停或退出。

## 三种内置引擎

引擎由识别结果自动设置，界面无需手动选择。

- **加密网页认证**：挑战值与加密参数流程。认证地址由用户填写，认证编号可手动填写或自动获取。协议细节是 SRun CGI 示例，不代表能自动识别所有认证系统。
- **表单网页认证**：用户定义提交路径、账号和密码字段名、明确的成功标记，执行简单 POST。暂不处理验证码、短信、扫码、CSRF、Cookie 登录会话或多步表单；这类流程需要另写引擎。
- **系统网络恢复**：连接 Windows 已保存的 Wi-Fi，不提交网页账号密码；如仍需网页认证，会提示用户处理。

正常联网时不会强制注销。表单响应未确认成功、凭据被拒绝或出现未知引擎时暂停自动认证，不无限重试。

## 调度与账号

- 轻量模式每 6 小时、待机模式每 4 小时、夜间模式每 2 小时检查。夜间模式表示频率，全天有效。
- 唤醒后等待 20 秒，失败时每隔 60 秒额外检查，最多 3 次；已暂停时不自动恢复。
- 内置引擎仅在检测时运行，Node 不常驻；托盘显示最近一次检测结果。
- 支持多账号新增、编辑、切换、删除与设置默认账号。密码由 Windows 当前用户 DPAPI 加密保存。
- 修改网络参数后暂停检测，需要用户重新启用；开机启动默认关闭。

## 配置与安全边界

个人配置位于 `%LOCALAPPDATA%\CampusReconnect`。源码与发行包不附带这些配置、运行日志、账号密码、Cookie、Token 或实际内网网关。示例和测试只使用合成凭据、保留示例域名、文档 IP 与本机回环地址。

认证地址可粘贴完整浏览器网址；保存时只保留服务器地址，并可提取认证编号。拒绝地址中的账号密码，拒绝跨服务器认证跳转。HTTPS 保留证书验证；HTTP 的传输安全取决于网络部署，DPAPI 只保护本机密码存储。

真实掉线与休眠恢复仍需在具体网络中验证。

## 构建与检查

Windows 10/11 x64，.NET Framework 4.8 与 Windows PowerShell；运行包附带 Node.js。高 DPI 缩放配置随程序提供。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-app.ps1
node .\tests\verify.cjs
node .\tests\discovery.cjs
node .\tests\manual-reconnect.cjs
node .\tests\channel.cjs
Start-Process .\CampusReconnect.exe -ArgumentList '--self-test', "$PWD\native-check.txt" -Wait
Get-Content .\native-check.txt
```

## 文件

- `CampusApp.cs`：主界面、网络配置窗口、账号、托盘与调度。
- `reconnect.cjs`：引擎注册、配置校验、网络恢复和认证请求。
- `discovery.cjs`：Wi-Fi 与认证入口发现、只读协议识别和字段提取。
- `channel.cjs`、`network-channel.ps1`：有线/无线网卡识别、来源绑定与路由校验。
- `decrypt.ps1`：当前用户的 DPAPI 解密。
- `tests/`：合成参数向量、模拟恢复与本机 HTTP 测试。
- `app.config`、`app.manifest`：高 DPI 与 Windows 显示配置。
- `assets/app-icon.png`、`build-icon.ps1`：图标源文件与多尺寸 ICO 转换。

详见 [使用说明](docs/usage.md)、[引擎扩展](docs/engines.md)、[本地检查记录](docs/security-check.md)。

## 许可

项目使用 MIT 许可。第三方声明见 `THIRD_PARTY_NOTICES.txt`，运行包的 Node.js 许可位于 `licenses/Node.js-LICENSE.txt`。

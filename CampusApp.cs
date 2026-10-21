using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CampusReconnect {
    static class Program {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string title);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [STAThread]
        static void Main(string[] args) {
            try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--check-clean-start") {
                string isolated = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "clean-start-" + Guid.NewGuid().ToString("N"));
                try {
                    using (TrayForm clean = new TrayForm(false, isolated)) {
                        if (!clean.IsCleanFirstStart) throw new Exception("First start contains credentials or enabled automation");
                        string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "node.exe");
                        if (!File.Exists(runtime)) throw new Exception("Bundled runtime is missing");
                        clean.ShowInTaskbar = false; clean.StartPosition = FormStartPosition.Manual;
                        clean.Location = new Point(-20000, -20000); clean.Show(); Application.DoEvents();
                        using (Bitmap preview = new Bitmap(clean.Width, clean.Height)) {
                            clean.DrawToBitmap(preview, new Rectangle(0, 0, clean.Width, clean.Height));
                            preview.Save(Path.ChangeExtension(args[1], ".png"));
                        }
                        clean.Hide();
                        clean.SaveNetworkPreview(Path.ChangeExtension(args[1], ".network.png"));
                        File.WriteAllText(args[1], "PASS: no saved accounts or preset gateway/SSID, blank account/password, automation off, startup off, bundled runtime present", Encoding.UTF8);
                    }
                } catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex.Message, Encoding.UTF8); }
                finally { if (Directory.Exists(isolated) && Directory.GetFileSystemEntries(isolated).Length == 0) Directory.Delete(isolated); }
                return;
            }
            if (args.Length == 2 && args[0] == "--idle-test") {
                using (TrayForm probe = new TrayForm(true)) { probe.StartIdleProbe(args[1]); Application.Run(probe); }
                return;
            }
            if (args.Length == 2 && args[0] == "--self-test") {
                try {
                    string test = "Local-test-Only-123!测试";
                    string cipher = TrayForm.Protect(test);
                    byte[] bytes = new byte[cipher.Length / 2];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(cipher.Substring(i * 2, 2), 16);
                    string recovered = Encoding.Unicode.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
                    if (test != recovered || cipher.Contains(test)) throw new Exception("Encryption check failed");
                    using (TrayForm form = new TrayForm(true)) {
                        if (form.DefaultInterval != 21600000) throw new Exception("Interval check failed");
                        form.VerifyAccounts();
                        form.VerifyModes();
                        form.VerifyChannels();
                        form.VerifyNetworkConfiguration();
                        form.VerifyWakeRecovery();
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents();
                        if (!form.PowerNotificationsRegistered) throw new Exception("Windows power notification registration failed");
                        using (Bitmap preview = new Bitmap(form.Width, form.Height)) {
                            form.DrawToBitmap(preview, new Rectangle(0, 0, form.Width, form.Height));
                            preview.Save(Path.ChangeExtension(args[1], ".png"));
                        }
                        form.Hide();
                    }
                    File.WriteAllText(Path.ChangeExtension(args[1], ".cipher.json"), "{\"passwordCipher\":\"" + cipher + "\"}", Encoding.UTF8);
                    File.WriteAllText(args[1], "PASS: compact native form, 6/4/2-hour modes, editable network configuration, DPAPI, accounts, resume delay, duplicate resume, bounded retry, pause, successful recovery", Encoding.UTF8);
                } catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex.Message, Encoding.UTF8); }
                return;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\HAUTCampusReconnectNative", out created)) {
                if (!created) {
                    IntPtr window = FindWindow(null, "校园网自动重连");
                    if (window != IntPtr.Zero) PostMessage(window, 0x8017, IntPtr.Zero, IntPtr.Zero);
                    else MessageBox.Show("应用已在运行，请查看右下角托盘图标。", "校园网自动重连");
                    return;
                }
                try { Application.Run(new TrayForm(false)); }
                catch (Exception ex) { MessageBox.Show("启动失败：" + ex.Message, "校园网自动重连"); }
            }
        }
    }

    sealed class TrayForm : Form {
        readonly string appDir = AppDomain.CurrentDomain.BaseDirectory;
        readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusReconnect");
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly TextBox user = new TextBox(), password = new TextBox(), wifi = new TextBox();
        readonly ComboBox accountBox = new ComboBox();
        readonly ComboBox schoolBox = new ComboBox();
        readonly List<Dictionary<string, object>> visibleAccounts = new List<Dictionary<string, object>>();
        readonly Label networkSummary = new Label();
        string SchoolId { get { return "default"; } }
        string DefaultAccountId { get { return Get("defaultAccountId_" + SchoolId, ""); } }
        readonly CheckBox reveal = new CheckBox();
        readonly List<Dictionary<string, object>> accounts = new List<Dictionary<string, object>>();
        string editingId = "";
        bool loadingAccounts;
        readonly CheckBox startup = new CheckBox();
        readonly CheckBox managed = new CheckBox();
        readonly ComboBox mode = new ComboBox();
        readonly ComboBox channel = new ComboBox();
        readonly Label status = new Label(), schedule = new Label();
        readonly System.Windows.Forms.Timer dueTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer wakeTimer = new System.Windows.Forms.Timer();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterSuspendResumeNotification(IntPtr registration);
        IntPtr powerRegistration;
        bool wakeRecovery;
        int wakeAttempts;
        DateTime lastResume = DateTime.MinValue;
        string lastWorkerState = "waiting";
        NotifyIcon tray;
        ToolStripMenuItem trayStatus, trayDue;
        Dictionary<string, object> settings = new Dictionary<string, object>();
        Process worker;
        bool active, exiting, testMode;
        DateTime nextCheck;
        public int DefaultInterval { get { return 21600000; } }
        public bool PowerNotificationsRegistered { get { return powerRegistration != IntPtr.Zero; } }
        public bool IsCleanFirstStart {
            get { return accounts.Count == 0 && user.Text.Length == 0 && password.Text.Length == 0 &&
                !active && !startup.Checked && !managed.Checked && mode.SelectedIndex == 0 && Get("portalUrl", "") == "" &&
                Get("acId", "") == "" && wifi.Text == ""; }
        }
        string ConfigFile { get { return Path.Combine(dataDir, "config.json"); } }
        string StartupFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "校园网自动重连.lnk"); } }
        string Get(string key, string fallback) {
            object value; return settings.TryGetValue(key, out value) ? Convert.ToString(value) : fallback;
        }
        public static string Protect(string value) {
            byte[] bytes = ProtectedData.Protect(Encoding.Unicode.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        public static string Unprotect(string cipher) {
            byte[] bytes = new byte[cipher.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(cipher.Substring(i * 2, 2), 16);
            return Encoding.Unicode.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        public TrayForm(bool testing, string isolatedDirectory = null) {
            if (isolatedDirectory != null) dataDir = isolatedDirectory;
            testMode = testing;
            Text = "校园网自动重连";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(480, 538);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(244, 247, 250);
            ForeColor = Color.FromArgb(35, 48, 64);
            string iconFile = Path.Combine(appDir, "campus.ico");
            if (File.Exists(iconFile)) Icon = new Icon(iconFile);
            if (!testing) {
                Directory.CreateDirectory(dataDir);
                if (File.Exists(ConfigFile)) {
                    try { settings = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8)); }
                    catch { }
                }
            }
            Label heading = new Label { Text = "校园网本地重连程序", Location = new Point(20, 14), Size = new Size(440, 29), Font = new Font(Font.FontFamily, 15, FontStyle.Bold) };
            Controls.Add(heading);
            AddLabel("专为校园网断连而生", 20, 46, 440);
            AddLabel("当前配置", 20, 79, 76);
            schoolBox.Location = new Point(100, 76); schoolBox.Size = new Size(164, 28);
            schoolBox.DropDownStyle = ComboBoxStyle.DropDownList;
            schoolBox.Items.Add(Get("profileName", "我的校园网"));
            schoolBox.SelectedIndex = 0; Controls.Add(schoolBox);
            AddLabel("已存账号", 32, 133, 76);
            accountBox.Location = new Point(116, 130); accountBox.Size = new Size(332, 28);
            accountBox.DropDownStyle = ComboBoxStyle.DropDownList; Controls.Add(accountBox);
            AddAccountButton("新增", 116, delegate { NewAccount(); });
            AddAccountButton("删除", 230, delegate { DeleteAccount(); });
            AddAccountButton("设为默认", 344, delegate { MakeDefault(); });
            AddLabel("账号", 32, 197, 76); AddBox(user, 116, 194);
            user.Text = Get("username", "");
            AddLabel("密码", 32, 231, 76); AddBox(password, 116, 228);
            password.UseSystemPasswordChar = true;
            AddLabel("已存密码可留空", 116, 258, 220);
            reveal.Text = "显示密码"; reveal.Location = new Point(346, 257); reveal.Size = new Size(102, 25);
            reveal.CheckedChanged += delegate { password.UseSystemPasswordChar = !reveal.Checked; }; Controls.Add(reveal);
            AddLabel("Wi-Fi", 32, 325, 76); AddBox(wifi, 116, 322);
            wifi.Text = Get("wifiProfile", "");
            wifi.Width = 188;
            channel.Location = new Point(314, 322); channel.Size = new Size(134, 28);
            channel.DropDownStyle = ComboBoxStyle.DropDownList; channel.Items.AddRange(new object[] { "无线", "有线" });
            channel.SelectedIndex = Get("channel", "wireless") == "wired" ? 1 : 0;
            wifi.Enabled = channel.SelectedIndex == 0; Controls.Add(channel);
            channel.SelectedIndexChanged += delegate {
                wifi.Enabled = channel.SelectedIndex == 0;
                active = false; wakeRecovery = false; StopOwnedWorker(); ArmTimer();
                SetStatus("渠道已修改，请保存并启用", "idle");
            };
            Button networkButton = new Button { Text = "网络配置", Location = new Point(368, 74), Size = new Size(92, 30) };
            StyleButton(networkButton, false); networkButton.Click += delegate { OpenNetworkConfiguration(); }; Controls.Add(networkButton);
            Button detectButton = new Button { Text = "一键配置", Location = new Point(276, 74), Size = new Size(84, 30) };
            StyleButton(detectButton, false); detectButton.Click += delegate { OpenNetworkConfiguration(true); }; Controls.Add(detectButton);
            networkSummary.Location = new Point(100, 103); networkSummary.Size = new Size(360, 19);
            networkSummary.Font = new Font(Font.FontFamily, 8.5f);
            networkSummary.ForeColor = Color.FromArgb(91, 106, 124); Controls.Add(networkSummary);
            MigrateNetworkConfiguration(); UpdateNetworkSummary();
            AddLabel("检测模式", 32, 362, 76);
            mode.Location = new Point(116, 359); mode.Size = new Size(332, 28);
            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Items.AddRange(new object[] { "轻量模式 · 6 小时一次", "待机模式 · 4 小时一次", "夜间模式 · 2 小时一次" });
            mode.SelectedIndex = Get("mode", "light") == "standby" ? 1 : Get("mode", "light") == "night" ? 2 : 0;
            Controls.Add(mode);
            startup.Text = "开机自动启动";
            startup.Location = new Point(32, 395); startup.Size = new Size(200, 26);
            startup.Checked = !testing && File.Exists(StartupFile); Controls.Add(startup);
            managed.Text = "托管模式"; managed.Location = new Point(270, 395); managed.Size = new Size(178, 26);
            managed.Checked = Get("managed", "False").Equals("True", StringComparison.OrdinalIgnoreCase);
            Controls.Add(managed); UpdateModeDisplay(DateTime.Now);
            managed.CheckedChanged += delegate {
                settings["managed"] = managed.Checked;
                UpdateModeDisplay(DateTime.Now);
                if (!testMode) Persist();
                ArmTimer();
            };
            mode.SelectedIndexChanged += delegate {
                if (managed.Checked) return;
                settings["mode"] = mode.SelectedIndex == 1 ? "standby" : mode.SelectedIndex == 2 ? "night" : "light";
                if (!testMode) Persist();
                ArmTimer();
            };
            status.Location = new Point(32, 437); status.Size = new Size(416, 24); status.AutoEllipsis = true;
            status.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            status.Text = "尚未启用"; Controls.Add(status);
            schedule.Location = new Point(32, 461); schedule.Size = new Size(416, 20); schedule.Font = new Font(Font.FontFamily, 8.5f);
            schedule.Text = "唤醒后额外检查"; schedule.ForeColor = Color.FromArgb(91, 106, 124); Controls.Add(schedule);
            AddButton("保存并启用", 20, delegate { SaveAndEnable(); });
            AddButton("立即检测", 208, delegate { CheckNow(); });
            AddButton("暂停", 348, delegate { Pause(); });
            Button reconnectButton = new Button { Text = "断线重连", Location = new Point(314, 430), Size = new Size(134, 30) };
            StyleButton(reconnectButton, false); reconnectButton.Click += delegate { ManualReconnect(); }; Controls.Add(reconnectButton);
            status.Width = 274;
            foreach (Control control in Controls) {
                if (control is Label || control is CheckBox) control.BackColor = Color.Transparent;
            }
            DoubleBuffered = true;
            LoadAccounts();
            accountBox.SelectedIndexChanged += delegate { SelectAccount(); };
            RefreshAccounts(Get("defaultAccountId_" + SchoolId, Get("defaultAccountId", Get("selectedAccountId", ""))));
            dueTimer.Tick += delegate {
                if (managed.Checked && DateTime.Now < nextCheck) ArmTimer(true);
                else CheckNow();
            };
            progressTimer.Interval = 1000; progressTimer.Tick += delegate { ReadProgress(); };
            wakeTimer.Tick += delegate {
                wakeTimer.Stop();
                if (active && wakeRecovery && !exiting) CheckNow();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!exiting && !testMode) { e.Cancel = true; Hide(); }
            };
            if (!testing) {
                MakeTray();
                if (Get("enabled", "False").Equals("True", StringComparison.OrdinalIgnoreCase) &&
                    (Get("engine", "srun") == "system" || (Get("passwordCipher", "") != "" && Get("portalUrl", "") != ""))) {
                    active = true; ArmTimer();
                    SetStatus("后台待机，等待下一次检测", "idle");
                    Shown += delegate { Hide(); };
                }
            }
            AutoScaleDimensions = new SizeF(96, 96); PerformAutoScale();
        }
        void AddLabel(string text, int x, int y, int width) {
            Label label = new Label(); label.Text = text; label.Location = new Point(x, y);
            label.Size = new Size(width, 25); Controls.Add(label);
        }
        void AddBox(TextBox box, int x, int y) { box.Location = new Point(x, y); box.Width = 332; Controls.Add(box); }
        void AddAccountButton(string text, int x, EventHandler action) {
            Button button = new Button(); button.Text = text; button.Location = new Point(x, 161);
            button.Size = new Size(104, 27); StyleButton(button, false); button.Click += action; Controls.Add(button);
        }
        void LoadAccounts() {
            object list;
            if (settings.TryGetValue("accounts", out list) && list is System.Collections.IEnumerable) {
                foreach (object item in (System.Collections.IEnumerable)list) {
                    Dictionary<string, object> account = item as Dictionary<string, object>;
                    if (account != null && account.ContainsKey("id") && account.ContainsKey("username")) {
                        account["schoolId"] = "default";
                        accounts.Add(account);
                    }
                }
            }
            if (accounts.Count == 0 && Get("username", "") != "" && Get("passwordCipher", "") != "") {
                Dictionary<string, object> migrated = new Dictionary<string, object>();
                migrated["id"] = Guid.NewGuid().ToString("N"); migrated["username"] = Get("username", "");
                migrated["passwordCipher"] = Get("passwordCipher", ""); accounts.Add(migrated);
                migrated["schoolId"] = "default";
                settings["defaultAccountId"] = migrated["id"];
            }
            if (DefaultAccountId == "") settings["defaultAccountId_default"] = Get("defaultAccountId", "");
        }
        void MigrateNetworkConfiguration() {
            string portal = Get("portalUrl_" + SchoolId, Get("portalUrl", ""));
            string acid = Get("acId_" + SchoolId, Get("acId", ""));
            string legacyPortal = Get("portal", Get("gateway", ""));
            if (portal == "" && legacyPortal != "") portal = legacyPortal;
            try {
                if (portal != "") portal = NetworkSettingsDialog.NormalizePortal(portal);
            } catch { portal = ""; settings["enabled"] = false; }
            settings["portalUrl"] = portal; settings["portalUrl_" + SchoolId] = portal;
            settings["acId"] = acid; settings["acId_" + SchoolId] = acid;
            settings["wifiProfile_" + SchoolId] = wifi.Text;
            if (Get("engine", "") == "") settings["engine"] = "srun";
        }
        void UpdateNetworkSummary() {
            networkSummary.Text = Get("engine", "srun") == "system" ? "系统网络恢复" :
                Get("portalUrl", "") == "" ? "填写网络配置后启用" : "网络已配置";
        }
        void ApplyNetworkConfiguration(string portal, string acid, string wifiName, string name = "我的校园网") {
            active = false; wakeRecovery = false; StopOwnedWorker(); ArmTimer();
            settings["enabled"] = false;
            settings["portalUrl"] = portal; settings["portalUrl_" + SchoolId] = portal;
            settings["acId"] = acid; settings["acId_" + SchoolId] = acid;
            settings["wifiProfile"] = wifiName; settings["wifiProfile_" + SchoolId] = wifiName;
            settings["profileName"] = name;
            schoolBox.Items.Clear(); schoolBox.Items.Add(name); schoolBox.SelectedIndex = 0;
            wifi.Text = wifiName; UpdateNetworkSummary();
            if (!testMode) Persist();
            SetStatus("配置已保存，请重新启用", "idle");
        }
        void OpenNetworkConfiguration(bool discover = false) {
            if (WorkerRunning()) { MessageBox.Show("请等待当前检测结束后修改网络配置。", Text); return; }
            using (NetworkSettingsDialog dialog = new NetworkSettingsDialog(Convert.ToString(schoolBox.SelectedItem),
                Get("portalUrl", ""), Get("acId", ""), wifi.Text, Get("engine", "srun"),
                Get("formLoginPath", ""), Get("formUsernameField", ""), Get("formPasswordField", ""), Get("formSuccessMarker", ""), Get("wifiSsid", ""), channel.SelectedIndex == 1)) {
                if (discover) dialog.Shown += delegate { dialog.StartDiscovery(true); };
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    settings["engine"] = dialog.Engine; settings["formLoginPath"] = dialog.FormPath;
                    settings["formUsernameField"] = dialog.FormUserField; settings["formPasswordField"] = dialog.FormPasswordField;
                    settings["formSuccessMarker"] = dialog.FormSuccessMarker;
                    settings["wifiSsid"] = dialog.WifiSsid;
                    ApplyNetworkConfiguration(dialog.Portal, dialog.AcId, dialog.Wifi, dialog.ProfileName);
                }
            }
        }
        public void SaveNetworkPreview(string file) {
            using (NetworkSettingsDialog dialog = new NetworkSettingsDialog(Convert.ToString(schoolBox.SelectedItem), "", "", "")) {
                dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                dialog.Show(); Application.DoEvents();
                using (Bitmap preview = new Bitmap(dialog.Width, dialog.Height)) {
                    dialog.DrawToBitmap(preview, new Rectangle(0, 0, dialog.Width, dialog.Height)); preview.Save(file);
                }
                dialog.Hide();
            }
        }
        public void VerifyNetworkConfiguration() {
            if (NetworkSettingsDialog.NormalizePortal("http://192.0.2.1/login?ac_id=12&theme=sample") != "http://192.0.2.1")
                throw new Exception("Portal URL normalization failed");
            if (NetworkSettingsDialog.ExtractAcId("http://192.0.2.1/login?ac_id=12&theme=sample") != "12")
                throw new Exception("Portal ID extraction failed");
            foreach (string invalid in new string[] { "file:///test", "http://user:secret@example.invalid", "invalid" }) {
                bool rejected = false; try { NetworkSettingsDialog.NormalizePortal(invalid); } catch { rejected = true; }
                if (!rejected) throw new Exception("Unsafe portal URL accepted");
            }
            active = true; ApplyNetworkConfiguration("http://192.0.2.1", "12", "ExampleCampus", "Example Profile");
            if (active || Get("portalUrl", "") != "http://192.0.2.1" || Get("acId", "") != "12" || wifi.Text != "ExampleCampus" || Convert.ToString(schoolBox.SelectedItem) != "Example Profile")
                throw new Exception("Network configuration mapping or pause failed");
            ApplyNetworkConfiguration("", "", "");
            using (NetworkSettingsDialog dialog = new NetworkSettingsDialog("Example School", "", "", "")) {
                dialog.VerifyEngineLayouts();
                foreach (Control control in dialog.Controls) {
                    if (control is Panel && !control.Visible) continue;
                    if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height) throw new Exception("Configuration control outside window");
                }
            }
        }
        public void VerifyModes() {
            for (int i = 0; i < 3; i++) {
                settings["mode"] = new string[] { "light", "standby", "night" }[i];
                active = true; ArmTimer();
                if (dueTimer.Interval != new int[] { 21600000, 14400000, 7200000 }[i]) throw new Exception("Detection mode interval failed");
            }
            settings["mode"] = "light";
            DateTime day = new DateTime(2026, 1, 1);
            foreach (int hour in new int[] { 0, 5, 6, 17, 18, 23 }) {
                string expected = hour < 6 ? "night" : hour < 18 ? "light" : "standby";
                if (ManagedMode(day.AddHours(hour)) != expected || NextModeBoundary(day.AddHours(hour)) <= day.AddHours(hour))
                    throw new Exception("Managed mode boundary failed");
            }
            managed.Checked = true;
            if (mode.Enabled || !managed.Checked) throw new Exception("Managed checkbox failed");
            managed.Checked = false;
            if (!mode.Enabled || Get("mode", "") != "light") throw new Exception("Manual mode restoration failed");
            active = false; ArmTimer();
            foreach (Control control in Controls) {
                if (control.Right > ClientSize.Width || control.Bottom > ClientSize.Height) throw new Exception("Control outside window: " + control.Text);
            }
        }
        public void VerifyChannels() {
            active = true; channel.SelectedIndex = 1;
            if (wifi.Enabled || active) throw new Exception("Wired channel selection failed");
            channel.SelectedIndex = 0;
            if (!wifi.Enabled || active) throw new Exception("Wireless channel selection failed");
        }
        public void VerifyAccounts() {
            accounts.Clear();
            accounts.Add(new Dictionary<string, object> { { "id", "test-a" }, { "username", "测试账号 A" }, { "passwordCipher", Protect("Synthetic-A") } });
            accounts.Add(new Dictionary<string, object> { { "id", "test-b" }, { "username", "测试账号 B" }, { "passwordCipher", Protect("Synthetic-B") } });
            settings["defaultAccountId"] = "test-b"; settings["accounts"] = accounts;
            string saved = json.Serialize(settings);
            settings = json.Deserialize<Dictionary<string, object>>(saved);
            accounts.Clear(); LoadAccounts();
            if (accounts.Count != 2) throw new Exception("Multiple account persistence failed");
            RefreshAccounts("test-b");
            if (user.Text != "测试账号 B" || password.Text != "Synthetic-B" || !password.UseSystemPasswordChar)
                throw new Exception("Default account selection failed");
            accountBox.SelectedIndex = 0;
            if (user.Text != "测试账号 A" || password.Text != "Synthetic-A" || Get("username", "") != "测试账号 A")
                throw new Exception("Account switching failed");
            password.Text = "Synthetic-edit";
            if (password.ReadOnly || password.Text != "Synthetic-edit") throw new Exception("Password editing failed");
            RefreshAccounts("test-b");
        }
        void RefreshAccounts(string selectId) {
            loadingAccounts = true; accountBox.Items.Clear(); visibleAccounts.Clear(); int selected = -1;
            for (int i = 0; i < accounts.Count; i++) {
                if (Convert.ToString(accounts[i]["schoolId"]) != SchoolId) continue;
                visibleAccounts.Add(accounts[i]);
                string id = Convert.ToString(accounts[i]["id"]);
                accountBox.Items.Add(Convert.ToString(accounts[i]["username"]) + (id == DefaultAccountId ? "（默认）" : ""));
                if (id == selectId) selected = visibleAccounts.Count - 1;
            }
            if (selected < 0 && visibleAccounts.Count > 0) selected = 0;
            accountBox.SelectedIndex = selected; loadingAccounts = false; SelectAccount();
        }
        void SelectAccount() {
            if (loadingAccounts || accountBox.SelectedIndex < 0) return;
            Dictionary<string, object> account = visibleAccounts[accountBox.SelectedIndex];
            if (WorkerRunning()) { StopOwnedWorker(); ArmTimer(); }
            editingId = Convert.ToString(account["id"]); user.Text = Convert.ToString(account["username"]);
            try { password.Text = Unprotect(Convert.ToString(account["passwordCipher"])); }
            catch { password.Clear(); }
            settings["username"] = account["username"]; settings["passwordCipher"] = account["passwordCipher"];
            settings["selectedAccountId"] = editingId;
            if (!testMode) Persist();
        }
        void Persist() {
            settings["accounts"] = accounts;
            File.WriteAllText(ConfigFile, json.Serialize(settings), new UTF8Encoding(false));
        }
        void NewAccount() {
            if (WorkerRunning()) { MessageBox.Show("请等待当前检测完成。", Text); return; }
            loadingAccounts = true; accountBox.SelectedIndex = -1; loadingAccounts = false;
            editingId = ""; user.Clear(); password.Clear(); reveal.Checked = false;
            SetStatus("输入新账号密码，点击保存并启用", "idle"); user.Focus();
        }
        void DeleteAccount() {
            if (WorkerRunning() || accountBox.SelectedIndex < 0) return;
            if (MessageBox.Show("删除当前保存的账号？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            accounts.Remove(visibleAccounts[accountBox.SelectedIndex]);
            visibleAccounts.RemoveAt(accountBox.SelectedIndex);
            if (editingId == DefaultAccountId) settings["defaultAccountId_" + SchoolId] = visibleAccounts.Count > 0 ? visibleAccounts[0]["id"] : "";
            if (visibleAccounts.Count == 0) {
                Pause(); settings["username"] = ""; settings["passwordCipher"] = "";
                settings["selectedAccountId"] = ""; user.Clear(); password.Clear(); editingId = "";
            }
            RefreshAccounts(DefaultAccountId); Persist();
        }
        void MakeDefault() {
            if (accountBox.SelectedIndex < 0) { MessageBox.Show("请先保存这个账号。", Text); return; }
            settings["defaultAccountId_" + SchoolId] = editingId; Persist(); RefreshAccounts(editingId);
            SetStatus("已设为默认账号，下次打开时自动选中", "idle");
        }
        void AddButton(string text, int x, EventHandler handler) {
            Button button = new Button(); button.Text = text; button.Location = new Point(x, 490);
            button.Size = new Size(x == 20 ? 176 : x == 208 ? 128 : 112, 34); StyleButton(button, x == 20); button.Click += handler; Controls.Add(button);
        }
        void StyleButton(Button button, bool primary) {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(19, 119, 128) : Color.FromArgb(215, 223, 231);
            button.BackColor = primary ? Color.FromArgb(19, 119, 128) : Color.White;
            button.ForeColor = primary ? Color.White : ForeColor;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(16, 101, 110) : Color.FromArgb(239, 246, 247);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(11, 81, 89) : Color.FromArgb(225, 236, 240);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            float scale = ClientSize.Width / 480f;
            using (SolidBrush card = new SolidBrush(Color.White))
            using (SolidBrush state = new SolidBrush(Color.FromArgb(232, 243, 243)))
            using (Pen border = new Pen(Color.FromArgb(222, 230, 236))) {
                foreach (Rectangle box in new Rectangle[] { new Rectangle(20, 124, 440, 172), new Rectangle(20, 308, 440, 118) }) {
                    RectangleF bounds = new RectangleF(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);
                    e.Graphics.FillRectangle(card, bounds); e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                }
                e.Graphics.FillRectangle(state, 20 * scale, 431 * scale, 440 * scale, 51 * scale);
            }
        }
        void ShowSettings() { UpdateModeDisplay(DateTime.Now); Show(); WindowState = FormWindowState.Normal; Activate(); }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            powerRegistration = RegisterSuspendResumeNotification(Handle, 0);
        }
        protected override void OnHandleDestroyed(EventArgs e) {
            if (powerRegistration != IntPtr.Zero) {
                UnregisterSuspendResumeNotification(powerRegistration); powerRegistration = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x8017) ShowSettings();
            if (message.Msg == 0x218) {
                int powerEvent = message.WParam.ToInt32();
                if (powerEvent == 4) {
                    StopOwnedWorker(); wakeRecovery = false; wakeAttempts = 0; lastResume = DateTime.MinValue;
                    if (active) SetStatus("电脑进入休眠，唤醒后再检查", "idle");
                } else if (powerEvent == 18 || powerEvent == 7) OnResume();
            }
            base.WndProc(ref message);
        }
        void OnResume() {
            if (!active || exiting || wakeRecovery || DateTime.UtcNow - lastResume < TimeSpan.FromSeconds(30)) return;
            lastResume = DateTime.UtcNow;
            StopOwnedWorker(); wakeRecovery = true; wakeAttempts = 0;
            ScheduleWakeCheck(20000);
            SetStatus("电脑已唤醒，等待 20 秒让网络恢复", "idle");
        }
        void ScheduleWakeCheck(int delay) {
            wakeTimer.Stop(); wakeTimer.Interval = delay; wakeTimer.Start();
            schedule.Text = "唤醒后检测：" + DateTime.Now.AddMilliseconds(delay).ToString("HH:mm:ss") +
                "（第 " + (wakeAttempts + 1) + "/3 次）";
            if (trayDue != null) trayDue.Text = schedule.Text;
        }
        void FinishWakeCheck(string state) {
            if (!wakeRecovery) return;
            if (!active || state == "online" || state == "blocked" || wakeAttempts >= 3) {
                wakeRecovery = false; wakeTimer.Stop(); ArmTimer(); return;
            }
            ScheduleWakeCheck(60000);
        }
        public void VerifyWakeRecovery() {
            active = false; OnResume();
            if (wakeTimer.Enabled) throw new Exception("Paused app resumed unexpectedly");
            active = true; lastResume = DateTime.MinValue; OnResume();
            if (!wakeRecovery || wakeTimer.Interval != 20000 || wakeAttempts != 0)
                throw new Exception("Resume grace period failed");
            OnResume();
            if (wakeAttempts != 0 || !wakeTimer.Enabled) throw new Exception("Duplicate resume failed");
            wakeAttempts = 1; FinishWakeCheck("waiting");
            if (wakeTimer.Interval != 60000 || !wakeTimer.Enabled) throw new Exception("Resume retry failed");
            wakeAttempts = 3; FinishWakeCheck("waiting");
            if (wakeRecovery || wakeTimer.Enabled || dueTimer.Interval != DefaultInterval)
                throw new Exception("Bounded resume recovery failed");
            lastResume = DateTime.MinValue; OnResume(); wakeAttempts = 1; FinishWakeCheck("online");
            if (wakeRecovery || wakeTimer.Enabled) throw new Exception("Successful resume recovery failed");
            active = false; StopOwnedWorker(); ArmTimer();
            SetStatus("唤醒恢复逻辑测试通过", "idle");
        }
        public void StartIdleProbe(string output) {
            MakeTray(); active = true; ArmTimer();
            SetStatus("轻量模式待机测试", "idle");
            System.Windows.Forms.Timer finish = new System.Windows.Forms.Timer(); finish.Interval = 8000;
            TimeSpan cpuStart = TimeSpan.Zero;
            Shown += delegate { Hide(); cpuStart = Process.GetCurrentProcess().TotalProcessorTime; finish.Start(); };
            finish.Tick += delegate {
                finish.Stop();
                using (Process current = Process.GetCurrentProcess()) {
                    current.Refresh();
                    double cpu = (current.TotalProcessorTime - cpuStart).TotalMilliseconds;
                    File.WriteAllText(output, "Idle test: 8 seconds; CPU time=" + cpu.ToString("F1") + " ms; memory=" +
                        (current.WorkingSet64 / 1048576.0).ToString("F1") + " MB; resident Node workers=0; interval=" +
                        dueTimer.Interval + " ms", Encoding.UTF8);
                }
                finish.Dispose(); exiting = true; Close();
            };
        }
        void MakeTray() {
            tray = new NotifyIcon(); tray.Icon = Icon; tray.Text = "校园网自动重连";
            ContextMenuStrip menu = new ContextMenuStrip();
            trayStatus = new ToolStripMenuItem("状态：尚未启用"); trayStatus.Enabled = false; menu.Items.Add(trayStatus);
            trayDue = new ToolStripMenuItem("下次检测：尚未安排"); trayDue.Enabled = false; menu.Items.Add(trayDue);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("立即检测", null, delegate { CheckNow(); });
            menu.Items.Add("断线重连", null, delegate { ManualReconnect(); });
            menu.Items.Add("打开设置", null, delegate { ShowSettings(); });
            menu.Items.Add("暂停自动检测", null, delegate { Pause(); });
            menu.Items.Add("查看运行记录", null, delegate {
                string log = Path.Combine(dataDir, "activity.log");
                if (File.Exists(log)) Process.Start("notepad.exe", "\"" + log + "\"");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { exiting = true; StopOwnedWorker(); Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowSettings(); }; tray.Visible = true;
        }
        void SetStatus(string text, string state) {
            status.Text = text;
            status.ForeColor = state == "blocked" ? Color.FromArgb(151, 82, 19) : Color.FromArgb(16, 96, 104);
            if (tray == null) return;
            trayStatus.Text = "状态：" + text;
            tray.Text = text.Length > 60 ? text.Substring(0, 60) : text;
            tray.Icon = Icon;
        }
        static string ManagedMode(DateTime now) { return now.Hour < 6 ? "night" : now.Hour < 18 ? "light" : "standby"; }
        static DateTime NextModeBoundary(DateTime now) {
            return now.Hour < 6 ? now.Date.AddHours(6) : now.Hour < 18 ? now.Date.AddHours(18) : now.Date.AddDays(1);
        }
        static int ModeInterval(string value) { return value == "standby" ? 14400000 : value == "night" ? 7200000 : 21600000; }
        void UpdateModeDisplay(DateTime now) {
            string current = managed.Checked ? ManagedMode(now) : Get("mode", "light");
            mode.Enabled = !managed.Checked;
            mode.SelectedIndex = current == "standby" ? 1 : current == "night" ? 2 : 0;
        }
        void ArmTimer(bool keepDue = false) {
            dueTimer.Stop();
            DateTime now = DateTime.Now;
            UpdateModeDisplay(now);
            if (!active) { schedule.Text = "自动检测已暂停。"; if (trayDue != null) trayDue.Text = "下次检测：已暂停"; return; }
            int interval = ModeInterval(managed.Checked ? ManagedMode(now) : Get("mode", "light"));
            DateTime candidate = now.AddMilliseconds(interval);
            nextCheck = keepDue && nextCheck < candidate ? nextCheck : candidate;
            DateTime timerDue = managed.Checked && NextModeBoundary(now) < nextCheck ? NextModeBoundary(now) : nextCheck;
            dueTimer.Interval = Math.Max(1, (int)(timerDue - now).TotalMilliseconds);
            schedule.Text = "下次检测：" + nextCheck.ToString("MM-dd HH:mm") + "（电脑本地时间）";
            if (trayDue != null) trayDue.Text = schedule.Text;
            dueTimer.Start();
        }
        void SaveAndEnable() {
            try {
                if (WorkerRunning()) throw new Exception("当前正在检测，请等待完成后再保存。");
                settings["channel"] = channel.SelectedIndex == 1 ? "wired" : "wireless";
                settings["bindingRevision"] = Guid.NewGuid().ToString("N");
                if (wifi.Text.Trim() != Get("wifiProfile", "")) settings["wifiSsid"] = "";
                if (!managed.Checked) settings["mode"] = mode.SelectedIndex == 1 ? "standby" : mode.SelectedIndex == 2 ? "night" : "light";
                if (Get("engine", "srun") == "system") {
                    if (channel.SelectedIndex == 0 && wifi.Text.Trim() == "") throw new Exception("请填写已保存的 Wi-Fi 名称。");
                    settings["wifiProfile"] = wifi.Text.Trim(); settings["enabled"] = true;
                    Persist(); SetStartup(startup.Checked); active = true; ArmTimer(); SetStatus("已启用，后台待机", "idle"); return;
                }
                if (Get("portalUrl", "") == "") { OpenNetworkConfiguration(); return; }
                string cipher = Get("passwordCipher", "");
                if (password.Text.Length > 0) cipher = Protect(password.Text);
                else if (Get("username", "") != user.Text.Trim()) cipher = "";
                if (user.Text.Trim() == "" || cipher == "") throw new Exception("请填写校园网账号和密码。");
                if (editingId == "") editingId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> account = accounts.Find(delegate(Dictionary<string, object> a) { return Convert.ToString(a["id"]) == editingId; });
                if (account == null) { account = new Dictionary<string, object>(); account["id"] = editingId; accounts.Add(account); }
                account["username"] = user.Text.Trim(); account["passwordCipher"] = cipher;
                account["schoolId"] = SchoolId;
                if (DefaultAccountId == "") settings["defaultAccountId_" + SchoolId] = editingId;
                settings["selectedAccountId"] = editingId;
                settings["username"] = user.Text.Trim(); settings["passwordCipher"] = cipher;
                settings["schoolId"] = SchoolId;
                settings["wifiProfile"] = wifi.Text.Trim(); settings["wifiProfile_" + SchoolId] = wifi.Text.Trim();
                if (!managed.Checked) settings["mode"] = mode.SelectedIndex == 1 ? "standby" : mode.SelectedIndex == 2 ? "night" : "light";
                settings["enabled"] = true;
                Persist();
                SetStartup(startup.Checked);
                RefreshAccounts(editingId); active = true; ArmTimer();
                SetStatus("已启用，后台待机", "idle");
            } catch (Exception ex) { MessageBox.Show(ex.Message, "校园网自动重连"); }
        }
        void SetStartup(bool enabled) {
            if (!enabled) { if (File.Exists(StartupFile)) File.Delete(StartupFile); return; }
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupFile });
            Type st = shortcut.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { appDir });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "校园网自动重连" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        bool WorkerRunning() {
            try { return worker != null && !worker.HasExited; } catch { return false; }
        }
        void ManualReconnect() {
            if (WorkerRunning()) return;
            if (Get("channel", "wireless") != "wired" && Get("wifiProfile", "") == "") { MessageBox.Show("请先保存校园 Wi-Fi 名称，再使用断线重连。", Text); return; }
            wakeRecovery = false;
            CheckNow(true);
        }
        void CheckNow(bool forceReconnect = false) {
            if (WorkerRunning()) return;
            if (Get("channel", "wireless") != (channel.SelectedIndex == 1 ? "wired" : "wireless")) {
                MessageBox.Show("请先保存当前选择的连接渠道。", Text); return;
            }
            wakeTimer.Stop();
            if (Get("engine", "srun") != "system" && accountBox.SelectedIndex < 0) {
                wakeRecovery = false; ArmTimer(); ShowSettings(); MessageBox.Show("请先保存当前账号。", Text); return;
            }
            if (Get("engine", "srun") != "system" && (Get("username", "") == "" || Get("passwordCipher", "") == "")) {
                wakeRecovery = false; ArmTimer();
                ShowSettings(); MessageBox.Show("请先填写账号密码，点击保存并启用。", "校园网自动重连"); return;
            }
            if (Get("engine", "srun") != "system" && Get("portalUrl", "") == "") {
                wakeRecovery = false; Pause(); ShowSettings(); OpenNetworkConfiguration(); return;
            }
            try {
                string lockFile = Path.Combine(dataDir, "worker.lock");
                if (File.Exists(lockFile)) {
                    int pid;
                    if (Int32.TryParse(File.ReadAllText(lockFile).Trim(), out pid)) {
                        try { if (Process.GetProcessById(pid).ProcessName == "node") throw new InvalidOperationException("另一个认证程序正在运行，请先停止旧版本。"); }
                        catch (ArgumentException) { }
                    }
                }
                string nodeFile = Path.Combine(appDir, "runtime", "node.exe");
                if (!File.Exists(nodeFile)) nodeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
                if (!File.Exists(nodeFile)) nodeFile = "node.exe";
                ProcessStartInfo start = new ProcessStartInfo(nodeFile, "\"" + Path.Combine(appDir, "reconnect.cjs") + "\" \"" + dataDir + "\"" + (forceReconnect ? " --reconnect" : ""));
                start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                worker = Process.Start(start); progressTimer.Start(); lastWorkerState = "waiting";
                if (wakeRecovery) wakeAttempts++;
                SetStatus(forceReconnect ? "正在断线重连……" : "正在检测网络……", "checking");
                ArmTimer();
            } catch (Exception ex) {
                wakeRecovery = false; ArmTimer(); SetStatus("检测未启动", "blocked");
                MessageBox.Show(ex.Message, "校园网自动重连");
            }
        }
        void ReadProgress() {
            string stateFile = Path.Combine(dataDir, "status.json");
            try {
                if (File.Exists(stateFile)) {
                    Dictionary<string, object> info = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(stateFile));
                    if (worker != null && Convert.ToInt32(info["pid"]) == worker.Id) {
                        string state = Convert.ToString(info["state"]);
                        lastWorkerState = state;
                        SetStatus(Convert.ToString(info["message"]), state);
                        if (state == "blocked" && active) {
                            active = false; settings["enabled"] = false;
                            File.WriteAllText(ConfigFile, json.Serialize(settings), new UTF8Encoding(false)); ArmTimer();
                        }
                    }
                }
            } catch { }
            if (!WorkerRunning()) {
                progressTimer.Stop();
                if (worker != null) { worker.Dispose(); worker = null; }
                FinishWakeCheck(lastWorkerState);
            }
        }
        void StopOwnedWorker() {
            dueTimer.Stop(); progressTimer.Stop(); wakeTimer.Stop();
            if (WorkerRunning()) { try { worker.Kill(); worker.WaitForExit(2000); } catch { } }
            if (worker != null) { worker.Dispose(); worker = null; }
        }
        void Pause() {
            active = false; wakeRecovery = false; StopOwnedWorker(); ArmTimer(); SetStatus("已暂停自动检测", "idle");
            settings["enabled"] = false;
            if (File.Exists(ConfigFile)) File.WriteAllText(ConfigFile, json.Serialize(settings), new UTF8Encoding(false));
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                StopOwnedWorker(); dueTimer.Dispose(); progressTimer.Dispose(); wakeTimer.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
    sealed class NetworkSettingsDialog : Form {
        readonly bool wiredChannel;
        readonly TextBox portal = new TextBox(), acid = new TextBox(), wifi = new TextBox(), profileName = new TextBox();
        readonly Panel formFields = new Panel();
        readonly TextBox formPath = new TextBox(), formUser = new TextBox(), formPassword = new TextBox(), formMarker = new TextBox();
        readonly Button save, cancel, discoverButton;
        readonly Label result = new Label();
        readonly System.Windows.Forms.Timer pasteTimer = new System.Windows.Forms.Timer();
        readonly object processGate = new object();
        Process discoveryProcess;
        bool busy, filling, recognized;
        volatile bool closed;
        int revision;
        string detectedEngine = "unknown", recognizedInput = "";
        string observedWifiName = "", observedWifiSsid = "";
        public string WifiSsid { get; private set; }
        public string Engine { get; private set; }
        public string FormPath { get; private set; }
        public string FormUserField { get; private set; }
        public string FormPasswordField { get; private set; }
        public string FormSuccessMarker { get; private set; }
        public string ProfileName { get; private set; }
        public string Portal { get; private set; }
        public string AcId { get; private set; }
        public string Wifi { get; private set; }
        public static string NormalizePortal(string value) {
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo != "" || uri.Host == "")
                throw new Exception("请填写完整的 http:// 或 https:// 认证网址。");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        public static string ExtractAcId(string value) {
            Uri uri; if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)) return "";
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(uri.Query, @"(?:^|[?&])ac_id=(\d{1,6})(?:&|$)");
            return match.Success ? match.Groups[1].Value : "";
        }
        public NetworkSettingsDialog(string name, string address, string id, string wifiName, string engineId = "unknown", string postPath = "", string userKey = "", string passwordKey = "", string successMarker = "", string ssid = "", bool wired = false) {
            wiredChannel = wired;
            observedWifiName = wifiName; observedWifiSsid = ssid;
            Text = "网络配置"; AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(440, 330); Font = new Font("Microsoft YaHei UI", 9.5f); BackColor = Color.White;
            ForeColor = Color.FromArgb(35, 48, 64); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            Controls.Add(new Label { Text = "网络配置", Location = new Point(20, 14), Size = new Size(220, 29), Font = new Font(Font.FontFamily, 14, FontStyle.Bold) });
            discoverButton = new Button { Text = "一键配置", Location = new Point(302, 12), Size = new Size(118, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            discoverButton.FlatAppearance.BorderColor = Color.FromArgb(215, 223, 231);
            discoverButton.Click += delegate { StartDiscovery(true); }; Controls.Add(discoverButton);
            AddField("配置名称", profileName, 54); profileName.Text = name;
            AddField("认证网址", portal, 94); portal.Text = address;
            result.Location = new Point(112, 125); result.Size = new Size(308, 39); result.Font = new Font(Font.FontFamily, 8.5f);
            result.ForeColor = Color.FromArgb(91,106,124); result.Text = "粘贴网址后自动识别"; Controls.Add(result);
            AddField("认证编号", acid, 174); acid.Text = id;
            AddHint("留空自动获取", 202);
            AddField("Wi-Fi 名称", wifi, 234); wifi.Text = wifiName;
            wifi.Enabled = !wiredChannel;
            AddHint("有线连接可留空", 263);
            formFields.Location = new Point(20, 292); formFields.Size = new Size(400, 124);
            AddFormField("提交路径", formPath, 0); formPath.Text = postPath;
            AddFormField("账号字段名", formUser, 30); formUser.Text = userKey;
            AddFormField("密码字段名", formPassword, 60); formPassword.Text = passwordKey;
            AddFormField("成功标记", formMarker, 90); formMarker.Text = successMarker; Controls.Add(formFields);
            save = new Button { Text = "保存配置", Size = new Size(112, 32), Location = new Point(186, 302), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(19,119,128), ForeColor = Color.White };
            save.FlatAppearance.BorderSize = 0; save.Click += delegate { SaveConfiguration(); };
            cancel = new Button { Text = "取消", Size = new Size(112, 32), Location = new Point(308, 302), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(215,223,231);
            Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
            if ((engineId == "srun" || engineId == "form") && address != "") {
                recognized = true; detectedEngine = engineId; recognizedInput = address; result.Text = "网络已配置";
            } else if (engineId == "system" && wifiName != "") {
                recognized = true; detectedEngine = "system"; result.Text = "使用 Windows 已保存网络";
            }
            pasteTimer.Interval = 700; pasteTimer.Tick += delegate { pasteTimer.Stop(); StartDiscovery(false); };
            portal.TextChanged += delegate {
                if (filling) return;
                revision++; recognized = false; detectedEngine = "unknown"; StopDiscovery(); busy = false; discoverButton.Enabled = true; save.Enabled = true;
                acid.Clear(); formPath.Clear(); formUser.Clear(); formPassword.Clear(); formMarker.Clear();
                result.Text = "粘贴网址后自动识别"; pasteTimer.Stop(); UpdateEngineFields();
                Uri candidate; if (Uri.TryCreate(portal.Text.Trim(), UriKind.Absolute, out candidate) && (candidate.Scheme == "http" || candidate.Scheme == "https") && candidate.UserInfo == "") pasteTimer.Start();
            };
            AutoScaleDimensions = new SizeF(96, 96); UpdateEngineFields(); PerformAutoScale();
        }
        public async void StartDiscovery(bool oneClick) {
            if (busy || IsDisposed) return;
            string value = portal.Text.Trim();
            if (!oneClick && value == "") return;
            if (value != "") {
                if (value.Length > 4096) { result.Text = "认证网址过长，请检查粘贴内容"; return; }
                try { NormalizePortal(value); value = new Uri(value).AbsoluteUri; }
                catch { result.Text = "请输入完整的认证网址"; return; }
            }
            pasteTimer.Stop(); int stamp = ++revision; busy = true; recognized = false;
            discoverButton.Enabled = false; save.Enabled = false; result.Text = "正在读取网络与认证页面…";
            try {
                string input = value;
                Dictionary<string, object> found = await Task.Run(delegate { return ReadDiscovery(input, stamp); });
                if (IsDisposed || stamp != revision) return;
                ApplyDiscovery(found);
            } catch {
                if (!IsDisposed && stamp == revision) result.Text = "识别失败，请检查网络或粘贴认证网址";
            } finally {
                if (!IsDisposed && stamp == revision) { busy = false; discoverButton.Enabled = true; save.Enabled = true; }
            }
        }
        Dictionary<string, object> ReadDiscovery(string address, int stamp) {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string node = Path.Combine(appDir, "runtime", "node.exe");
            if (!File.Exists(node)) node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            ProcessStartInfo info = new ProcessStartInfo(node, "\"" + Path.Combine(appDir, "discovery.cjs") + "\" --stdin" + (wiredChannel ? " --wired" : " --wireless"));
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = new Process { StartInfo = info }) {
                lock (processGate) {
                    if (closed || stamp != revision) throw new Exception("Discovery cancelled"); discoveryProcess = process; process.Start();
                    byte[] inputBytes = Encoding.UTF8.GetBytes(address);
                    process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length); process.StandardInput.Close();
                }
                try {
                    if (!process.WaitForExit(60000)) { process.Kill(); process.WaitForExit(2000); throw new Exception("Discovery timeout"); }
                    string output = process.StandardOutput.ReadToEnd();
                    if (output.Length > 16384 || process.ExitCode != 0) throw new Exception("Discovery response invalid");
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output);
                } finally { lock (processGate) { if (discoveryProcess == process) discoveryProcess = null; } }
            }
        }
        static string Value(Dictionary<string, object> values, string key) { object value; return values.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        void ApplyDiscovery(Dictionary<string, object> found) {
            string kind = Value(found, "engine");
            recognized = Value(found, "recognized").Equals("True", StringComparison.OrdinalIgnoreCase) && (kind == "srun" || kind == "form" || kind == "system");
            detectedEngine = recognized ? kind : "unknown";
            filling = true;
            try {
                string address = Value(found, "portalUrl"); if (address != "" && (recognized || portal.Text == "")) portal.Text = NormalizePortal(address);
                string name = Value(found, "wifiProfile"); if (name != "") { wifi.Text = name; observedWifiName = name; observedWifiSsid = Value(found, "wifiSsid"); }
                string id = Value(found, "acId"); if (id != "") acid.Text = id;
                if (kind == "form") {
                    formPath.Text = Value(found, "formLoginPath"); formUser.Text = Value(found, "formUsernameField");
                    formPassword.Text = Value(found, "formPasswordField"); formMarker.Clear();
                }
                recognizedInput = portal.Text;
            } finally { filling = false; }
            result.Text = Value(found, "message"); UpdateEngineFields();
        }
        void SaveConfiguration() {
            if (busy) return;
            if (!recognized || (detectedEngine != "system" && recognizedInput != portal.Text)) { StartDiscovery(false); return; }
            try {
                ProfileName = profileName.Text.Trim(); if (ProfileName == "") ProfileName = "我的校园网";
                if (ProfileName.Length > 40) throw new Exception("配置名称请控制在 40 个字符以内。");
                Engine = detectedEngine; Portal = Engine == "system" ? "" : NormalizePortal(portal.Text);
                AcId = acid.Text.Trim(); if (AcId == "") AcId = ExtractAcId(portal.Text);
                if (AcId != "" && !System.Text.RegularExpressions.Regex.IsMatch(AcId, @"^\d{1,6}$")) throw new Exception("认证编号只接受 1 至 6 位数字，也可留空。");
                Wifi = wifi.Text.Trim(); if (Engine == "system" && Wifi == "" && !wiredChannel) throw new Exception("请填写已保存的 Wi-Fi 名称。");
                WifiSsid = Wifi == observedWifiName ? observedWifiSsid : "";
                FormPath = formPath.Text.Trim(); FormUserField = formUser.Text.Trim(); FormPasswordField = formPassword.Text.Trim(); FormSuccessMarker = formMarker.Text;
                if (Engine == "form") {
                    Uri target;
                    if (!FormPath.StartsWith("/") || FormPath.StartsWith("//") || !Uri.TryCreate(new Uri(Portal), FormPath, out target) || target.GetLeftPart(UriPartial.Authority) != Portal || target.Query != "" || target.Fragment != "") throw new Exception("提交路径应为当前服务器内的路径。");
                    if (FormUserField == "" || FormPasswordField == "" || FormUserField == FormPasswordField || FormSuccessMarker.Trim() == "") throw new Exception("请补充表单字段和明确的成功标记。");
                }
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { MessageBox.Show(ex.Message, Text); }
        }
        void StopDiscovery() {
            lock (processGate) {
                if (discoveryProcess != null) { try { if (!discoveryProcess.HasExited) discoveryProcess.Kill(); } catch { } }
            }
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { closed = true; revision++; StopDiscovery(); pasteTimer.Stop(); pasteTimer.Dispose(); }
            base.Dispose(disposing);
        }
        void UpdateEngineFields() {
            bool form = detectedEngine == "form", system = detectedEngine == "system";
            acid.Enabled = !system && !form; formFields.Visible = form;
            float scale = AutoScaleDimensions.Width / 96f;
            ClientSize = new Size((int)(440 * scale), (int)((form ? 470 : 344) * scale));
            save.Top = cancel.Top = (int)((form ? 426 : 302) * scale);
        }
        public void VerifyEngineLayouts() {
            foreach (string kind in new string[] { "unknown", "srun", "form", "system" }) {
                detectedEngine = kind; UpdateEngineFields();
                foreach (Control control in Controls) {
                    if (control == formFields && kind != "form") continue;
                    if (control.Right > ClientSize.Width || control.Bottom > ClientSize.Height) throw new Exception("Configuration layout overflow");
                    if (control is ComboBox) throw new Exception("Engine selector is still visible");
                }
            }
            ApplyDiscovery(new Dictionary<string, object> { {"recognized", true}, {"engine", "srun"}, {"portalUrl", "http://192.0.2.1"}, {"acId", "12"}, {"wifiProfile", "ExampleCampus"}, {"message", "Detected"} });
            if (!recognized || portal.Text != "http://192.0.2.1" || acid.Text != "12" || wifi.Text != "ExampleCampus") throw new Exception("Automatic field mapping failed");
            filling = true; portal.Clear(); acid.Clear(); wifi.Clear(); filling = false; recognized = false; detectedEngine = "unknown"; result.Text = "粘贴网址后自动识别"; UpdateEngineFields();
        }
        void AddFormField(string text, TextBox box, int y) {
            formFields.Controls.Add(new Label { Text = text, Location = new Point(0, y + 3), Size = new Size(92, 24) });
            box.Location = new Point(92, y); box.Width = 308; formFields.Controls.Add(box);
        }
        void AddField(string text, TextBox box, int y) {
            Controls.Add(new Label { Text = text, Location = new Point(20, y + 3), Size = new Size(88, 24) });
            box.Location = new Point(112, y); box.Width = 308; Controls.Add(box);
        }
        void AddHint(string text, int y) {
            Controls.Add(new Label { Text = text, Location = new Point(112, y), Size = new Size(308, 22), ForeColor = Color.FromArgb(91,106,124), Font = new Font(Font.FontFamily, 8.5f), AutoEllipsis = true });
        }
    }

}

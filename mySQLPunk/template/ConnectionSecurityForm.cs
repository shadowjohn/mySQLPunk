using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public sealed class ConnectionSecurityForm : Form
    {
        private readonly Dictionary<string, object> settings;
        private readonly string provider;
        private ComboBox tlsMode;
        private TextBox caPath;
        private TextBox clientCertificatePath;
        private TextBox clientKeyPath;
        private TextBox certificatePassword;
        private TextBox walletPath;
        private CheckBox checkRevocation;
        private CheckBox sshEnabled;
        private TextBox sshHost;
        private NumericUpDown sshPort;
        private TextBox sshUsername;
        private TextBox sshPassword;
        private TextBox privateKeyPath;
        private TextBox privateKeyPassphrase;
        private TextBox hostKeyFingerprint;

        private ConnectionSecurityForm(string providerName, Dictionary<string, object> source)
        {
            provider = ConnectionConfigurationService.NormalizeProvider(providerName);
            settings = new Dictionary<string, object>();
            if (source != null)
            {
                foreach (KeyValuePair<string, object> pair in source) settings[pair.Key] = pair.Value;
            }
            settings["db_kind"] = provider;
            ConnectionSecuritySettingsService.Normalize(settings);
            BuildUi();
            LoadValues();
            Form1.ApplyModernTheme(this);
        }

        public static bool Edit(IWin32Window owner, string provider, Dictionary<string, object> connection)
        {
            using (ConnectionSecurityForm form = new ConnectionSecurityForm(provider, connection))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                ConnectionSecuritySettingsService.Copy(form.settings, connection);
                return true;
            }
        }

        private void BuildUi()
        {
            Text = L("連線安全設定", "Connection Security");
            Icon = AppIconService.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(700, 590);
            ClientSize = new Size(720, 620);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            tabs.TabPages.Add(BuildTlsTab());
            tabs.TabPages.Add(BuildSshTab());

            Button ok = new Button { Text = L("確定", "OK"), AutoSize = true, DialogResult = DialogResult.None };
            Button cancel = new Button { Text = L("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (sender, args) => SaveAndClose();
            FlowLayoutPanel footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12, 10, 12, 8)
            };
            footer.Controls.Add(cancel);
            footer.Controls.Add(ok);

            Controls.Add(tabs);
            Controls.Add(footer);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private TabPage BuildTlsTab()
        {
            TabPage page = new TabPage("SSL / TLS");
            TableLayoutPanel table = CreateTable();
            tlsMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            tlsMode.Items.AddRange(GetTlsModes().Cast<object>().ToArray());
            AddRow(table, L("驗證模式", "Validation Mode"), tlsMode);

            caPath = AddPathRow(table, L("CA 憑證", "CA Certificate"), L("憑證檔案", "Certificate Files") + "|*.pem;*.crt;*.cer|" + L("所有檔案", "All Files") + "|*.*");
            clientCertificatePath = AddPathRow(table, L("用戶端憑證", "Client Certificate"), L("憑證檔案", "Certificate Files") + "|*.pfx;*.p12;*.pem;*.crt|" + L("所有檔案", "All Files") + "|*.*");
            clientKeyPath = AddPathRow(table, L("用戶端私鑰", "Client Private Key"), L("私鑰檔案", "Private Key Files") + "|*.key;*.pem|" + L("所有檔案", "All Files") + "|*.*");
            certificatePassword = new TextBox { Width = 340, UseSystemPasswordChar = true };
            AddRow(table, L("憑證密碼", "Certificate Password"), certificatePassword);
            walletPath = AddFolderRow(table, "Oracle Wallet");
            checkRevocation = new CheckBox { Text = L("檢查憑證撤銷狀態", "Check certificate revocation"), AutoSize = true };
            AddRow(table, string.Empty, checkRevocation);

            Label note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(570, 0),
                ForeColor = Color.DimGray,
                Text = BuildTlsNote()
            };
            AddRow(table, string.Empty, note);
            page.Controls.Add(WrapScrollable(table));
            return page;
        }

        private TabPage BuildSshTab()
        {
            TabPage page = new TabPage("SSH Tunnel");
            TableLayoutPanel table = CreateTable();
            sshEnabled = new CheckBox { Text = L("透過 SSH Tunnel 連線", "Connect through an SSH tunnel"), AutoSize = true };
            AddRow(table, string.Empty, sshEnabled);
            sshHost = new TextBox { Width = 340 };
            AddRow(table, L("SSH 主機", "SSH Host"), sshHost);
            sshPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22, Width = 110 };
            AddRow(table, L("SSH 連接埠", "SSH Port"), sshPort);
            sshUsername = new TextBox { Width = 260 };
            AddRow(table, L("SSH 使用者", "SSH User"), sshUsername);
            sshPassword = new TextBox { Width = 340, UseSystemPasswordChar = true };
            AddRow(table, L("SSH 密碼", "SSH Password"), sshPassword);
            privateKeyPath = AddPathRow(table, L("私鑰檔案", "Private Key File"), L("私鑰檔案", "Private Key Files") + "|*.pem;*.key;*.ppk|" + L("所有檔案", "All Files") + "|*.*");
            privateKeyPassphrase = new TextBox { Width = 340, UseSystemPasswordChar = true };
            AddRow(table, L("私鑰密語", "Key Passphrase"), privateKeyPassphrase);
            hostKeyFingerprint = new TextBox { Width = 430 };
            AddRow(table, L("主機金鑰指紋", "Host Key Fingerprint"), hostKeyFingerprint);
            Label note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(570, 0),
                ForeColor = Color.DimGray,
                Text = L("必須填入 SHA256 主機金鑰指紋，例如 SHA256:abc...。請向伺服器管理員確認，避免連到遭冒用的 SSH 主機。密碼與私鑰密語只會存進 Windows 認證管理員。",
                    "A SHA256 host-key fingerprint such as SHA256:abc... is required. Verify it with the server administrator to prevent SSH host impersonation. Passwords and key passphrases are stored only in Windows Credential Manager.")
            };
            AddRow(table, string.Empty, note);

            sshEnabled.CheckedChanged += (sender, args) => ToggleSshFields();
            page.Controls.Add(WrapScrollable(table));
            return page;
        }

        private void LoadValues()
        {
            string mode = ConnectionSecuritySettingsService.GetValue(settings, "tls_mode");
            tlsMode.SelectedItem = mode;
            if (tlsMode.SelectedIndex < 0) tlsMode.SelectedIndex = 0;
            caPath.Text = Get("tls_ca_path");
            clientCertificatePath.Text = Get("tls_client_certificate_path");
            clientKeyPath.Text = Get("tls_client_key_path");
            certificatePassword.Text = Get("tls_certificate_password");
            walletPath.Text = Get("tls_wallet_path");
            checkRevocation.Checked = ConnectionSecuritySettingsService.IsTrue(settings, "tls_check_revocation");
            sshEnabled.Checked = ConnectionSecuritySettingsService.IsTrue(settings, "ssh_enabled");
            sshHost.Text = Get("ssh_host");
            int port;
            if (int.TryParse(Get("ssh_port"), out port) && port >= 1 && port <= 65535) sshPort.Value = port;
            sshUsername.Text = Get("ssh_username");
            sshPassword.Text = Get("ssh_password");
            privateKeyPath.Text = Get("ssh_private_key_path");
            privateKeyPassphrase.Text = Get("ssh_key_passphrase");
            hostKeyFingerprint.Text = Get("ssh_host_key_fingerprint");
            ToggleSshFields();
            ToggleProviderFields();
        }

        private void SaveAndClose()
        {
            string mode = Convert.ToString(tlsMode.SelectedItem);
            if (sshEnabled.Checked && string.Equals(mode, "VerifyFull", StringComparison.OrdinalIgnoreCase))
            {
                string alternative = provider == "mysql" || provider == "postgresql" ? "VerifyCA" : "Required";
                ShowValidation(L("SSH Tunnel 下無法用資料庫主機名稱做 TLS 完整比對，請改選 ", "An SSH tunnel prevents TLS hostname validation against the database host. Select ") + alternative + L("。", "."));
                return;
            }
            if ((string.Equals(mode, "VerifyCA", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "VerifyFull", StringComparison.OrdinalIgnoreCase)) &&
                (provider == "mysql" || provider == "postgresql") && string.IsNullOrWhiteSpace(caPath.Text))
            {
                ShowValidation(L("使用 VerifyCA 或 VerifyFull 時請選擇 CA 憑證。", "Select a CA certificate when using VerifyCA or VerifyFull."));
                return;
            }
            if (provider == "oracle" &&
                string.Equals(Get("connection_type"), "TNS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                ShowValidation(L("Oracle TNS 的 TLS 必須在 tnsnames.ora 設定；這裡請維持 Disabled，或改用 Basic 連線。", "Oracle TNS TLS must be configured in tnsnames.ora. Keep this setting Disabled or use a Basic connection."));
                return;
            }
            if (!ValidateExistingFile(caPath.Text, L("CA 憑證", "CA certificate")) ||
                !ValidateExistingFile(clientCertificatePath.Text, L("用戶端憑證", "client certificate")) ||
                !ValidateExistingFile(clientKeyPath.Text, L("用戶端私鑰", "client private key")) ||
                !ValidateExistingFile(privateKeyPath.Text, L("SSH 私鑰", "SSH private key"))) return;

            if (sshEnabled.Checked)
            {
                if (string.IsNullOrWhiteSpace(sshHost.Text) || string.IsNullOrWhiteSpace(sshUsername.Text))
                {
                    ShowValidation(L("啟用 SSH Tunnel 時，SSH 主機與使用者都不能空白。", "SSH host and user are required when the tunnel is enabled."));
                    return;
                }
                if (string.IsNullOrEmpty(sshPassword.Text) && string.IsNullOrWhiteSpace(privateKeyPath.Text))
                {
                    ShowValidation(L("SSH Tunnel 至少要填密碼或選擇私鑰。", "Enter an SSH password or select a private key."));
                    return;
                }
                if (string.IsNullOrWhiteSpace(hostKeyFingerprint.Text))
                {
                    ShowValidation(L("請填入 SSH 主機金鑰 SHA256 指紋。", "Enter the SSH host-key SHA256 fingerprint."));
                    return;
                }
                hostKeyFingerprint.Text = ConnectionSecuritySettingsService.NormalizeSshHostKeyFingerprint(hostKeyFingerprint.Text);
            }

            settings["tls_mode"] = mode;
            settings["tls_ca_path"] = caPath.Text.Trim();
            settings["tls_client_certificate_path"] = clientCertificatePath.Text.Trim();
            settings["tls_client_key_path"] = clientKeyPath.Text.Trim();
            settings["tls_certificate_password"] = certificatePassword.Text;
            settings["tls_wallet_path"] = walletPath.Text.Trim();
            settings["tls_check_revocation"] = checkRevocation.Checked ? "T" : "F";
            settings["ssh_enabled"] = sshEnabled.Checked ? "T" : "F";
            settings["ssh_host"] = sshHost.Text.Trim();
            settings["ssh_port"] = ((int)sshPort.Value).ToString();
            settings["ssh_username"] = sshUsername.Text.Trim();
            settings["ssh_password"] = sshPassword.Text;
            settings["ssh_private_key_path"] = privateKeyPath.Text.Trim();
            settings["ssh_key_passphrase"] = privateKeyPassphrase.Text;
            settings["ssh_host_key_fingerprint"] = hostKeyFingerprint.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        private IEnumerable<string> GetTlsModes()
        {
            if (provider == "mysql") return new[] { "Preferred", "Required", "VerifyCA", "VerifyFull", "Disabled" };
            if (provider == "postgresql") return new[] { "Prefer", "Require", "VerifyCA", "VerifyFull", "Disable" };
            return new[] { "Disabled", "Required", "VerifyFull" };
        }

        private string BuildTlsNote()
        {
            if (provider == "mssql") return L("Required 會加密但信任伺服器憑證；VerifyFull 會驗證憑證鏈與主機名稱。", "Required encrypts while trusting the server certificate. VerifyFull validates the certificate chain and hostname.");
            if (provider == "oracle") return L("Basic 模式會把 TCP 改為 TCPS；VerifyFull 另啟用伺服器 DN 比對。TNS 模式請直接設定 tnsnames.ora。", "Basic mode changes TCP to TCPS. VerifyFull also enables server DN matching. Configure TNS mode in tnsnames.ora.");
            return L("正式環境建議使用 VerifyFull；若同時使用 SSH Tunnel，請改用 VerifyCA，主機身分由 SSH 金鑰指紋固定。", "Use VerifyFull in production. With an SSH tunnel, use VerifyCA and pin the host identity with the SSH key fingerprint.");
        }

        private void ToggleProviderFields()
        {
            bool sqlServer = provider == "mssql";
            bool oracle = provider == "oracle";
            caPath.Parent.Enabled = !sqlServer && !oracle;
            clientCertificatePath.Parent.Enabled = !sqlServer && !oracle;
            clientKeyPath.Parent.Enabled = !sqlServer && !oracle;
            certificatePassword.Enabled = !sqlServer && !oracle;
            walletPath.Parent.Enabled = oracle;
            checkRevocation.Enabled = provider == "postgresql";
        }

        private void ToggleSshFields()
        {
            foreach (Control control in new Control[] { sshHost, sshPort, sshUsername, sshPassword, privateKeyPath, privateKeyPassphrase, hostKeyFingerprint })
                control.Enabled = sshEnabled.Checked;
            if (privateKeyPath.Parent != null) privateKeyPath.Parent.Enabled = sshEnabled.Checked;
        }

        private string Get(string key) { return ConnectionSecuritySettingsService.GetValue(settings, key); }

        private static bool ValidateExistingFile(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return true;
            ShowValidation(L("找不到", "Cannot find ") + label + L("：", ": ") + path);
            return false;
        }

        private static void ShowValidation(string message)
        {
            MessageBox.Show(message, L("連線安全設定", "Connection Security"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static TableLayoutPanel CreateTable()
        {
            TableLayoutPanel table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(20),
                Dock = DockStyle.Top
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }

        private static Panel WrapScrollable(Control content)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            panel.Controls.Add(content);
            return panel;
        }

        private static void AddRow(TableLayoutPanel table, string labelText, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) };
            control.Margin = new Padding(0, 5, 0, 5);
            control.Anchor = AnchorStyles.Left;
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private static TextBox AddPathRow(TableLayoutPanel table, string label, string filter)
        {
            TextBox text = new TextBox { Width = 360 };
            Button browse = new Button { Text = L("瀏覽...", "Browse..."), AutoSize = true };
            browse.Click += (sender, args) =>
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true })
                {
                    if (dialog.ShowDialog() == DialogResult.OK) text.Text = dialog.FileName;
                }
            };
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.Add(text);
            row.Controls.Add(browse);
            AddRow(table, label, row);
            return text;
        }

        private static TextBox AddFolderRow(TableLayoutPanel table, string label)
        {
            TextBox text = new TextBox { Width = 360 };
            Button browse = new Button { Text = L("瀏覽...", "Browse..."), AutoSize = true };
            browse.Click += (sender, args) =>
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    if (dialog.ShowDialog() == DialogResult.OK) text.Text = dialog.SelectedPath;
                }
            };
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.Add(text);
            row.Controls.Add(browse);
            AddRow(table, label, row);
            return text;
        }

        private static string L(string traditionalChinese, string english)
        {
            return string.Equals(Localization.CurrentLanguage, Localization.TraditionalChinese, StringComparison.OrdinalIgnoreCase)
                ? traditionalChinese
                : english;
        }
    }
}

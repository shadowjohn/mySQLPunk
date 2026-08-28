using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace mySQLPunk
{
    public class OptionsForm : Form
    {
        private readonly ListBox navigationList;
        private readonly Panel navigationHost;
        private readonly Panel contentPanel;
        private RadioButton lightThemeRadio;
        private RadioButton darkThemeRadio;
        private ComboBox languageCombo;
        private CheckBox noPrimaryKeyReadOnlyCheckBox;
        private TextBox remoteBackupDirectoryInput;
        private NumericUpDown remoteBackupRetainCountInput;
        private CheckBox backupIntegrityScheduleEnabledCheckBox;
        private NumericUpDown backupIntegrityIntervalInput;
        private CheckBox backupIntegrityAutoQuarantineCheckBox;
        private NumericUpDown backupIntegrityQuarantineRetainCountInput;
        private NumericUpDown backupRestoreContentSampleRowsInput;
        private ThemePreviewControl lightPreview;
        private ThemePreviewControl darkPreview;
        private readonly Button okButton;
        private readonly Dictionary<string, TextBox> cliPathInputs = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, CheckBox> optionCheckBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NumericUpDown> optionNumbers = new Dictionary<string, NumericUpDown>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComboBox> optionCombos = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBox> optionTextBoxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);

        public string SelectedLanguage { get; private set; }
        public string SelectedTheme { get; private set; }

        public OptionsForm()
        {
            SelectedLanguage = Localization.CurrentLanguage;
            SelectedTheme = ThemeManager.CurrentTheme;

            Text = Localization.T("Options.Title");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 610);
            MinimumSize = new Size(760, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            navigationList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            navigationHost = new Panel
            {
                Dock = DockStyle.Left,
                Width = 180,
                Padding = new Padding(0, UiMetrics.Space2, 1, UiMetrics.Space2)
            };
            navigationHost.Paint += (s, e) =>
                UiKit.DrawVerticalHairline(e.Graphics, navigationHost.Width - 1, 0, navigationHost.Height, ThemeManager.BorderColor);
            navigationHost.Controls.Add(navigationList);
            navigationList.Items.AddRange(new object[]
            {
                Localization.T("Options.General"),
                Localization.T("Options.Navigation"),
                Localization.T("Options.AutoComplete"),
                Localization.T("Options.Editor"),
                Localization.T("Options.Record"),
                Localization.T("Options.AutoRecovery"),
                Localization.T("Options.FileLocation"),
                Localization.T("Options.Connection"),
                Localization.T("Options.AI"),
                Localization.T("Options.Environment"),
                Localization.T("Options.Advanced")
            });
            navigationList.SelectedIndex = 0;
            navigationList.SelectedIndexChanged += (s, e) => RenderSelectedPage();

            contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space5),
                AutoScroll = true
            };

            Panel buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                Padding = new Padding(UiMetrics.Space5, UiMetrics.Space3, UiMetrics.Space5, UiMetrics.Space3)
            };
            buttonPanel.Paint += (s, e) => UiKit.DrawHairline(e.Graphics, 0, buttonPanel.Width, 0, ThemeManager.BorderColor);
            okButton = new Button
            {
                Text = Localization.T("Common.OK"),
                DialogResult = DialogResult.OK,
                Size = new Size(96, UiMetrics.ControlHeight),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, UiMetrics.ControlHeight),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            Action layoutButtons = () =>
            {
                int top = (buttonPanel.Height - UiMetrics.ControlHeight) / 2;
                cancelButton.Location = new Point(buttonPanel.Width - UiMetrics.Space5 - cancelButton.Width, top);
                okButton.Location = new Point(cancelButton.Left - UiMetrics.Space2 - okButton.Width, top);
            };
            layoutButtons();
            buttonPanel.Resize += (s, e) => layoutButtons();
            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);
            okButton.Click += (s, e) =>
            {
                SaveCliPathSettings();
                SaveTableEditSettings();
                SaveBackupMirrorSettings();
                SaveApplicationOptionSettings();
                ApplyAdvancedRegistrationSettings();
                UpdateSelection();

                // 存檔失敗以往被默默吞掉，重開程式設定就消失了；至少要讓使用者知道
                string saveError = ApplicationOptionSettings.LastSaveErrorMessage
                    ?? CliPathSettings.LastSaveErrorMessage
                    ?? TableEditSettings.LastSaveErrorMessage
                    ?? BackupMirrorSettings.LastSaveErrorMessage;
                if (saveError != null)
                {
                    MessageBox.Show(this, Localization.Format("Options.SaveFailed", saveError),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            RenderGeneralPage();

            Controls.Add(contentPanel);
            Controls.Add(navigationHost);
            Controls.Add(buttonPanel);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            ThemeManager.ApplyTo(this);
            ThemeManager.MarkAsPrimary(okButton);
            ThemeManager.StyleNavigationList(navigationList);
            navigationHost.BackColor = ThemeManager.SurfaceColor;
            contentPanel.BackColor = ThemeManager.WindowBackColor;
            buttonPanel.BackColor = ThemeManager.SurfaceColor;
            UpdateSelection();
        }

        private void RenderSelectedPage()
        {
            string selected = navigationList.SelectedItem == null ? string.Empty : navigationList.SelectedItem.ToString();
            if (string.Equals(selected, Localization.T("Options.Navigation"), StringComparison.Ordinal))
            {
                RenderNavigationPage();
            }
            else if (string.Equals(selected, Localization.T("Options.AutoComplete"), StringComparison.Ordinal))
            {
                RenderAutoCompletePage();
            }
            else if (string.Equals(selected, Localization.T("Options.Editor"), StringComparison.Ordinal))
            {
                RenderEditorPage();
            }
            else if (string.Equals(selected, Localization.T("Options.Record"), StringComparison.Ordinal))
            {
                RenderRecordPage();
            }
            else if (string.Equals(selected, Localization.T("Options.AutoRecovery"), StringComparison.Ordinal))
            {
                RenderAutoRecoveryPage();
            }
            else if (string.Equals(selected, Localization.T("Options.Connection"), StringComparison.Ordinal))
            {
                RenderConnectivityPage();
            }
            else if (string.Equals(selected, Localization.T("Options.AI"), StringComparison.Ordinal))
            {
                RenderAiPage();
            }
            else if (string.Equals(selected, Localization.T("Options.Environment"), StringComparison.Ordinal))
            {
                RenderEnvironmentPage();
            }
            else if (string.Equals(selected, Localization.T("Options.FileLocation"), StringComparison.Ordinal))
            {
                RenderFileLocationPage();
            }
            else if (string.Equals(selected, Localization.T("Options.Advanced"), StringComparison.Ordinal))
            {
                RenderAdvancedPage();
            }
            else
            {
                RenderGeneralPage();
            }

            ThemeManager.ApplyTo(contentPanel);
            contentPanel.BackColor = ThemeManager.WindowBackColor;
        }

        private void RenderGeneralPage()
        {
            ClearOptionPage();

            Label sectionTitle = new Label
            {
                Text = Localization.T("Options.General"),
                AutoSize = true,
                Font = UiKit.Title,
                Location = new Point(18, 12)
            };

            Label themeLabel = new Label
            {
                Text = Localization.T("Options.ThemeLabel"),
                AutoSize = true,
                Location = new Point(18, 70)
            };

            lightPreview = new ThemePreviewControl(ThemeManager.Light)
            {
                Location = new Point(105, 58),
                Size = new Size(162, 102)
            };
            darkPreview = new ThemePreviewControl(ThemeManager.Dark)
            {
                Location = new Point(300, 58),
                Size = new Size(162, 102)
            };

            lightThemeRadio = new RadioButton
            {
                Text = Localization.T("Options.Light"),
                AutoSize = true,
                Location = new Point(135, 166)
            };
            darkThemeRadio = new RadioButton
            {
                Text = Localization.T("Options.Dark"),
                AutoSize = true,
                Location = new Point(330, 166)
            };
            lightThemeRadio.Checked = SelectedTheme != ThemeManager.Dark;
            darkThemeRadio.Checked = SelectedTheme == ThemeManager.Dark;

            Label languageLabel = new Label
            {
                Text = Localization.T("Options.LanguageLabel"),
                AutoSize = true,
                Location = new Point(18, 215)
            };

            languageCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(105, 210),
                Width = 250
            };
            languageCombo.Items.Add(new LanguageItem(Localization.T("Menu.LanguageZh"), Localization.TraditionalChinese));
            languageCombo.Items.Add(new LanguageItem(Localization.T("Menu.LanguageEn"), Localization.English));
            languageCombo.SelectedIndex = SelectedLanguage == Localization.English ? 1 : 0;

            Label noteLabel = new Label
            {
                Text = Localization.T("Options.RestartNote"),
                AutoSize = true,
                Location = new Point(18, 260),
                MaximumSize = new Size(600, 0)
            };
            noPrimaryKeyReadOnlyCheckBox = new CheckBox
            {
                Text = Localization.T("Options.NoPrimaryKeyReadOnly"),
                AutoSize = true,
                Checked = TableEditSettings.NoPrimaryKeyReadOnly,
                Location = new Point(18, 300),
                MaximumSize = new Size(600, 0)
            };
            AddOptionCheckBox("AllowDuplicateObjects", T("允許重複開啟相同的物件", "Allow opening the same object more than once"), 340);
            AddOptionCheckBox("ShowObjectTooltips", T("顯示工具提示", "Show tooltips"), 372);
            AddOptionCheckBox("RememberTableSettings", T("顯示並記住資料表設定檔", "Show and remember named table profiles"), 404);
            AddOptionCheckBox("AutoCheckUpdates", T("啟動時自動檢查更新", "Check for updates on startup"), 436);

            lightThemeRadio.CheckedChanged += (s, e) => UpdateSelection();
            darkThemeRadio.CheckedChanged += (s, e) => UpdateSelection();
            lightPreview.Click += (s, e) => lightThemeRadio.Checked = true;
            darkPreview.Click += (s, e) => darkThemeRadio.Checked = true;
            languageCombo.SelectedIndexChanged += (s, e) => UpdateSelection();

            contentPanel.Controls.Add(sectionTitle);
            contentPanel.Controls.Add(themeLabel);
            contentPanel.Controls.Add(lightPreview);
            contentPanel.Controls.Add(darkPreview);
            contentPanel.Controls.Add(lightThemeRadio);
            contentPanel.Controls.Add(darkThemeRadio);
            contentPanel.Controls.Add(languageLabel);
            contentPanel.Controls.Add(languageCombo);
            contentPanel.Controls.Add(noteLabel);
            contentPanel.Controls.Add(noPrimaryKeyReadOnlyCheckBox);
        }

        private void RenderNavigationPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Navigation"));

            AddOptionCombo("IndexOpenTarget", T("開啟索引標籤於:", "Open tabs in:"), new[]
            {
                new OptionChoice("main", T("主視窗", "Main window")),
                new OptionChoice("last", T("最後開啟的視窗", "Last opened window")),
                new OptionChoice("new", T("新視窗", "New window"))
            }, 60, 210);

        }

        private void RenderAutoCompletePage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.AutoComplete"));
            AddOptionCheckBox("AutoCompleteEnabled", T("使用自動完成程式碼", "Use code completion"), 60);
            AddOptionCheckBox("AutoCompleteAutoRefresh", T("自動更新自動完成資訊", "Automatically refresh completion metadata"), 92);
            AddOptionCheckBox("AutoCompleteSelectFirst", T("自動選取第一個建議項目", "Automatically select the first suggestion"), 124);

            Button clearButton = new Button
            {
                Text = T("清除自動完成資料", "Clear completion data"),
                Location = new Point(430, 58),
                Size = new Size(200, 30)
            };
            clearButton.Click += (s, e) =>
            {
                ApplicationOptionSettings.ClearAutoCompleteCache();
                MessageBox.Show(BuildAutoCompleteCacheClearedMessage(), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            contentPanel.Controls.Add(clearButton);
        }

        public static string BuildAutoCompleteCacheClearedMessage()
        {
            return Localization.T("Options.AutoCompleteCacheCleared");
        }

        private void RenderEditorPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Editor"));
            AddOptionCheckBox("EditorSyntaxHighlight", T("使用語法突顯", "Use syntax highlighting"), 60);
            AddOptionNumeric("EditorLargeFileLimitMb", T("如果檔案大小大於此就停用 (MB):", "Disable editor helpers above file size (MB):"), 92, 1, 4096);
            AddOptionCheckBox("EditorWordWrap", T("使用自動換行", "Use word wrap"), 124);
            AddOptionNumeric("EditorTabWidth", T("定位點寬度:", "Tab width:"), 156, 1, 16);

            AddOptionCheckBox("EditorInsertSpaces", T("按 Tab 時插入空格", "Insert spaces when pressing Tab"), 188);

            AddOptionCombo("EditorFontName", T("編輯器字型:", "Editor font:"), BuildFontChoices(), 330, 300);
            AddOptionNumeric("EditorFontSize", T("字型大小:", "Font size:"), 372, 6, 48);
        }

        private void RenderRecordPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Record"));
            AddOptionCheckBox("RecordLimitEnabled", T("限制記錄", "Limit records"), 60);
            AddOptionNumeric("RecordLimit", T("筆記錄（每頁）:", "records per page:"), 92, 1, 1000000);
            AddOptionCheckBox("RecordAutoBeginTransaction", T("自動開始交易", "Automatically begin transaction"), 124);
            AddOptionCombo("RecordGridFontName", T("網格字型:", "Grid font:"), BuildFontChoices(), 170, 300);
            AddOptionNumeric("RecordGridFontSize", T("網格字型大小:", "Grid font size:"), 212, 6, 48);
            AddOptionCombo("RecordRowHeightMode", T("列高度:", "Row height:"), new[]
            {
                new OptionChoice("single", T("單列", "Single line")),
                new OptionChoice("compact", T("緊湊", "Compact")),
                new OptionChoice("comfortable", T("舒適", "Comfortable"))
            }, 254, 180);
            AddOptionTextBox("RecordDateFormat", T("日期格式:", "Date format:"), 302, 220);
            AddOptionTextBox("RecordTimeFormat", T("時間格式:", "Time format:"), 344, 220);
            AddOptionTextBox("RecordDateTimeFormat", T("日期時間格式:", "Date/time format:"), 386, 220);
            AddOptionCheckBox("RecordShowThousandsSeparator", T("顯示千位分隔符號", "Show thousands separator"), 430);
            AddOptionCheckBox("RecordUseSystemNumberFormat", T("使用系統區域設定的小數點和千位分隔符號", "Use system decimal and thousands separators"), 462);
        }

        private void RenderAiPage()
        {
            ClearOptionPage();
            AddOptionTitle(T("AI 助理", "AI Assistant"));
            lib.AiChatSettings initialAiSettings = lib.AiChatSettings.Load();

            Label introduction = new Label
            {
                Text = T("優先使用你已登入的 AI CLI 訂閱；也可以在下方設定 API 或本機模型服務。",
                    "Use an AI CLI subscription you are already signed in to, or configure an API or local model below."),
                AutoSize = true,
                MaximumSize = new Size(630, 0),
                Location = new Point(18, 42),
                ForeColor = ThemeManager.MutedTextColor
            };
            contentPanel.Controls.Add(introduction);

            Label cliTitle = new Label
            {
                Text = T("訂閱 CLI", "Subscription CLIs"),
                AutoSize = true,
                Font = UiKit.Subtitle,
                Location = new Point(18, 82)
            };
            contentPanel.Controls.Add(cliTitle);

            Button refreshCliButton = new Button
            {
                Text = T("重新偵測", "Refresh"),
                Size = new Size(96, UiMetrics.ControlHeight),
                Location = new Point(482, 74),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            contentPanel.Controls.Add(refreshCliButton);

            FlowLayoutPanel cliCards = new FlowLayoutPanel
            {
                Location = new Point(18, 112),
                Size = new Size(560, 178),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                WrapContents = false,
                AutoScroll = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            contentPanel.Controls.Add(cliCards);

            Label cliPrivacyHint = new Label
            {
                Text = T("只讀取 CLI 保存的帳號標籤與登入方式，不會顯示 token 或金鑰；找到登入資料不代表已驗證訂閱權限。",
                    "Only the account label and sign-in method saved by each CLI are read. Tokens and keys are never shown; detected sign-in data does not verify subscription access."),
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Location = new Point(18, 296),
                ForeColor = ThemeManager.MutedTextColor
            };
            contentPanel.Controls.Add(cliPrivacyHint);

            Label advancedTitle = new Label
            {
                Text = T("目前服務與進階設定", "Current service and advanced settings"),
                AutoSize = true,
                Font = UiKit.Subtitle,
                Location = new Point(18, 340)
            };
            contentPanel.Controls.Add(advancedTitle);

            var providerChoices = new List<OptionChoice>();
            foreach (lib.AiProviderPreset preset in lib.AiChatService.Presets)
            {
                providerChoices.Add(new OptionChoice(preset.Id, preset.DisplayName));
            }
            ComboBox providerCombo = AddOptionCombo("AiProvider", T("服務提供者:", "Provider:"), providerChoices.ToArray(), 374, 350);
            providerCombo.Left = 220;

            Label endpointLabel = new Label
            {
                Text = T("端點 URL:", "Endpoint URL:"),
                AutoSize = true,
                Location = new Point(18, 420)
            };
            TextBox endpointBox = new TextBox
            {
                Text = ApplicationOptionSettings.GetString("AiEndpoint"),
                Location = new Point(220, 416),
                Width = 350
            };
            endpointBox.TextChanged += (s, e) => ApplicationOptionSettings.SetString("AiEndpoint", endpointBox.Text);
            optionTextBoxes["AiEndpoint"] = endpointBox;
            contentPanel.Controls.Add(endpointLabel);
            contentPanel.Controls.Add(endpointBox);

            Label modelLabel = new Label
            {
                Text = T("模型（留空用預設）:", "Model (blank = default):"),
                AutoSize = true,
                Location = new Point(18, 462)
            };
            contentPanel.Controls.Add(modelLabel);
            ComboBox modelCombo = new ComboBox
            {
                Location = new Point(220, 458),
                Width = 350,
                DropDownStyle = ComboBoxStyle.DropDown,
                Text = initialAiSettings.Model
            };
            modelCombo.TextChanged += (s, e) => ApplicationOptionSettings.SetString("AiModel", modelCombo.Text);
            contentPanel.Controls.Add(modelCombo);

            // API 金鑰不落地設定檔，直接進 Windows 認證管理員；一家一把
            Label keyLabel = new Label
            {
                Text = T("API 金鑰:", "API key:"),
                AutoSize = true,
                Location = new Point(18, 504)
            };
            contentPanel.Controls.Add(keyLabel);
            TextBox keyBox = new TextBox
            {
                Location = new Point(220, 500),
                Width = 350,
                UseSystemPasswordChar = true
            };
            Label keyState = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(350, 0),
                Location = new Point(220, 528),
                ForeColor = SystemColors.GrayText
            };
            Func<string> currentProviderId = () =>
            {
                OptionChoice choice = providerCombo.SelectedItem as OptionChoice;
                return choice == null ? "openai" : choice.Value;
            };
            Action refreshKeyState = () =>
            {
                lib.AiProviderPreset preset = lib.AiChatService.FindPreset(currentProviderId());
                keyBox.Enabled = preset.NeedsKey;
                if (preset.AuthStyle == "cli")
                {
                    keyState.Text = T("使用 CLI 自己的登入身分；端點欄可留空自動偵測，或填入執行檔完整路徑。",
                        "Uses the CLI's own sign-in identity. Leave the endpoint blank for auto-detection or enter the full executable path.");
                }
                else if (!preset.NeedsKey)
                {
                    keyState.Text = T("這個服務不需要金鑰（本機推論）。", "This service needs no key (local inference).");
                }
                else if (lib.AiChatService.HasApiKey(preset.Id))
                {
                    keyState.Text = T("此服務已設定金鑰。留空＝保持不變；輸入新值＝覆蓋；輸入單一減號「-」＝清除。",
                        "A key is configured for this provider. Blank = keep; new value = replace; a single \"-\" = clear.");
                }
                else
                {
                    keyState.Text = T("此服務尚未設定金鑰。金鑰會存進 Windows 認證管理員，不會寫入設定檔。",
                        "No key configured for this provider yet. Keys are stored in Windows Credential Manager, never in the settings file.");
                }
            };
            keyBox.Leave += (s, e) =>
            {
                string value = (keyBox.Text ?? "").Trim();
                if (value.Length == 0) return;
                string target = lib.AiChatService.ApiKeyTargetFor(currentProviderId());
                if (value == "-")
                {
                    lib.WindowsCredentialService.TryDeletePassword(target);
                }
                else
                {
                    lib.WindowsCredentialService.TryWritePassword(target, "ai", value);
                }
                keyBox.Text = "";
                refreshKeyState();
            };
            contentPanel.Controls.Add(keyBox);
            contentPanel.Controls.Add(keyState);

            // 一鍵跳到該服務的金鑰／認證網頁（本機服務則是下載頁）
            LinkLabel keyLink = new LinkLabel
            {
                Text = T("前往取得金鑰／認證頁面", "Open the provider's key / sign-up page"),
                AutoSize = true,
                Location = new Point(18, 528),
                LinkColor = SystemColors.HotTrack
            };
            keyLink.LinkClicked += (s, e) =>
            {
                lib.AiProviderPreset preset = lib.AiChatService.FindPreset(currentProviderId());
                if (string.IsNullOrWhiteSpace(preset.KeySignupUrl)) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preset.KeySignupUrl) { UseShellExecute = true });
                }
                catch { }
            };
            contentPanel.Controls.Add(keyLink);

            // ── 偵測本機服務 + 測試連線 ──
            Label probeResult = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Location = new Point(18, 608),
                ForeColor = SystemColors.GrayText
            };
            Button detectButton = new Button
            {
                Text = T("偵測本機模型", "Detect local models"),
                Location = new Point(18, 568),
                AutoSize = true
            };
            Button testButton = new Button
            {
                Text = T("測試連線並列出模型", "Test connection && list models"),
                Location = new Point(152, 568),
                Size = new Size(174, UiMetrics.ControlHeight)
            };
            Button oauthButton = new Button
            {
                Text = T("🔑 用瀏覽器授權連結 OpenRouter", "🔑 Connect OpenRouter via browser"),
                Location = new Point(334, 568),
                Size = new Size(230, UiMetrics.ControlHeight)
            };
            oauthButton.Click += async (s, e) =>
            {
                oauthButton.Enabled = false;
                probeResult.Text = T("已開啟瀏覽器，請在網頁上同意授權…", "Browser opened — approve the authorization there…");
                try
                {
                    await System.Threading.Tasks.Task.Run(() => lib.AiOAuthService.ConnectOpenRouter());
                    ApplicationOptionSettings.SetString("AiProvider", "openrouter");
                    ApplicationOptionSettings.SetString("AiEndpoint", "");
                    for (int i = 0; i < providerCombo.Items.Count; i++)
                    {
                        OptionChoice choice = providerCombo.Items[i] as OptionChoice;
                        if (choice != null && choice.Value == "openrouter") { providerCombo.SelectedIndex = i; break; }
                    }
                    refreshKeyState();
                    probeResult.Text = T("OpenRouter 已連結完成，金鑰存進 Windows 認證管理員；這把鑰匙可以用 OpenAI／Claude／Gemini 等各家模型。",
                        "OpenRouter connected — the key is stored in Windows Credential Manager and works with OpenAI / Claude / Gemini models and more.");
                }
                catch (Exception ex)
                {
                    probeResult.Text = T("授權失敗：", "Authorization failed: ") + ex.Message;
                }
                finally
                {
                    oauthButton.Enabled = true;
                }
            };

            detectButton.Click += async (s, e) =>
            {
                detectButton.Enabled = false;
                probeResult.Text = T("偵測中…", "Detecting…");
                try
                {
                    var found = await System.Threading.Tasks.Task.Run(() => lib.AiChatService.DetectLocalServices());
                    var summary = new List<string>();
                    foreach (var server in found) summary.Add(server.Key.DisplayName + "（" + server.Value.Count + T(" 個模型）", " models)"));

                    if (summary.Count == 0)
                    {
                        probeResult.Text = T("沒有偵測到正在執行的 Ollama 或 LM Studio。上方訂閱 CLI 會另外自動偵測。",
                            "No running Ollama or LM Studio service was detected. Subscription CLIs are detected separately above.");
                    }
                    else
                    {
                        var first = found[0];
                        SelectAiProvider(providerCombo, first.Key.Id);
                        modelCombo.Items.Clear();
                        foreach (string m in first.Value) modelCombo.Items.Add(m);
                        if (first.Value.Count > 0 && string.IsNullOrWhiteSpace(modelCombo.Text)) modelCombo.Text = first.Value[0];
                        probeResult.Text = T("偵測到 ", "Detected ") + first.Key.DisplayName +
                            T("，共 ", " with ") + first.Value.Count + T(" 個模型，已自動選用。", " models. Selected automatically.");
                    }
                }
                finally
                {
                    detectButton.Enabled = true;
                }
            };

            testButton.Click += async (s, e) =>
            {
                testButton.Enabled = false;
                probeResult.Text = T("測試中…", "Testing…");
                try
                {
                    lib.AiChatSettings settings = lib.AiChatSettings.Load();
                    if (settings.Preset.AuthStyle == "cli")
                    {
                        string version = await System.Threading.Tasks.Task.Run(() => lib.AiChatService.CliVersion(settings));
                        probeResult.Text = T("CLI 可用：", "CLI available: ") + version;
                    }
                    else
                    {
                        var models = await System.Threading.Tasks.Task.Run(() => lib.AiChatService.ListModels(settings));
                        modelCombo.Items.Clear();
                        foreach (string m in models) modelCombo.Items.Add(m);
                        probeResult.Text = T("連線成功，", "Connected. ") + models.Count + T(" 個可用模型已放進模型下拉。", " models loaded into the model dropdown.");
                    }
                }
                catch (Exception ex)
                {
                    probeResult.Text = T("測試失敗：", "Test failed: ") + ex.Message;
                }
                finally
                {
                    testButton.Enabled = true;
                }
            };

            List<lib.AiCliDetectionResult> cliDetections = lib.AiChatService.DetectCliProviders();
            Action updateAdvancedLayout = () => { };
            Action renderCliCards = () =>
            {
                cliCards.SuspendLayout();
                cliCards.Controls.Clear();
                int cardWidth = Math.Max(178, (cliCards.ClientSize.Width - 18) / 3);
                string selectedProvider = currentProviderId();
                foreach (lib.AiCliDetectionResult result in cliDetections)
                {
                    Control card = CreateAiCliCard(result,
                        string.Equals(result.Preset.Id, selectedProvider, StringComparison.OrdinalIgnoreCase),
                        id => SelectAiProvider(providerCombo, id));
                    card.Width = cardWidth;
                    cliCards.Controls.Add(card);
                }
                cliCards.ResumeLayout();
            };
            cliCards.SizeChanged += (s, e) => renderCliCards();
            refreshCliButton.Click += async (s, e) =>
            {
                refreshCliButton.Enabled = false;
                refreshCliButton.Text = T("偵測中…", "Detecting…");
                try
                {
                    cliDetections = await System.Threading.Tasks.Task.Run(() => lib.AiChatService.DetectCliProviders());
                    renderCliCards();
                    updateAdvancedLayout();
                }
                finally
                {
                    refreshCliButton.Text = T("重新偵測", "Refresh");
                    refreshCliButton.Enabled = true;
                }
            };
            renderCliCards();

            contentPanel.Controls.Add(detectButton);
            contentPanel.Controls.Add(testButton);
            contentPanel.Controls.Add(oauthButton);
            contentPanel.Controls.Add(probeResult);

            Label hint = new Label
            {
                Text = T("除 Anthropic Claude 走原生 API 外，其餘服務都走 OpenAI 相容的 chat/completions 介面。Azure OpenAI 的端點請填到 deployment 為止（…/openai/deployments/<名稱>），金鑰用資源的 api-key。",
                         "All providers use the OpenAI-compatible chat/completions API except Anthropic Claude (native API). For Azure OpenAI, fill the endpoint down to the deployment (…/openai/deployments/<name>) and use the resource api-key."),
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Location = new Point(18, 658)
            };
            contentPanel.Controls.Add(hint);

            updateAdvancedLayout = () =>
            {
                lib.AiProviderPreset preset = lib.AiChatService.FindPreset(currentProviderId());
                bool isCli = string.Equals(preset.AuthStyle, "cli", StringComparison.OrdinalIgnoreCase);
                bool isLocal = !preset.NeedsKey && !isCli;
                bool isOpenRouter = string.Equals(preset.Id, "openrouter", StringComparison.OrdinalIgnoreCase);
                bool selectedCliInstalled = false;
                foreach (lib.AiCliDetectionResult result in cliDetections)
                {
                    if (string.Equals(result.Preset.Id, preset.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedCliInstalled = result.Installed;
                        break;
                    }
                }

                bool showEndpoint = !isCli || !selectedCliInstalled || !string.IsNullOrWhiteSpace(endpointBox.Text);
                bool showKey = preset.NeedsKey;
                int nextTop = 416;

                endpointLabel.Visible = showEndpoint;
                endpointBox.Visible = showEndpoint;
                if (showEndpoint)
                {
                    endpointLabel.Text = isCli
                        ? T("自訂 CLI 路徑:", "Custom CLI path:")
                        : T("端點 URL:", "Endpoint URL:");
                    endpointLabel.Top = nextTop + 4;
                    endpointBox.Top = nextTop;
                    nextTop += 42;
                }

                modelLabel.Text = isCli
                    ? T("模型（留空由 CLI 決定）:", "Model (blank = CLI default):")
                    : T("模型（留空用預設）:", "Model (blank = default):");
                modelLabel.Top = nextTop + 4;
                modelCombo.Top = nextTop;
                nextTop += 42;

                keyLabel.Visible = showKey;
                keyBox.Visible = showKey;
                keyLink.Visible = showKey;
                keyState.Visible = showKey;
                if (showKey)
                {
                    keyLabel.Top = nextTop + 4;
                    keyBox.Top = nextTop;
                    keyLink.Top = nextTop + 32;
                    keyState.Top = nextTop + 32;
                    nextTop += 74;
                }

                int buttonTop = nextTop + 8;
                detectButton.Visible = isLocal;
                oauthButton.Visible = isOpenRouter;
                testButton.Text = isCli ? T("測試 CLI", "Test CLI") : T("測試連線並列出模型", "Test connection && list models");
                testButton.Width = isCli ? 120 : 174;

                if (isLocal)
                {
                    detectButton.Location = new Point(18, buttonTop);
                    testButton.Location = new Point(152, buttonTop);
                }
                else
                {
                    testButton.Location = new Point(18, buttonTop);
                }
                if (isOpenRouter) oauthButton.Location = new Point(200, buttonTop);

                probeResult.Top = buttonTop + 40;
                hint.Visible = showKey;
                if (hint.Visible) hint.Top = probeResult.Top + 50;
                int contentBottom = hint.Visible ? hint.Bottom + 24 : probeResult.Top + 64;
                contentPanel.AutoScrollMinSize = new Size(0, Math.Max(540, contentBottom));
            };

            providerCombo.SelectedIndexChanged += (s, e) =>
            {
                lib.AiProviderPreset preset = lib.AiChatService.FindPreset(currentProviderId());
                // 換供應商時清掉端點/模型覆寫，回到該家預設；CLI 供應商放內建常用型號
                endpointBox.Text = "";
                modelCombo.Items.Clear();
                foreach (string m in lib.AiChatService.KnownCliModels(preset.Id)) modelCombo.Items.Add(m);
                modelCombo.Text = "";
                keyBox.Text = "";
                refreshKeyState();
                probeResult.Text = string.IsNullOrWhiteSpace(preset.Endpoint)
                    ? T("這個選項需要自行填端點 URL。", "This option requires you to fill in the endpoint URL.")
                    : T("預設端點：", "Default endpoint: ") + preset.Endpoint +
                      (string.IsNullOrWhiteSpace(preset.DefaultModel) ? "" : T("；預設模型：", "; default model: ") + preset.DefaultModel);
                updateAdvancedLayout();
                renderCliCards();
            };
            refreshKeyState();
            updateAdvancedLayout();
        }

        private static void SelectAiProvider(ComboBox providerCombo, string providerId)
        {
            for (int i = 0; i < providerCombo.Items.Count; i++)
            {
                OptionChoice choice = providerCombo.Items[i] as OptionChoice;
                if (choice != null && string.Equals(choice.Value, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    providerCombo.SelectedIndex = i;
                    return;
                }
            }
        }

        private Control CreateAiCliCard(lib.AiCliDetectionResult result, bool selected, Action<string> selectProvider)
        {
            AiCliCardPanel card = new AiCliCardPanel
            {
                Height = 174,
                Margin = new Padding(0, 0, 8, 0),
                IsSelected = selected
            };

            AiCliGlyph glyph = new AiCliGlyph
            {
                Location = new Point(12, 12),
                Size = new Size(30, 30)
            };
            card.Controls.Add(glyph);

            Label title = new Label
            {
                Text = AiCliProductName(result.Preset.Id),
                AutoEllipsis = true,
                Font = UiKit.Subtitle,
                Location = new Point(50, 11),
                Size = new Size(card.Width - 62, 21),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(title);

            AiStatusPill status = new AiStatusPill
            {
                Text = result.Installed ? T("可使用", "Available") : T("未偵測", "Missing"),
                Positive = result.Installed,
                Size = new Size(56, 22),
                Location = new Point(12, 53),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            card.Controls.Add(status);

            Label executable = new Label
            {
                Text = result.Executable,
                AutoEllipsis = true,
                Font = UiKit.GetMonoFont(UiMetrics.FontSizeCaption),
                Location = new Point(50, 31),
                Size = new Size(118, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(executable);

            Label path = new Label
            {
                Text = result.Installed ? result.ExecutablePath : T("PATH 中找不到可直接執行的程式", "Executable was not found in PATH"),
                AutoEllipsis = true,
                Location = new Point(76, 55),
                Size = new Size(card.Width - 88, 19),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(path);

            Label accountCaption = new Label
            {
                Text = T("偵測到登入資料", "Detected sign-in"),
                AutoSize = true,
                Location = new Point(12, 80),
                Font = UiKit.Caption
            };
            card.Controls.Add(accountCaption);

            Label account = new Label
            {
                Text = AiCliAccountText(result),
                AutoEllipsis = true,
                Location = new Point(12, 99),
                Size = new Size(card.Width - 24, 19),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(account);

            Label method = new Label
            {
                Text = result.Account == null ? string.Empty : result.Account.Method ?? string.Empty,
                AutoEllipsis = true,
                Location = new Point(12, 118),
                Size = new Size(card.Width - 24, 17),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = UiKit.Caption
            };
            card.Controls.Add(method);

            Button action = new Button
            {
                Text = result.Installed
                    ? selected ? T("目前使用中", "Currently selected") : T("設為目前使用", "Use this CLI")
                    : T("開啟安裝頁", "Open install page"),
                Location = new Point(12, 137),
                Size = new Size(card.Width - 24, 27),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Enabled = !selected
            };
            action.Click += (s, e) =>
            {
                if (result.Installed)
                {
                    selectProvider(result.Preset.Id);
                    return;
                }
                OpenExternalUrl(result.Preset.KeySignupUrl);
            };
            card.Controls.Add(action);
            return card;
        }

        private string AiCliAccountText(lib.AiCliDetectionResult result)
        {
            if (!result.Installed) return T("安裝後才會檢查帳號", "Account is checked after installation");
            if (result.Account == null) return T("無法安全判定帳號", "Account could not be determined safely");
            switch (result.Account.State)
            {
                case lib.AiCliAccountState.SignedIn:
                    return string.IsNullOrWhiteSpace(result.Account.Label)
                        ? T("已找到登入資料", "Sign-in data found")
                        : result.Account.Label;
                case lib.AiCliAccountState.NotFound:
                    return T("尚未找到登入資料", "No sign-in data found");
                case lib.AiCliAccountState.Unsupported:
                    return T("此 CLI 尚未支援帳號偵測", "Account detection is not supported");
                default:
                    return T("無法安全判定帳號", "Account could not be determined safely");
            }
        }

        private static string AiCliProductName(string providerId)
        {
            switch ((providerId ?? string.Empty).ToLowerInvariant())
            {
                case "codex-cli": return "OpenAI Codex";
                case "claude-cli": return "Claude Code";
                case "gemini-cli": return "Gemini CLI";
                default: return providerId;
            }
        }

        private static void OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private void RenderAutoRecoveryPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.AutoRecovery"));
            AddOptionCheckBox("AutoRecoveryQueryEnabled", T("查詢", "Query"), 60);
            AddOptionNumeric("AutoRecoveryIntervalSeconds", T("自動儲存間隔（秒）:", "Auto-save interval (seconds):"), 94, 5, 3600);
        }

        private void RenderConnectivityPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Connection"));
            AddOptionCheckBox("ConnectionUseProxy", T("使用代理伺服器", "Use proxy server"), 60);
            AddOptionCombo("ConnectionProxyType", T("代理伺服器類型:", "Proxy type:"), new[]
            {
                new OptionChoice("http", "HTTP"),
                new OptionChoice("socks5", "SOCKS5")
            }, 94, 160);
            AddOptionTextBox("ConnectionProxyHost", T("主機:", "Host:"), 136, 300);
            AddOptionNumeric("ConnectionProxyPort", T("通訊埠:", "Port:"), 178, 1, 65535);
            AddOptionTextBox("ConnectionProxyUser", T("使用者名稱:", "User name:"), 220, 240);
            AddOptionTextBox("ConnectionProxyPassword", T("密碼:", "Password:"), 262, 240, true);

            Button testButton = new Button
            {
                Text = T("測試連線能力", "Test connectivity"),
                Location = new Point(430, 300),
                Size = new Size(150, 30)
            };
            testButton.Click += async (s, e) =>
            {
                testButton.Enabled = false;
                testButton.Text = T("測試中...", "Testing...");
                try
                {
                    mySQLPunk.lib.ConnectionProxySettings settings = mySQLPunk.lib.ConnectionProxySettingsService.Load();
                    mySQLPunk.lib.ConnectionProxyTestResult result = await Task.Run(() =>
                        mySQLPunk.lib.ConnectionProxySettingsService.TestConnectivity(
                            settings,
                            mySQLPunk.lib.ConnectionProxySettingsService.DefaultConnectivityTestUri,
                            8000));
                    MessageBoxIcon icon = result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
                    string message = BuildConnectivityTestMessage(result);
                    MessageBox.Show(message, Localization.T("Options.Connection"), MessageBoxButtons.OK, icon);
                }
                finally
                {
                    testButton.Text = T("測試連線能力", "Test connectivity");
                    testButton.Enabled = true;
                }
            };
            contentPanel.Controls.Add(testButton);
        }

        private string BuildConnectivityTestMessage(mySQLPunk.lib.ConnectionProxyTestResult result)
        {
            if (result == null) return T("連線能力測試沒有回傳結果。", "Connectivity test returned no result.");

            string mode = result.UsedProxy ? T("HTTP 代理", "HTTP proxy") : T("直接連線", "direct connection");
            string status = result.Success ? T("成功", "Succeeded") : T("失敗", "Failed");
            string target = string.IsNullOrWhiteSpace(result.TargetUrl) ? "" : "\n" + T("目標：", "Target: ") + result.TargetUrl;
            string detailText = BuildConnectivityTestDetail(result);
            string detail = string.IsNullOrWhiteSpace(detailText) ? "" : "\n" + T("詳細：", "Detail: ") + detailText;
            return T("連線能力測試", "Connectivity test") + " " + status + "\n" +
                T("模式：", "Mode: ") + mode + target + detail;
        }

        private string BuildConnectivityTestDetail(mySQLPunk.lib.ConnectionProxyTestResult result)
        {
            if (result == null) return string.Empty;
            if (result.Success && result.AttemptedRequest)
            {
                string suffix = result.StatusCode > 0 ? " HTTP " + result.StatusCode : string.Empty;
                return T("HTTP 探測成功。", "HTTP probe succeeded.") + suffix;
            }
            if (result.Success && !result.AttemptedRequest)
            {
                return T("代理未啟用，測試會使用直接連線。", "Proxy is disabled; the test will use direct connection.");
            }
            if (!result.AttemptedRequest && result.Message != null && result.Message.IndexOf("SOCKS5", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return T("SOCKS5 設定會保存，但目前 WebRequest 連線測試只支援 HTTP/HTTPS 代理。", "SOCKS5 settings are saved, but the WebRequest connectivity test currently supports HTTP/HTTPS proxies only.");
            }
            if (!result.AttemptedRequest && result.Message != null && result.Message.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return T("代理主機不可空白。", "Proxy host cannot be empty.");
            }
            if (result.AttemptedRequest && result.StatusCode > 0)
            {
                return T("HTTP 探測失敗。", "HTTP probe failed.") + " HTTP " + result.StatusCode;
            }
            return result.Message ?? string.Empty;
        }

        private void RenderAdvancedPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Advanced"));
            AddOptionCheckBox("AdvancedEnableDiagnosticsLog", T("啟用診斷記錄", "Enable diagnostics logging"), 60);
            AddOptionCheckBox("AdvancedAllowMultipleInstances", T("允許重複執行 mySQLPunk", "Allow multiple mySQLPunk instances"), 92);
            AddOptionCheckBox("AdvancedRegisterSqlFileOpen", T("在「開啟方式」清單上註冊 SQL 檔案", "Register SQL files in the Open With list"), 124);
            AddOptionCheckBox("AdvancedRegisterUrlProtocol", T("註冊 mySQLPunk URL 協定", "Register mySQLPunk URL protocol"), 156);
        }

        private void ClearOptionPage()
        {
            contentPanel.Controls.Clear();
            contentPanel.AutoScrollMinSize = Size.Empty;
            optionCheckBoxes.Clear();
            optionNumbers.Clear();
            optionCombos.Clear();
            optionTextBoxes.Clear();
        }

        private void AddOptionTitle(string title)
        {
            contentPanel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = UiKit.Title,
                Location = new Point(18, 12)
            });
        }

        private CheckBox AddOptionCheckBox(string key, string text, int top)
        {
            CheckBox checkBox = new CheckBox
            {
                Text = text,
                Checked = ApplicationOptionSettings.GetBool(key),
                AutoSize = true,
                Location = new Point(18, top),
                MaximumSize = new Size(650, 0)
            };
            checkBox.CheckedChanged += (s, e) => ApplicationOptionSettings.SetBool(key, checkBox.Checked);
            optionCheckBoxes[key] = checkBox;
            contentPanel.Controls.Add(checkBox);
            return checkBox;
        }

        private NumericUpDown AddOptionNumeric(string key, string labelText, int top, int minimum, int maximum)
        {
            contentPanel.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(18, top + 4)
            });
            NumericUpDown input = new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Min(maximum, Math.Max(minimum, ApplicationOptionSettings.GetInt(key))),
                Location = new Point(250, top),
                Width = 95
            };
            input.ValueChanged += (s, e) => ApplicationOptionSettings.SetInt(key, (int)input.Value);
            optionNumbers[key] = input;
            contentPanel.Controls.Add(input);
            return input;
        }

        private ComboBox AddOptionCombo(string key, string labelText, OptionChoice[] choices, int top, int width)
        {
            contentPanel.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(18, top + 4)
            });
            ComboBox combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(250, top),
                Width = width
            };
            combo.Items.AddRange(choices);
            string current = ApplicationOptionSettings.GetString(key);
            int selected = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                if (string.Equals(choices[i].Value, current, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    break;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = selected;
            combo.SelectedIndexChanged += (s, e) =>
            {
                OptionChoice choice = combo.SelectedItem as OptionChoice;
                ApplicationOptionSettings.SetString(key, choice == null ? string.Empty : choice.Value);
            };
            optionCombos[key] = combo;
            contentPanel.Controls.Add(combo);
            return combo;
        }

        private TextBox AddOptionTextBox(string key, string labelText, int top, int width)
        {
            return AddOptionTextBox(key, labelText, top, width, false);
        }

        private TextBox AddOptionTextBox(string key, string labelText, int top, int width, bool password)
        {
            contentPanel.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(18, top + 4)
            });
            TextBox input = new TextBox
            {
                Text = ApplicationOptionSettings.GetString(key),
                Location = new Point(250, top),
                Width = width,
                UseSystemPasswordChar = password
            };
            input.TextChanged += (s, e) => ApplicationOptionSettings.SetString(key, input.Text);
            optionTextBoxes[key] = input;
            contentPanel.Controls.Add(input);
            return input;
        }

        private OptionChoice[] BuildFontChoices()
        {
            List<OptionChoice> choices = new List<OptionChoice>();
            foreach (FontFamily family in FontFamily.Families)
            {
                choices.Add(new OptionChoice(family.Name, family.Name));
            }
            return choices.ToArray();
        }

        private string T(string zh, string en)
        {
            return Localization.IsEnglish ? en : zh;
        }

        private void RenderEnvironmentPage()
        {
            contentPanel.Controls.Clear();
            cliPathInputs.Clear();

            Label sectionTitle = new Label
            {
                Text = Localization.T("Options.Environment"),
                AutoSize = true,
                Font = UiKit.Title,
                Location = new Point(18, 12)
            };
            Label hintLabel = new Label
            {
                Text = Localization.T("Options.CliPathHint"),
                AutoSize = true,
                Location = new Point(18, 48),
                MaximumSize = new Size(620, 0)
            };

            contentPanel.Controls.Add(sectionTitle);
            contentPanel.Controls.Add(hintLabel);

            int top = 92;
            AddCliPathRow("mysql", Localization.T("Options.CliPathMySql"), top);
            AddCliPathRow("postgresql", Localization.T("Options.CliPathPostgreSql"), top + 42);
            AddCliPathRow("sqlserver", Localization.T("Options.CliPathSqlServer"), top + 84);
            AddCliPathRow("oracle", Localization.T("Options.CliPathOracle"), top + 126);
            AddCliPathRow("sqlite", Localization.T("Options.CliPathSqlite"), top + 168);
        }

        private void RenderFileLocationPage()
        {
            ClearOptionPage();
            remoteBackupDirectoryInput = null;
            remoteBackupRetainCountInput = null;
            backupIntegrityScheduleEnabledCheckBox = null;
            backupIntegrityIntervalInput = null;
            backupIntegrityAutoQuarantineCheckBox = null;
            backupIntegrityQuarantineRetainCountInput = null;
            backupRestoreContentSampleRowsInput = null;

            Label sectionTitle = new Label
            {
                Text = Localization.T("Options.FileLocation"),
                AutoSize = true,
                Font = UiKit.Title,
                Location = new Point(18, 12)
            };
            Label hintLabel = new Label
            {
                Text = Localization.T("Options.BackupMirrorHint"),
                AutoSize = true,
                Location = new Point(18, 48),
                MaximumSize = new Size(620, 0)
            };
            Label pathLabel = new Label
            {
                Text = Localization.T("Options.BackupMirrorDirectory"),
                AutoSize = true,
                Location = new Point(18, 105)
            };
            remoteBackupDirectoryInput = new TextBox
            {
                Text = BackupMirrorSettings.RemoteDirectory,
                Location = new Point(150, 100),
                Width = 390,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            Button browseButton = new Button
            {
                Text = Localization.T("Common.Browse"),
                Location = new Point(550, 99),
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            browseButton.Click += (s, e) =>
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = Localization.T("Options.BackupMirrorDirectory");
                    dialog.SelectedPath = Directory.Exists(remoteBackupDirectoryInput.Text) ? remoteBackupDirectoryInput.Text : string.Empty;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        remoteBackupDirectoryInput.Text = dialog.SelectedPath;
                    }
                }
            };
            Label retainLabel = new Label
            {
                Text = Localization.T("Options.BackupMirrorRetainCount"),
                AutoSize = true,
                Location = new Point(18, 150)
            };
            remoteBackupRetainCountInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 999,
                Value = BackupMirrorSettings.RetainCount,
                Location = new Point(150, 146),
                Width = 90
            };
            backupIntegrityScheduleEnabledCheckBox = new CheckBox
            {
                Text = Localization.T("Options.BackupIntegrityScheduleEnabled"),
                AutoSize = true,
                Checked = BackupMirrorSettings.IntegrityScheduleEnabled,
                Location = new Point(150, 196),
                MaximumSize = new Size(560, 0)
            };
            Label intervalLabel = new Label
            {
                Text = Localization.T("Options.BackupIntegrityIntervalHours"),
                AutoSize = true,
                Location = new Point(18, 238)
            };
            backupIntegrityIntervalInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 720,
                Value = BackupMirrorSettings.IntegrityIntervalHours,
                Location = new Point(150, 234),
                Width = 90
            };
            backupIntegrityAutoQuarantineCheckBox = new CheckBox
            {
                Text = Localization.T("Options.BackupIntegrityAutoQuarantine"),
                AutoSize = true,
                Checked = BackupMirrorSettings.IntegrityAutoQuarantineEnabled,
                Location = new Point(150, 278),
                MaximumSize = new Size(560, 0)
            };
            Label quarantineRetainLabel = new Label
            {
                Text = Localization.T("Options.BackupIntegrityQuarantineRetainCount"),
                AutoSize = true,
                Location = new Point(18, 320)
            };
            backupIntegrityQuarantineRetainCountInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 999,
                Value = BackupMirrorSettings.IntegrityQuarantineRetainCount,
                Location = new Point(150, 316),
                Width = 90
            };
            Label restoreSampleRowsLabel = new Label
            {
                Text = Localization.T("Options.RestoreContentSnapshotRows"),
                AutoSize = true,
                Location = new Point(18, 362)
            };
            backupRestoreContentSampleRowsInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = mySQLPunk.lib.BackupRestoreDiffService.MaxConfigurableContentSnapshotRows,
                Value = BackupMirrorSettings.RestoreContentSnapshotMaxRows,
                Location = new Point(150, 358),
                Width = 110
            };
            backupIntegrityScheduleEnabledCheckBox.CheckedChanged += (s, e) =>
            {
                backupIntegrityIntervalInput.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked;
                backupIntegrityAutoQuarantineCheckBox.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked;
                backupIntegrityQuarantineRetainCountInput.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked && backupIntegrityAutoQuarantineCheckBox.Checked;
            };
            backupIntegrityAutoQuarantineCheckBox.CheckedChanged += (s, e) =>
            {
                backupIntegrityQuarantineRetainCountInput.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked && backupIntegrityAutoQuarantineCheckBox.Checked;
            };
            backupIntegrityIntervalInput.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked;
            backupIntegrityAutoQuarantineCheckBox.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked;
            backupIntegrityQuarantineRetainCountInput.Enabled = backupIntegrityScheduleEnabledCheckBox.Checked && backupIntegrityAutoQuarantineCheckBox.Checked;

            contentPanel.Controls.Add(sectionTitle);
            contentPanel.Controls.Add(hintLabel);
            contentPanel.Controls.Add(pathLabel);
            contentPanel.Controls.Add(remoteBackupDirectoryInput);
            contentPanel.Controls.Add(browseButton);
            contentPanel.Controls.Add(retainLabel);
            contentPanel.Controls.Add(remoteBackupRetainCountInput);
            contentPanel.Controls.Add(backupIntegrityScheduleEnabledCheckBox);
            contentPanel.Controls.Add(intervalLabel);
            contentPanel.Controls.Add(backupIntegrityIntervalInput);
            contentPanel.Controls.Add(backupIntegrityAutoQuarantineCheckBox);
            contentPanel.Controls.Add(quarantineRetainLabel);
            contentPanel.Controls.Add(backupIntegrityQuarantineRetainCountInput);
            contentPanel.Controls.Add(restoreSampleRowsLabel);
            contentPanel.Controls.Add(backupRestoreContentSampleRowsInput);

            AddOptionTextBox("FileLogDirectory", T("記錄位置:", "Log folder:"), 412, 390);
            AddOptionTextBox("FileQueryDirectory", T("查詢檔案位置:", "Query folder:"), 454, 390);
            AddOptionTextBox("FileExportDirectory", T("匯出位置:", "Export folder:"), 496, 390);
        }

        private void AddCliPathRow(string provider, string labelText, int top)
        {
            Label label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(18, top + 5)
            };
            TextBox input = new TextBox
            {
                Text = CliPathSettings.GetPath(provider),
                Location = new Point(150, top),
                Width = 390,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            Button browseButton = new Button
            {
                Text = Localization.T("Common.Browse"),
                Location = new Point(550, top - 1),
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            browseButton.Click += (s, e) =>
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = Localization.T("Options.ExecutableFilter");
                    dialog.FileName = string.IsNullOrWhiteSpace(input.Text) ? string.Empty : input.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        input.Text = dialog.FileName;
                    }
                }
            };

            cliPathInputs[provider] = input;
            contentPanel.Controls.Add(label);
            contentPanel.Controls.Add(input);
            contentPanel.Controls.Add(browseButton);
        }

        private void SaveCliPathSettings()
        {
            foreach (var pair in cliPathInputs)
            {
                CliPathSettings.SetPath(pair.Key, pair.Value.Text);
            }
            if (cliPathInputs.Count > 0) CliPathSettings.Save();
        }

        private void SaveTableEditSettings()
        {
            if (noPrimaryKeyReadOnlyCheckBox == null) return;
            TableEditSettings.NoPrimaryKeyReadOnly = noPrimaryKeyReadOnlyCheckBox.Checked;
            TableEditSettings.Save();
        }

        private void SaveBackupMirrorSettings()
        {
            if (remoteBackupDirectoryInput == null) return;
            BackupMirrorSettings.RemoteDirectory = remoteBackupDirectoryInput.Text;
            if (remoteBackupRetainCountInput != null)
            {
                BackupMirrorSettings.RetainCount = (int)remoteBackupRetainCountInput.Value;
            }
            if (backupIntegrityScheduleEnabledCheckBox != null)
            {
                BackupMirrorSettings.IntegrityScheduleEnabled = backupIntegrityScheduleEnabledCheckBox.Checked;
            }
            if (backupIntegrityIntervalInput != null)
            {
                BackupMirrorSettings.IntegrityIntervalHours = (int)backupIntegrityIntervalInput.Value;
            }
            if (backupIntegrityAutoQuarantineCheckBox != null)
            {
                BackupMirrorSettings.IntegrityAutoQuarantineEnabled = backupIntegrityAutoQuarantineCheckBox.Checked;
            }
            if (backupIntegrityQuarantineRetainCountInput != null)
            {
                BackupMirrorSettings.IntegrityQuarantineRetainCount = (int)backupIntegrityQuarantineRetainCountInput.Value;
            }
            if (backupRestoreContentSampleRowsInput != null)
            {
                BackupMirrorSettings.RestoreContentSnapshotMaxRows = (int)backupRestoreContentSampleRowsInput.Value;
            }
            BackupMirrorSettings.Save();
        }

        private void SaveApplicationOptionSettings()
        {
            foreach (var pair in optionCheckBoxes)
            {
                ApplicationOptionSettings.SetBool(pair.Key, pair.Value.Checked);
            }
            foreach (var pair in optionNumbers)
            {
                ApplicationOptionSettings.SetInt(pair.Key, (int)pair.Value.Value);
            }
            foreach (var pair in optionCombos)
            {
                OptionChoice choice = pair.Value.SelectedItem as OptionChoice;
                ApplicationOptionSettings.SetString(pair.Key, choice == null ? string.Empty : choice.Value);
            }
            foreach (var pair in optionTextBoxes)
            {
                ApplicationOptionSettings.SetString(pair.Key, pair.Value.Text);
            }
            ApplicationOptionSettings.Save();
        }

        private void ApplyAdvancedRegistrationSettings()
        {
            try
            {
                mySQLPunk.lib.AdvancedRegistrationService.ApplyFromOptions(Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    BuildAdvancedRegistrationApplyFailedMessage(ex),
                    Localization.T("Common.Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        public static string BuildAdvancedRegistrationApplyFailedMessage(Exception ex)
        {
            string reason = ex == null ? null : ex.Message;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = Localization.T("Object.UnknownError");
            }
            else
            {
                reason = reason.Trim();
            }

            return (Localization.IsEnglish
                ? "Failed to apply advanced registration settings: "
                : "套用進階註冊設定失敗：") + reason;
        }

        private void UpdateSelection()
        {
            SelectedTheme = darkThemeRadio.Checked ? ThemeManager.Dark : ThemeManager.Light;
            LanguageItem item = languageCombo.SelectedItem as LanguageItem;
            SelectedLanguage = item == null ? Localization.TraditionalChinese : item.Value;
            lightPreview.Selected = SelectedTheme == ThemeManager.Light;
            darkPreview.Selected = SelectedTheme == ThemeManager.Dark;
            lightPreview.Invalidate();
            darkPreview.Invalidate();
        }

        private class LanguageItem
        {
            public string Text { get; private set; }
            public string Value { get; private set; }

            public LanguageItem(string text, string value)
            {
                Text = text;
                Value = value;
            }

            public override string ToString()
            {
                return Text;
            }
        }

        private class OptionChoice
        {
            public string Value { get; private set; }
            public string Text { get; private set; }

            public OptionChoice(string value, string text)
            {
                Value = value;
                Text = text;
            }

            public override string ToString()
            {
                return Text;
            }
        }

        private sealed class AiCliCardPanel : Panel
        {
            private bool isSelected;

            public bool IsSelected
            {
                get { return isSelected; }
                set
                {
                    isSelected = value;
                    Invalidate();
                }
            }

            public AiCliCardPanel()
            {
                DoubleBuffered = true;
                BackColor = ThemeManager.SurfaceColor;
            }

            protected override void OnResize(EventArgs eventArgs)
            {
                base.OnResize(eventArgs);
                if (Width <= 0 || Height <= 0) return;
                using (System.Drawing.Drawing2D.GraphicsPath path = UiKit.RoundedRect(
                    new RectangleF(0, 0, Width, Height), UiMetrics.RadiusLg))
                {
                    Region oldRegion = Region;
                    Region = new Region(path);
                    if (oldRegion != null) oldRegion.Dispose();
                }
            }

            protected override void OnPaintBackground(PaintEventArgs eventArgs)
            {
                eventArgs.Graphics.Clear(ThemeManager.SurfaceColor);
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                Rectangle border = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                UiKit.DrawRounded(eventArgs.Graphics, border, UiMetrics.RadiusLg,
                    IsSelected ? ThemeManager.AccentColor : ThemeManager.BorderColor,
                    IsSelected ? 2f : 1f);
            }
        }

        private sealed class AiStatusPill : Control
        {
            public bool Positive { get; set; }

            public AiStatusPill()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Font = UiKit.Caption;
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                Color back = Positive
                    ? UiKit.Mix(ThemeManager.SurfaceColor, ThemeManager.SuccessColor, ThemeManager.IsDark ? 0.24f : 0.12f)
                    : UiKit.Mix(ThemeManager.SurfaceColor, ThemeManager.MutedTextColor, ThemeManager.IsDark ? 0.18f : 0.08f);
                Color fore = Positive ? ThemeManager.SuccessColor : ThemeManager.MutedTextColor;
                UiKit.FillRounded(eventArgs.Graphics,
                    new RectangleF(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)),
                    UiMetrics.RadiusPill,
                    back);
                TextRenderer.DrawText(eventArgs.Graphics, Text, Font, ClientRectangle, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class AiCliGlyph : Control
        {
            public AiCliGlyph()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                RectangleF bounds = new RectangleF(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                UiKit.FillRounded(eventArgs.Graphics, bounds, UiMetrics.RadiusMd, ThemeManager.AccentSoftColor);
                UiKit.DrawGlyph(eventArgs.Graphics, UiGlyph.Code, RectangleF.Inflate(bounds, -7, -7), ThemeManager.AccentColor, 1.1f);
            }
        }

        private class ThemePreviewControl : Control
        {
            private readonly string previewTheme;

            public bool Selected { get; set; }

            public ThemePreviewControl(string theme)
            {
                previewTheme = theme;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                bool dark = previewTheme == ThemeManager.Dark;
                Color window = dark ? Color.FromArgb(30, 34, 38) : Color.White;
                Color surface = dark ? Color.FromArgb(38, 43, 48) : Color.FromArgb(245, 245, 245);
                Color elevated = dark ? Color.FromArgb(45, 51, 57) : Color.White;
                Color text = dark ? Color.FromArgb(235, 240, 244) : Color.FromArgb(51, 51, 51);
                Color muted = dark ? Color.FromArgb(170, 181, 189) : Color.FromArgb(105, 105, 105);
                Color accent = dark ? Color.FromArgb(80, 170, 220) : Color.FromArgb(0, 120, 212);
                Color grid = dark ? Color.FromArgb(58, 65, 72) : Color.FromArgb(220, 228, 232);

                Rectangle outer = new Rectangle(0, 0, Width - 1, Height - 1);
                using (SolidBrush brush = new SolidBrush(window))
                using (Pen border = new Pen(Selected ? accent : grid, Selected ? 3 : 1))
                {
                    e.Graphics.FillRectangle(brush, outer);
                    e.Graphics.DrawRectangle(border, outer);
                }

                using (SolidBrush brush = new SolidBrush(surface))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(1, 1, Width - 2, 18));
                    e.Graphics.FillRectangle(brush, new Rectangle(1, 19, 48, Height - 20));
                }

                using (SolidBrush brush = new SolidBrush(elevated))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(54, 25, Width - 62, Height - 34));
                }

                DrawCircle(e.Graphics, 12, 10, Color.FromArgb(70, 170, 90));
                DrawCircle(e.Graphics, 32, 10, Color.FromArgb(50, 150, 210));
                DrawCircle(e.Graphics, 52, 10, Color.FromArgb(240, 170, 60));

                using (Pen pen = new Pen(accent, 2))
                {
                    e.Graphics.DrawLine(pen, 65, 10, 78, 10);
                    e.Graphics.DrawLine(pen, 88, 10, 101, 10);
                    e.Graphics.DrawLine(pen, 111, 10, 124, 10);
                }

                using (SolidBrush brush = new SolidBrush(text))
                using (Font font = new Font("Segoe UI", 5.5f))
                {
                    e.Graphics.DrawString("mySQLPunk", font, brush, 6, 27);
                    e.Graphics.DrawString("Tables", font, brush, 10, 45);
                    e.Graphics.DrawString("Views", font, brush, 10, 60);
                }

                using (Pen pen = new Pen(grid, 1))
                {
                    for (int x = 62; x < Width - 10; x += 18)
                    {
                        e.Graphics.DrawLine(pen, x, 30, x, Height - 14);
                    }
                    for (int y = 36; y < Height - 12; y += 14)
                    {
                        e.Graphics.DrawLine(pen, 58, y, Width - 9, y);
                    }
                }

                using (SolidBrush brush = new SolidBrush(accent))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(67, 58, 6, 20));
                    e.Graphics.FillRectangle(brush, new Rectangle(82, 47, 6, 31));
                    e.Graphics.FillRectangle(brush, new Rectangle(97, 54, 6, 24));
                    e.Graphics.FillRectangle(brush, new Rectangle(112, 41, 6, 37));
                }

                using (SolidBrush brush = new SolidBrush(muted))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(128, 50, 6, 28));
                }
            }

            private static void DrawCircle(Graphics graphics, int x, int y, Color color)
            {
                using (SolidBrush brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, x - 4, y - 4, 8, 8);
                }
            }
        }
    }

    public static class ApplicationOptionSettings
    {
        private static readonly Dictionary<string, bool> BoolValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> IntValues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> StringValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static bool GetBool(string key)
        {
            EnsureLoaded();
            bool value;
            return BoolValues.TryGetValue(key, out value) ? value : GetDefaultBool(key);
        }

        public static int GetInt(string key)
        {
            EnsureLoaded();
            int value;
            return IntValues.TryGetValue(key, out value) ? value : GetDefaultInt(key);
        }

        public static string GetString(string key)
        {
            EnsureLoaded();
            string value;
            return StringValues.TryGetValue(key, out value) ? value : GetDefaultString(key);
        }

        public static bool GetAiPanelStartupVisibility()
        {
            return ResolveAiPanelStartupVisibility(GetString("ViewAiPanelVisibilityPreference"));
        }

        public static bool ResolveAiPanelStartupVisibility(string preference)
        {
            string normalized = (preference ?? string.Empty).Trim();
            if (string.Equals(normalized, "closed", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static void SetAiPanelVisibilityPreference(bool visible)
        {
            SetBool("ViewShowAiPanel", visible);
            SetString("ViewAiPanelVisibilityPreference", visible ? "open" : "closed");
        }

        public static void SetBool(string key, bool value)
        {
            EnsureLoaded();
            BoolValues[key] = value;
        }

        public static void SetInt(string key, int value)
        {
            EnsureLoaded();
            IntValues[key] = value;
        }

        public static void SetString(string key, string value)
        {
            EnsureLoaded();
            StringValues[key] = (value ?? string.Empty).Trim();
        }

        public static string LastSaveErrorMessage;

        public static void Save()
        {
            EnsureLoaded();
            LastSaveErrorMessage = null;
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new SettingsData
                {
                    BoolValues = BoolValues,
                    IntValues = IntValues,
                    StringValues = StringValues
                }, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LastSaveErrorMessage = ex.Message;
            }
        }

        public static void ClearAutoCompleteCache()
        {
            try
            {
                string path = Path.Combine(Application.UserAppDataPath, "autocomplete-cache.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            SeedDefaults();

            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path)) return;

                SettingsData data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(path));
                if (data == null) return;

                Merge(data.BoolValues, BoolValues);
                Merge(data.IntValues, IntValues);
                Merge(data.StringValues, StringValues);
            }
            catch
            {
                BoolValues.Clear();
                IntValues.Clear();
                StringValues.Clear();
                SeedDefaults();
            }
        }

        private static void SeedDefaults()
        {
            BoolValues["AllowDuplicateObjects"] = false;
            BoolValues["ShowObjectTooltips"] = true;
            BoolValues["ShowFunctionWizard"] = true;
            BoolValues["RememberQuerySettings"] = true;
            BoolValues["RememberTableSettings"] = true;
            BoolValues["UseSafeMode"] = true;
            BoolValues["AutoCheckUpdates"] = true;
            BoolValues["ShowStructureInNavigation"] = true;
            BoolValues["ShowTablesUnderGroups"] = true;
            BoolValues["ViewShowNavigationPane"] = true;
            BoolValues["ViewHideConnectionGroups"] = false;
            BoolValues["ViewActiveObjectsOnly"] = false;
            BoolValues["ViewShowTopFilter"] = false;
            BoolValues["ViewShowInfoPane"] = true;
            BoolValues["ViewInfoPaneAiMode"] = false;
            BoolValues["ViewHideObjectGroups"] = false;
            BoolValues["ViewShowHiddenItems"] = false;
            BoolValues["ViewSortDescending"] = false;
            BoolValues["SingleClickExpandsTree"] = false;
            BoolValues["AutoCompleteEnabled"] = true;
            BoolValues["AutoCompleteAutoRefresh"] = true;
            BoolValues["AutoCompleteIncludeSystemObjects"] = true;
            BoolValues["AutoCompleteSelectFirst"] = true;
            BoolValues["EditorShowLineNumbers"] = true;
            BoolValues["EditorCodeFolding"] = true;
            BoolValues["EditorHighlightBrackets"] = true;
            BoolValues["EditorSyntaxHighlight"] = true;
            BoolValues["EditorWordWrap"] = true;
            BoolValues["EditorInsertSpaces"] = true;
            BoolValues["RecordLimitEnabled"] = true;
            BoolValues["RecordAutoBeginTransaction"] = false;
            BoolValues["RecordShowThousandsSeparator"] = false;
            BoolValues["RecordUseSystemNumberFormat"] = true;
            BoolValues["AiAssistantEnabled"] = false;
            BoolValues["ViewShowAiPanel"] = true;
            BoolValues["AutoRecoveryQueryEnabled"] = true;
            BoolValues["AutoRecoveryTableDesignEnabled"] = true;
            BoolValues["ConnectionValidateCertificates"] = true;
            BoolValues["ConnectionUseProxy"] = false;
            BoolValues["AdvancedEnableDiagnosticsLog"] = false;
            BoolValues["AdvancedAllowMultipleInstances"] = true; // 多開是長期以來的既有行為，預設不能拿掉
            BoolValues["AdvancedRegisterSqlFileOpen"] = false;
            BoolValues["AdvancedRegisterUrlProtocol"] = false;

            IntValues["EditorLargeFileLimitMb"] = 10;
            IntValues["EditorTabWidth"] = 2;
            IntValues["EditorFontSize"] = 10;
            IntValues["RecordLimit"] = 1000;
            IntValues["RecordGridFontSize"] = 9;
            IntValues["AutoRecoveryIntervalSeconds"] = 30;
            IntValues["ConnectionProxyPort"] = 8080;

            StringValues["IndexOpenTarget"] = "main";
            StringValues["StartupView"] = "connections";
            StringValues["ViewObjectListMode"] = "details";
            StringValues["ViewSortColumn"] = "名稱";
            StringValues["ViewAiPanelVisibilityPreference"] = "";
            StringValues["EditorFontName"] = "Consolas";
            StringValues["RecordGridFontName"] = "Microsoft JhengHei UI";
            StringValues["RecordRowHeightMode"] = "single";
            StringValues["RecordDateFormat"] = "";
            StringValues["RecordTimeFormat"] = "";
            StringValues["RecordDateTimeFormat"] = "";
            StringValues["AiProvider"] = "openai";
            StringValues["AiEndpoint"] = "";
            StringValues["AiModel"] = "";
            StringValues["ConnectionProxyType"] = "http";
            StringValues["ConnectionProxyHost"] = "";
            StringValues["ConnectionProxyUser"] = "";
            StringValues["ConnectionProxyPassword"] = "";

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) documents = Application.UserAppDataPath;
            StringValues["FileLogDirectory"] = Path.Combine(documents, "mySQLPunk", "logs");
            StringValues["FileQueryDirectory"] = Path.Combine(documents, "mySQLPunk", "queries");
            StringValues["FileExportDirectory"] = Path.Combine(documents, "mySQLPunk", "exports");
        }

        private static bool GetDefaultBool(string key)
        {
            bool value;
            return BoolValues.TryGetValue(key, out value) && value;
        }

        private static int GetDefaultInt(string key)
        {
            int value;
            return IntValues.TryGetValue(key, out value) ? value : 0;
        }

        private static string GetDefaultString(string key)
        {
            string value;
            return StringValues.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static void Merge<T>(Dictionary<string, T> source, Dictionary<string, T> target)
        {
            if (source == null) return;
            foreach (var pair in source)
            {
                target[pair.Key] = pair.Value;
            }
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "application-options.json");
        }

        private class SettingsData
        {
            public Dictionary<string, bool> BoolValues { get; set; }
            public Dictionary<string, int> IntValues { get; set; }
            public Dictionary<string, string> StringValues { get; set; }
        }
    }

    public static class CliPathSettings
    {
        private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static string GetPath(string provider)
        {
            EnsureLoaded();
            string value;
            return Paths.TryGetValue(NormalizeProvider(provider), out value) ? value : string.Empty;
        }

        public static void SetPath(string provider, string path)
        {
            EnsureLoaded();
            string key = NormalizeProvider(provider);
            string value = (path ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                Paths.Remove(key);
            }
            else
            {
                Paths[key] = value;
            }
        }

        public static string LastSaveErrorMessage;

        public static void Save()
        {
            EnsureLoaded();
            LastSaveErrorMessage = null;
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(Paths, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LastSaveErrorMessage = ex.Message;
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Paths.Clear();

            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path)) return;

                var loadedPaths = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                if (loadedPaths == null) return;

                foreach (var pair in loadedPaths)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        Paths[NormalizeProvider(pair.Key)] = pair.Value.Trim();
                    }
                }
            }
            catch
            {
                Paths.Clear();
            }
        }

        private static string NormalizeProvider(string provider)
        {
            string key = (provider ?? string.Empty).Trim().ToLowerInvariant();
            return key == "mssql" ? "sqlserver" : key;
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "cli-paths.json");
        }
    }

    public static class TableEditSettings
    {
        private static bool loaded;
        private static bool noPrimaryKeyReadOnly;

        public static bool NoPrimaryKeyReadOnly
        {
            get
            {
                EnsureLoaded();
                return noPrimaryKeyReadOnly;
            }
            set
            {
                EnsureLoaded();
                noPrimaryKeyReadOnly = value;
            }
        }

        public static string LastSaveErrorMessage;

        public static void Save()
        {
            EnsureLoaded();
            LastSaveErrorMessage = null;
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new SettingsData
                {
                    NoPrimaryKeyReadOnly = noPrimaryKeyReadOnly
                }, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LastSaveErrorMessage = ex.Message;
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;

            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path)) return;

                SettingsData data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(path));
                if (data != null)
                {
                    noPrimaryKeyReadOnly = data.NoPrimaryKeyReadOnly;
                }
            }
            catch
            {
                noPrimaryKeyReadOnly = false;
            }
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "table-edit-settings.json");
        }

        private class SettingsData
        {
            public bool NoPrimaryKeyReadOnly { get; set; }
        }
    }

    public static class BackupMirrorSettings
    {
        private static bool loaded;
        private static string remoteDirectory = string.Empty;
        private static int retainCount = mySQLPunk.lib.BackupRemoteMirrorService.DefaultRetainCount;
        private static bool integrityScheduleEnabled = true;
        private static bool integrityAutoQuarantineEnabled = false;
        private static int integrityIntervalHours = mySQLPunk.lib.BackupIntegrityScheduleService.DefaultIntervalHours;
        private static int integrityQuarantineRetainCount = 50;
        private static int restoreContentSnapshotMaxRows = mySQLPunk.lib.BackupRestoreDiffService.MaxContentSnapshotRows;
        private static DateTime lastIntegrityVerifiedUtc = DateTime.MinValue;
        private static string lastIntegrityReportPath = string.Empty;

        public static string RemoteDirectory
        {
            get
            {
                EnsureLoaded();
                return remoteDirectory;
            }
            set
            {
                EnsureLoaded();
                remoteDirectory = (value ?? string.Empty).Trim();
            }
        }

        public static int RetainCount
        {
            get
            {
                EnsureLoaded();
                return retainCount;
            }
            set
            {
                EnsureLoaded();
                retainCount = Math.Max(1, value);
            }
        }

        public static bool IntegrityScheduleEnabled
        {
            get
            {
                EnsureLoaded();
                return integrityScheduleEnabled;
            }
            set
            {
                EnsureLoaded();
                integrityScheduleEnabled = value;
            }
        }

        public static int IntegrityIntervalHours
        {
            get
            {
                EnsureLoaded();
                return integrityIntervalHours;
            }
            set
            {
                EnsureLoaded();
                integrityIntervalHours = Math.Max(1, value);
            }
        }

        public static bool IntegrityAutoQuarantineEnabled
        {
            get
            {
                EnsureLoaded();
                return integrityAutoQuarantineEnabled;
            }
            set
            {
                EnsureLoaded();
                integrityAutoQuarantineEnabled = value;
            }
        }

        public static int IntegrityQuarantineRetainCount
        {
            get
            {
                EnsureLoaded();
                return integrityQuarantineRetainCount;
            }
            set
            {
                EnsureLoaded();
                integrityQuarantineRetainCount = Math.Max(1, value);
            }
        }

        public static int RestoreContentSnapshotMaxRows
        {
            get
            {
                EnsureLoaded();
                return restoreContentSnapshotMaxRows;
            }
            set
            {
                EnsureLoaded();
                restoreContentSnapshotMaxRows = mySQLPunk.lib.BackupRestoreDiffService.ResolveMaxContentSnapshotRows(value);
            }
        }

        public static DateTime LastIntegrityVerifiedUtc
        {
            get
            {
                EnsureLoaded();
                return lastIntegrityVerifiedUtc;
            }
            set
            {
                EnsureLoaded();
                lastIntegrityVerifiedUtc = value == DateTime.MinValue ? DateTime.MinValue : value.ToUniversalTime();
            }
        }

        public static string LastIntegrityReportPath
        {
            get
            {
                EnsureLoaded();
                return lastIntegrityReportPath;
            }
            set
            {
                EnsureLoaded();
                lastIntegrityReportPath = (value ?? string.Empty).Trim();
            }
        }

        public static string LastSaveErrorMessage;

        public static void Save()
        {
            EnsureLoaded();
            LastSaveErrorMessage = null;
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new SettingsData
                {
                    RemoteDirectory = remoteDirectory,
                    RetainCount = retainCount,
                    IntegrityScheduleEnabled = integrityScheduleEnabled,
                    IntegrityAutoQuarantineEnabled = integrityAutoQuarantineEnabled,
                    IntegrityIntervalHours = integrityIntervalHours,
                    IntegrityQuarantineRetainCount = integrityQuarantineRetainCount,
                    RestoreContentSnapshotMaxRows = restoreContentSnapshotMaxRows,
                    LastIntegrityVerifiedUtc = lastIntegrityVerifiedUtc,
                    LastIntegrityReportPath = lastIntegrityReportPath
                }, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LastSaveErrorMessage = ex.Message;
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;

            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path)) return;

                SettingsData data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(path));
                if (data != null)
                {
                    remoteDirectory = (data.RemoteDirectory ?? string.Empty).Trim();
                    retainCount = data.RetainCount <= 0
                        ? mySQLPunk.lib.BackupRemoteMirrorService.DefaultRetainCount
                        : data.RetainCount;
                    integrityScheduleEnabled = data.IntegrityScheduleEnabled.HasValue
                        ? data.IntegrityScheduleEnabled.Value
                        : true;
                    integrityAutoQuarantineEnabled = data.IntegrityAutoQuarantineEnabled.HasValue
                        ? data.IntegrityAutoQuarantineEnabled.Value
                        : false;
                    integrityIntervalHours = data.IntegrityIntervalHours <= 0
                        ? mySQLPunk.lib.BackupIntegrityScheduleService.DefaultIntervalHours
                        : data.IntegrityIntervalHours;
                    integrityQuarantineRetainCount = data.IntegrityQuarantineRetainCount <= 0
                        ? 50
                        : data.IntegrityQuarantineRetainCount;
                    restoreContentSnapshotMaxRows = mySQLPunk.lib.BackupRestoreDiffService.ResolveMaxContentSnapshotRows(data.RestoreContentSnapshotMaxRows);
                    lastIntegrityVerifiedUtc = data.LastIntegrityVerifiedUtc == DateTime.MinValue
                        ? DateTime.MinValue
                        : data.LastIntegrityVerifiedUtc.ToUniversalTime();
                    lastIntegrityReportPath = (data.LastIntegrityReportPath ?? string.Empty).Trim();
                }
            }
            catch
            {
                remoteDirectory = string.Empty;
                retainCount = mySQLPunk.lib.BackupRemoteMirrorService.DefaultRetainCount;
                integrityScheduleEnabled = true;
                integrityAutoQuarantineEnabled = false;
                integrityIntervalHours = mySQLPunk.lib.BackupIntegrityScheduleService.DefaultIntervalHours;
                integrityQuarantineRetainCount = 50;
                restoreContentSnapshotMaxRows = mySQLPunk.lib.BackupRestoreDiffService.MaxContentSnapshotRows;
                lastIntegrityVerifiedUtc = DateTime.MinValue;
                lastIntegrityReportPath = string.Empty;
            }
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "backup-mirror-settings.json");
        }

        private class SettingsData
        {
            public string RemoteDirectory { get; set; }
            public int RetainCount { get; set; }
            public bool? IntegrityScheduleEnabled { get; set; }
            public bool? IntegrityAutoQuarantineEnabled { get; set; }
            public int IntegrityIntervalHours { get; set; }
            public int IntegrityQuarantineRetainCount { get; set; }
            public int RestoreContentSnapshotMaxRows { get; set; }
            public DateTime LastIntegrityVerifiedUtc { get; set; }
            public string LastIntegrityReportPath { get; set; }
        }
    }
}

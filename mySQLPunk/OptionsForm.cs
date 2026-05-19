using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace mySQLPunk
{
    public class OptionsForm : Form
    {
        private readonly ListBox navigationList;
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
                Dock = DockStyle.Left,
                Width = 160,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            navigationList.Items.AddRange(new object[]
            {
                Localization.T("Options.General"),
                Localization.T("Options.Navigation"),
                Localization.T("Options.AutoComplete"),
                Localization.T("Options.Editor"),
                Localization.T("Options.Record"),
                Localization.T("Options.AI"),
                Localization.T("Options.AutoRecovery"),
                Localization.T("Options.FileLocation"),
                Localization.T("Options.Connection"),
                Localization.T("Options.Environment"),
                Localization.T("Options.Advanced")
            });
            navigationList.SelectedIndex = 0;
            navigationList.SelectedIndexChanged += (s, e) => RenderSelectedPage();

            contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                AutoScroll = true
            };

            Panel buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                Padding = new Padding(12, 8, 12, 8)
            };
            okButton = new Button
            {
                Text = Localization.T("Common.OK"),
                DialogResult = DialogResult.OK,
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            okButton.Location = new Point(buttonPanel.Width - 184, 12);
            cancelButton.Location = new Point(buttonPanel.Width - 94, 12);
            buttonPanel.Resize += (s, e) =>
            {
                okButton.Location = new Point(buttonPanel.Width - 184, 12);
                cancelButton.Location = new Point(buttonPanel.Width - 94, 12);
            };
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
            };

            RenderGeneralPage();

            Controls.Add(contentPanel);
            Controls.Add(navigationList);
            Controls.Add(buttonPanel);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            ThemeManager.ApplyTo(this);
            navigationList.BackColor = ThemeManager.ElevatedColor;
            navigationList.ForeColor = ThemeManager.TextColor;
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
            else if (string.Equals(selected, Localization.T("Options.AI"), StringComparison.Ordinal))
            {
                RenderAiPage();
            }
            else if (string.Equals(selected, Localization.T("Options.AutoRecovery"), StringComparison.Ordinal))
            {
                RenderAutoRecoveryPage();
            }
            else if (string.Equals(selected, Localization.T("Options.Connection"), StringComparison.Ordinal))
            {
                RenderConnectivityPage();
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
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                Location = new Point(18, 18)
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
                Location = new Point(105, 300),
                MaximumSize = new Size(600, 0)
            };
            AddOptionCheckBox("AllowDuplicateObjects", T("允許重複開啟相同的物件", "Allow opening the same object more than once"), 340);
            AddOptionCheckBox("ShowObjectTooltips", T("顯示工具提示", "Show tooltips"), 372);
            AddOptionCheckBox("ShowFunctionWizard", T("顯示函式精靈", "Show function wizard"), 404);
            AddOptionCheckBox("RememberQuerySettings", T("開啟前提示儲存新增的查詢或設定檔", "Prompt before opening when unsaved query settings exist"), 436);
            AddOptionCheckBox("RememberTableSettings", T("開啟前提示儲存新增的資料表設定檔", "Prompt before opening when unsaved table settings exist"), 468);
            AddOptionCheckBox("UseSafeMode", T("使用安全確認對話方塊", "Use safe confirmation dialogs"), 500);
            AddOptionCheckBox("AutoCheckUpdates", T("啟動時自動檢查更新", "Check for updates on startup"), 532);

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

            AddOptionCombo("StartupView", T("起始畫面:", "Startup view:"), new[]
            {
                new OptionChoice("connections", T("連線清單", "Connection list")),
                new OptionChoice("last", T("繼續上次開啟的畫面", "Continue previous workspace")),
                new OptionChoice("favorites", T("最愛與最近項目", "Favorites and recent items"))
            }, 112, 210);

            AddOptionCheckBox("ShowStructureInNavigation", T("在導覽窗格顯示物件結構描述", "Show object structure in the navigation pane"), 170);
            AddOptionCheckBox("ShowTablesUnderGroups", T("在導覽窗格中的資料表下顯示物件", "Show objects under table groups in the navigation pane"), 202);
            AddOptionCheckBox("SingleClickExpandsTree", T("單擊節點時展開資料庫樹狀清單", "Expand database tree nodes on single click"), 234);
        }

        private void RenderAutoCompletePage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.AutoComplete"));
            AddOptionCheckBox("AutoCompleteEnabled", T("使用自動完成程式碼", "Use code completion"), 60);
            AddOptionCheckBox("AutoCompleteAutoRefresh", T("自動更新自動完成資訊", "Automatically refresh completion metadata"), 92);
            AddOptionCheckBox("AutoCompleteIncludeSystemObjects", T("更新自動完成資訊時包含系統物件", "Include system objects when refreshing completion metadata"), 124);
            AddOptionCheckBox("AutoCompleteSelectFirst", T("自動選取第一個建議項目", "Automatically select the first suggestion"), 156);

            Button clearButton = new Button
            {
                Text = T("清除自動完成資料", "Clear completion data"),
                Location = new Point(430, 58),
                Size = new Size(200, 30)
            };
            clearButton.Click += (s, e) =>
            {
                ApplicationOptionSettings.ClearAutoCompleteCache();
                MessageBox.Show(T("自動完成資料已清除。", "Completion data cleared."), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            contentPanel.Controls.Add(clearButton);
        }

        private void RenderEditorPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Editor"));
            AddOptionCheckBox("EditorShowLineNumbers", T("顯示行號", "Show line numbers"), 60);
            AddOptionCheckBox("EditorCodeFolding", T("使用程式碼摺疊", "Use code folding"), 92);
            AddOptionCheckBox("EditorHighlightBrackets", T("使用括號突顯", "Highlight matching brackets"), 124);
            AddOptionCheckBox("EditorSyntaxHighlight", T("使用語法突顯", "Use syntax highlighting"), 156);
            AddOptionNumeric("EditorLargeFileLimitMb", T("如果檔案大小大於此就停用 (MB):", "Disable editor helpers above file size (MB):"), 188, 1, 4096);
            AddOptionCheckBox("EditorWordWrap", T("使用自動換行", "Use word wrap"), 220);
            AddOptionNumeric("EditorTabWidth", T("定位點寬度:", "Tab width:"), 252, 1, 16);
            AddOptionCheckBox("EditorInsertSpaces", T("按 Tab 時插入空格", "Insert spaces when pressing Tab"), 284);

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
            AddOptionCheckBox("AiAssistantEnabled", T("啟用 AI 助理入口", "Enable AI assistant entry point"), 72);
            AddOptionCombo("AiProvider", T("服務提供方式:", "Service provider:"), new[]
            {
                new OptionChoice("none", T("尚未設定", "Not configured")),
                new OptionChoice("local", T("本機或團隊服務", "Local or team service")),
                new OptionChoice("custom", T("自訂 API", "Custom API"))
            }, 112, 220);
            AddOptionTextBox("AiEndpoint", T("端點 URL:", "Endpoint URL:"), 158, 360);

            Label hint = new Label
            {
                Text = T("此頁會保留 AI 助理設定入口；實際服務整合時會避免在服務名稱與文案中使用競品字樣。", "This page keeps the AI assistant settings entry point; service names and copy avoid competitor wording."),
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                Location = new Point(18, 210)
            };
            contentPanel.Controls.Add(hint);
        }

        private void RenderAutoRecoveryPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.AutoRecovery"));
            AddOptionCheckBox("AutoRecoveryQueryEnabled", T("查詢", "Query"), 60);
            AddOptionNumeric("AutoRecoveryIntervalSeconds", T("自動儲存間隔（秒）:", "Auto-save interval (seconds):"), 94, 5, 3600);
            AddOptionCheckBox("AutoRecoveryTableDesignEnabled", T("資料表設計", "Table design"), 132);
        }

        private void RenderConnectivityPage()
        {
            ClearOptionPage();
            AddOptionTitle(Localization.T("Options.Connection"));
            AddOptionCheckBox("ConnectionValidateCertificates", T("驗證伺服器憑證", "Validate server certificates"), 60);
            AddOptionCheckBox("ConnectionUseProxy", T("使用代理伺服器", "Use proxy server"), 108);
            AddOptionCombo("ConnectionProxyType", T("代理伺服器類型:", "Proxy type:"), new[]
            {
                new OptionChoice("http", "HTTP"),
                new OptionChoice("socks5", "SOCKS5")
            }, 142, 160);
            AddOptionTextBox("ConnectionProxyHost", T("主機:", "Host:"), 184, 300);
            AddOptionNumeric("ConnectionProxyPort", T("通訊埠:", "Port:"), 226, 1, 65535);
            AddOptionTextBox("ConnectionProxyUser", T("使用者名稱:", "User name:"), 268, 240);
            AddOptionTextBox("ConnectionProxyPassword", T("密碼:", "Password:"), 310, 240, true);

            Button testButton = new Button
            {
                Text = T("測試連線能力", "Test connectivity"),
                Location = new Point(430, 348),
                Size = new Size(150, 30)
            };
            testButton.Click += (s, e) => MessageBox.Show(T("連線能力測試入口已就緒。", "Connectivity test entry point is ready."), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            contentPanel.Controls.Add(testButton);
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
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                Location = new Point(18, 18)
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
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                Location = new Point(18, 18)
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

            Label sectionTitle = new Label
            {
                Text = Localization.T("Options.FileLocation"),
                AutoSize = true,
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                Location = new Point(18, 18)
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

            AddOptionTextBox("FileLogDirectory", T("記錄位置:", "Log folder:"), 370, 390);
            AddOptionTextBox("FileQueryDirectory", T("查詢檔案位置:", "Query folder:"), 412, 390);
            AddOptionTextBox("FileExportDirectory", T("匯出位置:", "Export folder:"), 454, 390);
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
                    T("套用進階註冊設定失敗：", "Failed to apply advanced registration settings: ") + ex.Message,
                    Localization.T("Common.Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
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

        public static void Save()
        {
            EnsureLoaded();
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
            catch
            {
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
            BoolValues["AutoRecoveryQueryEnabled"] = true;
            BoolValues["AutoRecoveryTableDesignEnabled"] = true;
            BoolValues["ConnectionValidateCertificates"] = true;
            BoolValues["ConnectionUseProxy"] = false;
            BoolValues["AdvancedEnableDiagnosticsLog"] = false;
            BoolValues["AdvancedAllowMultipleInstances"] = false;
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
            StringValues["EditorFontName"] = "Consolas";
            StringValues["RecordGridFontName"] = "Microsoft JhengHei UI";
            StringValues["RecordRowHeightMode"] = "single";
            StringValues["RecordDateFormat"] = "";
            StringValues["RecordTimeFormat"] = "";
            StringValues["RecordDateTimeFormat"] = "";
            StringValues["AiProvider"] = "none";
            StringValues["AiEndpoint"] = "";
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

        public static void Save()
        {
            EnsureLoaded();
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(Paths, Formatting.Indented));
            }
            catch
            {
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

        public static void Save()
        {
            EnsureLoaded();
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new SettingsData
                {
                    NoPrimaryKeyReadOnly = noPrimaryKeyReadOnly
                }, Formatting.Indented));
            }
            catch
            {
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

        public static void Save()
        {
            EnsureLoaded();
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
                    LastIntegrityVerifiedUtc = lastIntegrityVerifiedUtc,
                    LastIntegrityReportPath = lastIntegrityReportPath
                }, Formatting.Indented));
            }
            catch
            {
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
            public DateTime LastIntegrityVerifiedUtc { get; set; }
            public string LastIntegrityReportPath { get; set; }
        }
    }
}

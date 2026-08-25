using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// AI 助理右側面板（Navicat / SSMS Copilot 式的停靠聊天窗）。
    /// 走 OpenAI 相容 API（GitHub Models / OpenAI / Ollama / 自訂），
    /// 可附上目前連線的資料庫結構當上下文，回覆裡的 SQL 一鍵插入查詢分頁。
    /// </summary>
    public class AiAssistantPanel : Panel
    {
        private readonly Func<string> _contextProvider;
        private readonly Action<string> _insertSqlAction;
        private readonly Action _openSettingsAction;

        private Label titleLabel;
        private Button settingsButton;
        private Button closeButton;
        private Panel headerPanel;
        private RichTextBox chatBox;
        private FlowLayoutPanel suggestionPanel;
        private Panel actionPanel;
        private Button insertSqlButton;
        private Panel inputPanel;
        private TextBox inputBox;
        private UiInputShell inputShell;
        private Button sendButton;
        private CheckBox includeContextBox;

        private readonly List<AiChatMessage> _history = new List<AiChatMessage>();
        private bool _busy;
        private string _lastAssistantSql;

        public AiAssistantPanel(Func<string> contextProvider, Action<string> insertSqlAction, Action closeAction, Action openSettingsAction)
        {
            _contextProvider = contextProvider;
            _insertSqlAction = insertSqlAction;
            _openSettingsAction = openSettingsAction;

            Width = 380;
            MinimumSize = new Size(280, 0);

            // ── 標題列（看板娘 Punky 頭像 + 名稱）──
            headerPanel = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(10, 0, 4, 0) };
            PictureBox avatarBox = new PictureBox
            {
                Dock = DockStyle.Left,
                Width = 30,
                SizeMode = PictureBoxSizeMode.CenterImage
            };
            try
            {
                using (Icon icon = new Icon(AppIconService.AppIcon, 24, 24))
                {
                    avatarBox.Image = icon.ToBitmap();
                }
            }
            catch { }
            titleLabel = new Label
            {
                Text = Localization.T("Ai.PanelTitle"),
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = UiKit.BodyBold,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 10, 0, 0)
            };
            closeButton = new Button { Text = "✕", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, TabStop = false };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.Click += (s, e) => closeAction?.Invoke();
            settingsButton = new Button { Text = "⚙", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, TabStop = false };
            settingsButton.FlatAppearance.BorderSize = 0;
            settingsButton.Click += (s, e) => _openSettingsAction?.Invoke();
            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(avatarBox);   // 排在 titleLabel 之後,靠左停靠時才會在最左邊
            headerPanel.Controls.Add(settingsButton);
            headerPanel.Controls.Add(closeButton);
            headerPanel.Paint += (s, e) => UiKit.DrawHairline(e.Graphics, 0, headerPanel.Width, headerPanel.Height - 1, ThemeManager.BorderColor);

            // ── 輸入區（底部）──
            inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 118, Padding = new Padding(10, 6, 10, 8) };
            includeContextBox = new CheckBox
            {
                Text = Localization.T("Ai.IncludeContext"),
                Checked = true,
                Dock = DockStyle.Top,
                Height = 24
            };
            sendButton = new Button
            {
                Text = Localization.T("Ai.Send"),
                Dock = DockStyle.Right,
                Width = 74
            };
            ThemeManager.MarkAsPrimary(sendButton);
            sendButton.Click += (s, e) => SendCurrentInput();
            inputBox = new TextBox { Multiline = true, AcceptsReturn = true };
            inputShell = new UiInputShell(inputBox) { Dock = DockStyle.Fill, Height = 60 };
            inputBox.KeyDown += (s, e) =>
            {
                // Enter 送出、Shift+Enter 換行
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    SendCurrentInput();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Panel inputRow = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            inputRow.Controls.Add(inputShell);
            inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            inputRow.Controls.Add(sendButton);
            inputPanel.Controls.Add(inputRow);
            inputPanel.Controls.Add(includeContextBox);

            // ── 插入 SQL 動作列 ──
            actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 4, 10, 6), Visible = false };
            insertSqlButton = new Button
            {
                Text = Localization.T("Ai.InsertSql"),
                Dock = DockStyle.Fill
            };
            insertSqlButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_lastAssistantSql)) _insertSqlAction?.Invoke(_lastAssistantSql);
            };
            actionPanel.Controls.Add(insertSqlButton);

            // ── 建議提問（第一次使用的引導）──
            suggestionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(10, 2, 10, 2)
            };
            AddSuggestion(Localization.T("Ai.SuggestGenerate"));
            AddSuggestion(Localization.T("Ai.SuggestExplain"));
            AddSuggestion(Localization.T("Ai.SuggestOptimize"));

            // ── 對話區 ──
            chatBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = UiKit.Body,
                DetectUrls = false
            };
            AppendSystemLine(Localization.T("Ai.Welcome"));

            // ── 供應商／模型快速切換列 ──
            BuildPickerRow();

            Controls.Add(chatBox);
            Controls.Add(suggestionPanel);
            Controls.Add(actionPanel);
            Controls.Add(inputPanel);
            Controls.Add(pickerPanel);
            Controls.Add(headerPanel);

            ApplyThemeColors();
        }

        private Panel pickerPanel;
        private ComboBox providerCombo;
        private ComboBox modelCombo;
        private Button refreshModelsButton;
        private bool _suppressPickerEvents;

        /// <summary>面板頂端的供應商／模型快速切換：使用者訂閱哪家、本機跑什麼，這裡直接換。</summary>
        private void BuildPickerRow()
        {
            pickerPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 4, 10, 4) };
            providerCombo = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (AiProviderPreset preset in AiChatService.Presets) providerCombo.Items.Add(preset.DisplayName);

            refreshModelsButton = new Button { Dock = DockStyle.Right, Width = 30, Text = "↻", TabStop = false };
            modelCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown
            };

            Panel modelHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 4, 0) };
            modelHost.Controls.Add(modelCombo);
            pickerPanel.Controls.Add(modelHost);
            pickerPanel.Controls.Add(refreshModelsButton);
            pickerPanel.Controls.Add(providerCombo);

            SyncPickerFromSettings();

            providerCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressPickerEvents) return;
                int index = providerCombo.SelectedIndex;
                if (index < 0 || index >= AiChatService.Presets.Length) return;
                AiProviderPreset preset = AiChatService.Presets[index];
                ApplicationOptionSettings.SetString("AiProvider", preset.Id);
                ApplicationOptionSettings.SetString("AiEndpoint", "");
                ApplicationOptionSettings.SetString("AiModel", "");
                ApplicationOptionSettings.Save();
                _suppressPickerEvents = true;
                modelCombo.Items.Clear();
                modelCombo.Text = preset.DefaultModel ?? "";
                _suppressPickerEvents = false;
                if (preset.NeedsKey && !AiChatService.HasApiKey(preset.Id))
                {
                    AppendSystemLine(Localization.T("Ai.NoApiKeyHint"));
                }
            };
            modelCombo.Leave += (s, e) => SaveModelFromPicker();
            modelCombo.SelectedIndexChanged += (s, e) => { if (!_suppressPickerEvents) SaveModelFromPicker(); };
            refreshModelsButton.Click += async (s, e) =>
            {
                refreshModelsButton.Enabled = false;
                try
                {
                    AiChatSettings settings = AiChatSettings.Load();
                    var models = await Task.Run(() => AiChatService.ListModels(settings));
                    _suppressPickerEvents = true;
                    string current = modelCombo.Text;
                    modelCombo.Items.Clear();
                    foreach (string m in models) modelCombo.Items.Add(m);
                    modelCombo.Text = current;
                    _suppressPickerEvents = false;
                    AppendSystemLine(Localization.Format("Ai.ModelsLoaded", models.Count));
                }
                catch (Exception ex)
                {
                    AppendErrorBody(Localization.Format("Ai.RequestFailed", ex.Message));
                }
                finally
                {
                    refreshModelsButton.Enabled = true;
                }
            };
        }

        private void SaveModelFromPicker()
        {
            AiChatSettings current = AiChatSettings.Load();
            string text = (modelCombo.Text ?? "").Trim();
            // 跟預設一樣就存空字串（維持「留空用預設」語意）
            ApplicationOptionSettings.SetString("AiModel", text == current.Preset.DefaultModel ? "" : text);
            ApplicationOptionSettings.Save();
        }

        /// <summary>把設定值套回快速切換列（開啟面板或設定變更後呼叫）。</summary>
        public void SyncPickerFromSettings()
        {
            _suppressPickerEvents = true;
            AiChatSettings settings = AiChatSettings.Load();
            // 設定裡若是無效值（例如舊版的 "none"），退回 FindPreset 的 fallback，不讓下拉空白
            AiProviderPreset effective = settings.Preset;
            for (int i = 0; i < AiChatService.Presets.Length; i++)
            {
                if (ReferenceEquals(AiChatService.Presets[i], effective))
                {
                    providerCombo.SelectedIndex = i;
                    break;
                }
            }
            modelCombo.Text = settings.Model ?? "";
            _suppressPickerEvents = false;
        }

        private void AddSuggestion(string text)
        {
            Button button = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 3, 6, 3),
                Margin = new Padding(0, 2, 0, 2),
                TextAlign = ContentAlignment.MiddleLeft
            };
            button.Click += (s, e) =>
            {
                inputBox.Text = text;
                inputBox.Focus();
                inputBox.SelectionStart = inputBox.Text.Length;
            };
            suggestionPanel.Controls.Add(button);
        }

        public void ApplyThemeColors()
        {
            BackColor = ThemeManager.SurfaceColor;
            headerPanel.BackColor = ThemeManager.SurfaceColor;
            titleLabel.ForeColor = ThemeManager.TextColor;
            chatBox.BackColor = ThemeManager.WindowBackColor;
            chatBox.ForeColor = ThemeManager.TextColor;
            inputPanel.BackColor = ThemeManager.SurfaceColor;
            actionPanel.BackColor = ThemeManager.SurfaceColor;
            suggestionPanel.BackColor = ThemeManager.WindowBackColor;
            includeContextBox.ForeColor = ThemeManager.TextColor;
            if (pickerPanel != null) pickerPanel.BackColor = ThemeManager.SurfaceColor;
        }

        private void SendCurrentInput()
        {
            string text = (inputBox.Text ?? "").Trim();
            if (text.Length == 0 || _busy) return;
            inputBox.Text = "";
            suggestionPanel.Visible = false;
            SendAsync(text);
        }

        private async void SendAsync(string userText)
        {
            _busy = true;
            sendButton.Enabled = false;
            AppendRoleLine(Localization.T("Ai.You"), ThemeManager.AccentColor);
            AppendBody(userText);

            AiChatSettings settings = AiChatSettings.Load();
            try
            {
                List<AiChatMessage> messages = new List<AiChatMessage>();
                messages.Add(new AiChatMessage("system", Localization.T("Ai.SystemPrompt")));
                if (includeContextBox.Checked && _contextProvider != null)
                {
                    string context = "";
                    try { context = _contextProvider() ?? ""; } catch { }
                    if (context.Length > 0)
                    {
                        messages.Add(new AiChatMessage("system", Localization.T("Ai.ContextPrefix") + "\n" + context));
                    }
                }
                foreach (AiChatMessage m in _history) messages.Add(m);
                messages.Add(new AiChatMessage("user", userText));

                AppendRoleLine("Punky（" + settings.Model + "）", ThemeManager.SuccessColor);
                AppendBody(Localization.T("Ai.Thinking"));
                int thinkingStart = chatBox.TextLength;

                string reply = await Task.Run(() => AiChatService.ChatCompletion(settings, messages));

                RemoveThinkingLine();
                AppendBody(reply);

                _history.Add(new AiChatMessage("user", userText));
                _history.Add(new AiChatMessage("assistant", reply));
                // 上下文別無限長大：保留最近 12 則
                while (_history.Count > 12) _history.RemoveAt(0);

                _lastAssistantSql = AiChatService.ExtractLastSqlBlock(reply);
                actionPanel.Visible = !string.IsNullOrWhiteSpace(_lastAssistantSql);
            }
            catch (Exception ex)
            {
                RemoveThinkingLine();
                AppendErrorBody(Localization.Format("Ai.RequestFailed", ex.Message));
                if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
                {
                    AppendErrorBody(Localization.T("Ai.NoApiKeyHint"));
                }
            }
            finally
            {
                _busy = false;
                sendButton.Enabled = true;
            }
        }

        private int _thinkingMark = -1;

        private void AppendRoleLine(string who, Color color)
        {
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.SelectionFont = UiKit.BodyBold;
            chatBox.SelectionColor = color;
            chatBox.AppendText((chatBox.TextLength > 0 ? "\n" : "") + who + "\n");
        }

        private void AppendBody(string text)
        {
            _thinkingMark = chatBox.TextLength;
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.SelectionFont = UiKit.Body;
            chatBox.SelectionColor = ThemeManager.TextColor;
            chatBox.AppendText(text + "\n");
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.ScrollToCaret();
        }

        private void AppendErrorBody(string text)
        {
            _thinkingMark = -1;
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.SelectionFont = UiKit.Body;
            chatBox.SelectionColor = ThemeManager.DangerColor;
            chatBox.AppendText(text + "\n");
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.ScrollToCaret();
        }

        private void AppendSystemLine(string text)
        {
            chatBox.SelectionStart = chatBox.TextLength;
            chatBox.SelectionFont = UiKit.Body;
            chatBox.SelectionColor = ThemeManager.MutedTextColor;
            chatBox.AppendText(text + "\n");
        }

        /// <summary>把「思考中…」那一行移掉，換成真正的回覆。</summary>
        private void RemoveThinkingLine()
        {
            if (_thinkingMark < 0 || _thinkingMark > chatBox.TextLength) { _thinkingMark = -1; return; }
            chatBox.SelectionStart = _thinkingMark;
            chatBox.SelectionLength = chatBox.TextLength - _thinkingMark;
            chatBox.SelectedText = "";
            _thinkingMark = -1;
        }
    }
}

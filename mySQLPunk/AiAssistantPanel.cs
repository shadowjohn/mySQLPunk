using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// AI 助理右側面板（Punky 崩琦）。對話用泡泡呈現：使用者靠右、Punky 靠左，
    /// SQL 區塊在泡泡裡用等寬字型與獨立底色。走 OpenAI 相容 API，
    /// 可附上目前連線的資料庫結構當上下文，回覆裡的 SQL 一鍵插入查詢分頁。
    /// </summary>
    public class AiAssistantPanel : Panel
    {
        private readonly Func<string> _contextProvider;
        private readonly Action<string> _insertSqlAction;
        private readonly Action _openSettingsAction;
        private readonly IAiAgentHost _agentHost;

        private Label titleLabel;
        private Button settingsButton;
        private Button collapseButton;
        private Button closeButton;
        private Panel headerPanel;
        private AiChatView chatView;
        private FlowLayoutPanel suggestionPanel;
        private Panel actionPanel;
        private Button insertSqlButton;
        private Button reviewSqlButton;
        private Panel inputPanel;
        private TextBox inputBox;
        private UiInputShell inputShell;
        private Button sendButton;
        private Button compareModelsButton;
        private CheckBox includeContextBox;

        private Panel conversationPanel;
        private ComboBox conversationCombo;
        private Button newConversationButton;
        private Button renameConversationButton;
        private Button closeConversationButton;
        private readonly ToolTip conversationToolTip = new ToolTip();
        private readonly List<AiConversationState> _conversations = new List<AiConversationState>();
        private AiConversationState _activeConversation;
        private int _nextConversationNumber = 1;
        private bool _suppressConversationEvents;

        private bool _busy;
        private string _lastAssistantSql;
        private Action<string> _pendingSqlReviewAction;
        private Action<string> _lastAssistantSqlReviewAction;
        private CheckBox agentModeBox;
        private Button stopButton;
        private CancellationTokenSource _agentCts;

        private sealed class AiConversationState
        {
            public int Number;
            public string Title;
            public bool IsUntitled = true;
            public bool HasUserContent;
            public bool IncludeContext = true;
            public bool AgentMode;
            public string Draft = string.Empty;
            public string LastAssistantSql;
            public Action<string> PendingSqlReviewAction;
            public Action<string> LastAssistantSqlReviewAction;
            public readonly List<AiChatMessage> History = new List<AiChatMessage>();
            public readonly AiChatView View = new AiChatView { Dock = DockStyle.Fill };

            public override string ToString()
            {
                return Title ?? string.Empty;
            }
        }

        public AiAssistantPanel(Func<string> contextProvider, Action<string> insertSqlAction, Action closeAction, Action collapseAction, Action openSettingsAction, IAiAgentHost agentHost)
        {
            _contextProvider = contextProvider;
            _insertSqlAction = insertSqlAction;
            _openSettingsAction = openSettingsAction;
            _agentHost = agentHost;

            Width = 380;
            MinimumSize = new Size(280, 0);

            // ── 標題列（Punky 頭像 + 名稱）──
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
            closeButton.AccessibleName = Localization.T("Common.Close");
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.Click += (s, e) => closeAction?.Invoke();
            settingsButton = new Button { Text = "⚙", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, TabStop = false };
            settingsButton.AccessibleName = Localization.T("Menu.Options");
            settingsButton.FlatAppearance.BorderSize = 0;
            settingsButton.Click += (s, e) => _openSettingsAction?.Invoke();
            collapseButton = new Button { Text = "›", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, TabStop = false };
            collapseButton.AccessibleName = Localization.T("View.CollapseAiPane");
            collapseButton.FlatAppearance.BorderSize = 0;
            collapseButton.Click += (s, e) => collapseAction?.Invoke();
            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(avatarBox);   // 排在 titleLabel 之後,靠左停靠時才會在最左邊
            headerPanel.Controls.Add(settingsButton);
            headerPanel.Controls.Add(collapseButton);
            headerPanel.Controls.Add(closeButton);
            ThemeManager.SetGlyph(settingsButton, UiGlyph.Settings);
            ThemeManager.SetGlyph(collapseButton, UiGlyph.ChevronRight);
            ThemeManager.SetGlyph(closeButton, UiGlyph.Close);
            headerPanel.Paint += (s, e) => UiKit.DrawHairline(e.Graphics, 0, headerPanel.Width, headerPanel.Height - 1, ThemeManager.BorderColor);

            // ── 輸入區（底部）──
            inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 142, Padding = new Padding(10, 6, 10, 8) };
            includeContextBox = new CheckBox
            {
                Text = Localization.T("Ai.IncludeContext"),
                Checked = true,
                Dock = DockStyle.Top,
                Height = 24
            };
            agentModeBox = new CheckBox
            {
                Text = Localization.T("Ai.AgentModeCheckbox"),
                Checked = false,
                Dock = DockStyle.Top,
                Height = 24,
                Visible = false
            };
            sendButton = new Button
            {
                Text = Localization.T("Ai.Send"),
                Dock = DockStyle.Right,
                Width = 74
            };
            ThemeManager.MarkAsPrimary(sendButton);
            sendButton.Click += (s, e) => SendCurrentInput();
            stopButton = new Button
            {
                Text = Localization.T("Ai.AgentStopButton"),
                Dock = DockStyle.Right,
                Width = 74,
                Visible = false
            };
            stopButton.Click += (s, e) => { if (_agentCts != null) _agentCts.Cancel(); };
            compareModelsButton = new Button
            {
                Text = Localization.T("Ai.CompareButton"),
                Dock = DockStyle.Right,
                Width = 74
            };
            compareModelsButton.Click += (s, e) => OpenModelComparison();
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
            inputRow.Controls.Add(compareModelsButton);
            inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            inputRow.Controls.Add(sendButton);
            inputRow.Controls.Add(stopButton);
            inputPanel.Controls.Add(inputRow);
            inputPanel.Controls.Add(agentModeBox);
            inputPanel.Controls.Add(includeContextBox);

            // ── 插入 SQL 動作列 ──
            actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 4, 10, 6), Visible = false };
            insertSqlButton = new Button
            {
                Text = Localization.T("Ai.OpenSqlInNewQuery"),
                Dock = DockStyle.Fill
            };
            insertSqlButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_lastAssistantSql)) _insertSqlAction?.Invoke(_lastAssistantSql);
            };
            reviewSqlButton = new Button
            {
                Text = Localization.T("Ai.ReviewSql"),
                Dock = DockStyle.Right,
                Width = 128,
                Visible = false
            };
            ThemeManager.MarkAsPrimary(reviewSqlButton);
            reviewSqlButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_lastAssistantSql))
                    _lastAssistantSqlReviewAction?.Invoke(_lastAssistantSql);
            };
            actionPanel.Controls.Add(insertSqlButton);
            actionPanel.Controls.Add(reviewSqlButton);

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

            // ── 供應商／模型快速切換列 ──
            BuildPickerRow();

            // ── 聊天室切換列 ──
            BuildConversationRow();
            AiConversationState initialConversation = CreateConversationState();
            _conversations.Add(initialConversation);
            _activeConversation = initialConversation;
            chatView = initialConversation.View;
            RefreshConversationPicker();

            // 新使用者什麼都沒設時，自動找本機已登入的 CLI（走訂閱、免金鑰）
            AutoDetectBackendAsync();

            Controls.Add(chatView);
            Controls.Add(suggestionPanel);
            Controls.Add(actionPanel);
            Controls.Add(inputPanel);
            Controls.Add(pickerPanel);
            Controls.Add(conversationPanel);
            Controls.Add(headerPanel);

            ApplyThemeColors();
            RefreshAgentModeAvailability();
        }

        private Panel pickerPanel;
        private ComboBox providerCombo;
        private ComboBox modelCombo;
        private Button refreshModelsButton;
        private bool _suppressPickerEvents;

        public void SetDraft(string text)
        {
            SetDraft(text, null);
        }

        public void SetDraft(string text, Action<string> reviewSqlAction)
        {
            string draft = (text ?? string.Empty).Trim();
            if (draft.Length == 0 || inputBox == null) return;

            inputBox.Text = draft;
            inputBox.SelectionStart = inputBox.Text.Length;
            inputBox.SelectionLength = 0;
            includeContextBox.Checked = true;
            _pendingSqlReviewAction = reviewSqlAction;
            _lastAssistantSql = null;
            _lastAssistantSqlReviewAction = null;
            UpdateSqlActions();
            inputBox.Focus();
        }

        public void ApplyLanguage()
        {
            titleLabel.Text = Localization.T("Ai.PanelTitle");
            settingsButton.AccessibleName = Localization.T("Menu.Options");
            collapseButton.AccessibleName = Localization.T("View.CollapseAiPane");
            closeButton.AccessibleName = Localization.T("Common.Close");
            refreshModelsButton.AccessibleName = Localization.T("Query.Refresh");
            includeContextBox.Text = Localization.T("Ai.IncludeContext");
            if (agentModeBox != null) agentModeBox.Text = Localization.T("Ai.AgentModeCheckbox");
            if (stopButton != null) stopButton.Text = Localization.T("Ai.AgentStopButton");
            sendButton.Text = Localization.T("Ai.Send");
            compareModelsButton.Text = Localization.T("Ai.CompareButton");
            insertSqlButton.Text = Localization.T("Ai.OpenSqlInNewQuery");
            reviewSqlButton.Text = Localization.T("Ai.ReviewSql");
            conversationCombo.AccessibleName = Localization.T("Ai.Conversations");
            newConversationButton.AccessibleName = Localization.T("Ai.NewConversation");
            renameConversationButton.AccessibleName = Localization.T("Ai.RenameConversation");
            closeConversationButton.AccessibleName = Localization.T("Ai.CloseConversation");
            conversationToolTip.SetToolTip(newConversationButton, Localization.T("Ai.NewConversation"));
            conversationToolTip.SetToolTip(renameConversationButton, Localization.T("Ai.RenameConversation"));
            conversationToolTip.SetToolTip(closeConversationButton, Localization.T("Ai.CloseConversation"));
            foreach (AiConversationState conversation in _conversations)
            {
                if (conversation.IsUntitled)
                    conversation.Title = Localization.Format("Ai.NewConversationTitle", conversation.Number);
            }
            RefreshConversationPicker();
        }

        private void BuildConversationRow()
        {
            conversationPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 4, 10, 4) };
            conversationCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = Localization.T("Ai.Conversations")
            };
            newConversationButton = CreateConversationButton(UiGlyph.Plus, Localization.T("Ai.NewConversation"));
            renameConversationButton = CreateConversationButton(UiGlyph.Pencil, Localization.T("Ai.RenameConversation"));
            closeConversationButton = CreateConversationButton(UiGlyph.Close, Localization.T("Ai.CloseConversation"));

            Panel actions = new Panel { Dock = DockStyle.Right, Width = 94 };
            closeConversationButton.Dock = DockStyle.Right;
            renameConversationButton.Dock = DockStyle.Right;
            newConversationButton.Dock = DockStyle.Right;
            actions.Controls.Add(closeConversationButton);
            actions.Controls.Add(renameConversationButton);
            actions.Controls.Add(newConversationButton);
            conversationPanel.Controls.Add(conversationCombo);
            conversationPanel.Controls.Add(actions);

            conversationCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressConversationEvents || _busy) return;
                int index = conversationCombo.SelectedIndex;
                if (index >= 0 && index < _conversations.Count)
                    ActivateConversation(_conversations[index], true);
            };
            newConversationButton.Click += (s, e) => CreateNewConversation();
            renameConversationButton.Click += (s, e) => ShowRenameConversationDialog();
            closeConversationButton.Click += (s, e) => CloseActiveConversation(true);
        }

        private Button CreateConversationButton(UiGlyph glyph, string accessibleName)
        {
            Button button = new Button
            {
                Width = 30,
                Text = string.Empty,
                AccessibleName = accessibleName,
                TabStop = false
            };
            ThemeManager.SetGlyph(button, glyph);
            conversationToolTip.SetToolTip(button, accessibleName);
            return button;
        }

        private AiConversationState CreateConversationState()
        {
            AiConversationState conversation = new AiConversationState
            {
                Number = _nextConversationNumber++
            };
            conversation.Title = Localization.Format("Ai.NewConversationTitle", conversation.Number);
            conversation.View.AddAssistant("Punky", Localization.T("Ai.Welcome"));
            return conversation;
        }

        private void CreateNewConversation()
        {
            if (_busy) return;
            SaveActiveConversationState();
            AiConversationState conversation = CreateConversationState();
            _conversations.Add(conversation);
            ActivateConversation(conversation, false);
        }

        private void ActivateConversation(AiConversationState conversation, bool saveCurrent)
        {
            if (conversation == null || ReferenceEquals(conversation, _activeConversation))
            {
                RefreshConversationPicker();
                return;
            }

            if (saveCurrent) SaveActiveConversationState();
            if (chatView != null) Controls.Remove(chatView);

            _activeConversation = conversation;
            _lastAssistantSql = conversation.LastAssistantSql;
            _pendingSqlReviewAction = conversation.PendingSqlReviewAction;
            _lastAssistantSqlReviewAction = conversation.LastAssistantSqlReviewAction;
            chatView = conversation.View;
            inputBox.Text = conversation.Draft ?? string.Empty;
            inputBox.SelectionStart = inputBox.Text.Length;
            includeContextBox.Checked = conversation.IncludeContext;
            if (agentModeBox != null) agentModeBox.Checked = conversation.AgentMode;
            suggestionPanel.Visible = !conversation.HasUserContent;

            Controls.Add(chatView);
            // 維持第一次加入時的順序，DockStyle.Fill 才會避開上下兩側的操作列。
            Controls.SetChildIndex(chatView, 0);
            chatView.BackColor = ThemeManager.WindowBackColor;
            chatView.RefreshTheme();
            chatView.LayoutBubbles();
            chatView.ScrollToBottom();
            UpdateSqlActions();
            RefreshConversationPicker();
            inputBox.Focus();
        }

        private void SaveActiveConversationState()
        {
            if (_activeConversation == null) return;
            _activeConversation.Draft = inputBox == null ? string.Empty : inputBox.Text;
            _activeConversation.IncludeContext = includeContextBox != null && includeContextBox.Checked;
            _activeConversation.AgentMode = agentModeBox != null && agentModeBox.Checked;
            _activeConversation.LastAssistantSql = _lastAssistantSql;
            _activeConversation.PendingSqlReviewAction = _pendingSqlReviewAction;
            _activeConversation.LastAssistantSqlReviewAction = _lastAssistantSqlReviewAction;
        }

        private void RefreshConversationPicker()
        {
            if (conversationCombo == null) return;
            _suppressConversationEvents = true;
            conversationCombo.BeginUpdate();
            conversationCombo.Items.Clear();
            foreach (AiConversationState conversation in _conversations)
                conversationCombo.Items.Add(conversation);
            conversationCombo.SelectedItem = _activeConversation;
            conversationCombo.EndUpdate();
            _suppressConversationEvents = false;
            if (closeConversationButton != null)
                closeConversationButton.Enabled = !_busy && _conversations.Count > 1;
        }

        private bool TryRenameActiveConversation(string title)
        {
            string normalized = NormalizeConversationTitle(title);
            if (_activeConversation == null || normalized.Length == 0) return false;
            _activeConversation.Title = normalized;
            _activeConversation.IsUntitled = false;
            RefreshConversationPicker();
            return true;
        }

        private static string NormalizeConversationTitle(string title)
        {
            string[] parts = (title ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string normalized = string.Join(" ", parts);
            if (normalized.Length > 36) normalized = normalized.Substring(0, 35) + "…";
            return normalized;
        }

        private void ShowRenameConversationDialog()
        {
            if (_busy || _activeConversation == null) return;
            using (Form dialog = new Form())
            using (TextBox nameBox = new TextBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = Localization.T("Ai.RenameConversation");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(360, 104);
                dialog.Padding = new Padding(12);

                nameBox.Text = _activeConversation.Title;
                nameBox.Dock = DockStyle.Top;
                nameBox.MaxLength = 36;
                Panel buttonRow = new Panel { Dock = DockStyle.Bottom, Height = 34 };
                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Dock = DockStyle.Right;
                cancelButton.Width = 82;
                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Dock = DockStyle.Right;
                okButton.Width = 82;
                buttonRow.Controls.Add(cancelButton);
                buttonRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
                buttonRow.Controls.Add(okButton);
                dialog.Controls.Add(nameBox);
                dialog.Controls.Add(buttonRow);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;
                ThemeManager.ApplyTo(dialog);

                nameBox.SelectAll();
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    TryRenameActiveConversation(nameBox.Text);
            }
        }

        private void CloseActiveConversation(bool confirm)
        {
            if (_busy || _activeConversation == null || _conversations.Count <= 1) return;
            SaveActiveConversationState();
            if (confirm && (_activeConversation.HasUserContent || !string.IsNullOrWhiteSpace(_activeConversation.Draft)))
            {
                DialogResult result = MessageBox.Show(
                    Localization.Format("Ai.CloseConversationConfirm", _activeConversation.Title),
                    Localization.T("Ai.CloseConversation"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes) return;
            }

            int index = _conversations.IndexOf(_activeConversation);
            AiConversationState removed = _activeConversation;
            _conversations.RemoveAt(index);
            _activeConversation = null;
            chatView = null;
            removed.View.Dispose();
            ActivateConversation(_conversations[Math.Min(index, _conversations.Count - 1)], false);
        }

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

            refreshModelsButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 30,
                Text = "↻",
                AccessibleName = Localization.T("Query.Refresh"),
                TabStop = false
            };
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
            ThemeManager.SetGlyph(refreshModelsButton, UiGlyph.Refresh);

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
                foreach (string m in AiChatService.KnownCliModels(preset.Id)) modelCombo.Items.Add(m);
                modelCombo.Text = preset.DefaultModel ?? "";
                _suppressPickerEvents = false;
                if (preset.NeedsKey && !AiChatService.HasApiKey(preset.Id))
                {
                    chatView.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                }
            };
            modelCombo.Leave += (s, e) => SaveModelFromPicker();
            modelCombo.SelectedIndexChanged += (s, e) => { if (!_suppressPickerEvents) SaveModelFromPicker(); };
            refreshModelsButton.Click += async (s, e) =>
            {
                AiConversationState targetConversation = _activeConversation;
                AiChatSettings settings = AiChatSettings.Load();
                if (settings.Preset.AuthStyle == "cli")
                {
                    // CLI 沒有列模型的 API,改放內建的常用型號清單
                    _suppressPickerEvents = true;
                    string keep = modelCombo.Text;
                    modelCombo.Items.Clear();
                    foreach (string m in AiChatService.KnownCliModels(settings.Provider)) modelCombo.Items.Add(m);
                    modelCombo.Text = keep;
                    _suppressPickerEvents = false;
                    targetConversation.View.AddSystem(Localization.Format("Ai.CliModelsHint", settings.Preset.DisplayName));
                    return;
                }
                if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
                {
                    targetConversation.View.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                    return;
                }
                refreshModelsButton.Enabled = false;
                try
                {
                    var models = await Task.Run(() => AiChatService.ListModels(settings));
                    _suppressPickerEvents = true;
                    string current = modelCombo.Text;
                    modelCombo.Items.Clear();
                    foreach (string m in models) modelCombo.Items.Add(m);
                    modelCombo.Text = current;
                    _suppressPickerEvents = false;
                    targetConversation.View.AddSystem(Localization.Format("Ai.ModelsLoaded", models.Count));
                }
                catch (Exception ex)
                {
                    targetConversation.View.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
                }
                finally
                {
                    refreshModelsButton.Enabled = !_busy;
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

        /// <summary>
        /// 目前的供應商還不能用（要金鑰但沒設）時，自動偵測本機已安裝的 AI CLI
        /// （Codex / Claude Code / Gemini），找到就直接選用——下載即用，不用任何設定。
        /// </summary>
        private async void AutoDetectBackendAsync()
        {
            AiConversationState targetConversation = _activeConversation;
            try
            {
                AiChatSettings settings = AiChatSettings.Load();
                if (!settings.Preset.NeedsKey || AiChatService.HasApiKey(settings.Provider)) return; // 已經能用就不動

                var clis = await Task.Run(() => AiChatService.DetectInstalledClis());
                if (clis.Count == 0) return;

                AiProviderPreset pick = clis[0];
                ApplicationOptionSettings.SetString("AiProvider", pick.Id);
                ApplicationOptionSettings.SetString("AiEndpoint", "");
                ApplicationOptionSettings.SetString("AiModel", "");
                ApplicationOptionSettings.Save();
                SyncPickerFromSettings();
                targetConversation.View.AddSystem(Localization.Format("Ai.AutoDetectedCli", pick.DisplayName));
            }
            catch { }
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
            modelCombo.Items.Clear();
            foreach (string m in AiChatService.KnownCliModels(effective.Id)) modelCombo.Items.Add(m);
            modelCombo.Text = settings.Model ?? "";
            _suppressPickerEvents = false;
            RefreshAgentModeAvailability();
        }

        /// <summary>依全域選項決定「代為操作」勾選是否出現;關閉時同時取消勾選,避免殘留。</summary>
        private void RefreshAgentModeAvailability()
        {
            if (agentModeBox == null) return;
            bool enabled = _agentHost != null && ApplicationOptionSettings.GetBool("AiAgentModeEnabled");
            agentModeBox.Visible = enabled;
            if (!enabled && agentModeBox.Checked) agentModeBox.Checked = false;
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
            chatView.BackColor = ThemeManager.WindowBackColor;
            inputPanel.BackColor = ThemeManager.SurfaceColor;
            actionPanel.BackColor = ThemeManager.SurfaceColor;
            suggestionPanel.BackColor = ThemeManager.WindowBackColor;
            includeContextBox.ForeColor = ThemeManager.TextColor;
            if (agentModeBox != null) agentModeBox.ForeColor = ThemeManager.TextColor;
            if (pickerPanel != null) pickerPanel.BackColor = ThemeManager.SurfaceColor;
            if (conversationPanel != null) conversationPanel.BackColor = ThemeManager.SurfaceColor;
            foreach (AiConversationState conversation in _conversations)
            {
                conversation.View.BackColor = ThemeManager.WindowBackColor;
                conversation.View.RefreshTheme();
            }
        }

        private void SendCurrentInput()
        {
            string text = (inputBox.Text ?? "").Trim();
            if (text.Length == 0 || _busy) return;

            // 沒金鑰就先擋下來，別真的打 API 換來一句原始錯誤；輸入內容保留不清空
            AiChatSettings settings = AiChatSettings.Load();
            if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
            {
                chatView.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                return;
            }

            inputBox.Text = "";
            suggestionPanel.Visible = false;
            AiConversationState conversation = _activeConversation;
            conversation.HasUserContent = true;
            conversation.IncludeContext = includeContextBox.Checked;
            conversation.AgentMode = agentModeBox != null && agentModeBox.Checked;
            if (conversation.IsUntitled)
            {
                conversation.Title = NormalizeConversationTitle(text);
                conversation.IsUntitled = false;
                RefreshConversationPicker();
            }
            Action<string> reviewSqlAction = _pendingSqlReviewAction;
            _pendingSqlReviewAction = null;
            _lastAssistantSql = null;
            _lastAssistantSqlReviewAction = null;
            conversation.Draft = string.Empty;
            conversation.PendingSqlReviewAction = null;
            conversation.LastAssistantSql = null;
            conversation.LastAssistantSqlReviewAction = null;
            UpdateSqlActions();

            bool agentMode = _agentHost != null && agentModeBox != null && agentModeBox.Checked
                && ApplicationOptionSettings.GetBool("AiAgentModeEnabled");
            if (agentMode) RunAgentLoopAsync(conversation, text);
            else SendAsync(conversation, text, reviewSqlAction);
        }

        private void OpenModelComparison()
        {
            string prompt = (inputBox.Text ?? string.Empty).Trim();
            if (_busy) return;
            if (prompt.Length == 0)
            {
                MessageBox.Show(
                    Localization.T("Ai.CompareNoPrompt"),
                    Localization.T("Ai.CompareTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string schemaContext = string.Empty;
            if (includeContextBox.Checked && _contextProvider != null)
            {
                try { schemaContext = _contextProvider() ?? string.Empty; } catch { }
            }
            using (AiModelComparisonForm form = new AiModelComparisonForm(
                AiChatSettings.Load(),
                prompt,
                schemaContext,
                _activeConversation.History))
            {
                form.ShowDialog(FindForm());
            }
        }

        private async void SendAsync(AiConversationState conversation, string userText, Action<string> reviewSqlAction)
        {
            _busy = true;
            SetBusyState(true);
            conversation.View.AddUser(userText);

            AiChatSettings settings = AiChatSettings.Load();
            string modelLabel = string.IsNullOrWhiteSpace(settings.Model) ? Localization.T("Ai.CliDefaultModel") : settings.Model;
            AiChatBubble replyBubble = conversation.View.AddAssistant("Punky · " + modelLabel, Localization.T("Ai.Thinking"));
            try
            {
                TrimHistory(conversation);
                List<AiChatMessage> messages = new List<AiChatMessage>();
                messages.Add(new AiChatMessage("system", Localization.T("Ai.SystemPrompt")));
                if (IsAgentModeGloballyEnabled())
                {
                    messages.Add(new AiChatMessage("system", Localization.T("Ai.AgentPlanPromptAddition")));
                }
                if (conversation.IncludeContext && _contextProvider != null)
                {
                    string context = "";
                    try { context = _contextProvider() ?? ""; } catch { }
                    if (context.Length > 0)
                    {
                        messages.Add(new AiChatMessage("system", Localization.T("Ai.ContextPrefix") + "\n" + context));
                    }
                }
                foreach (AiChatMessage m in conversation.History) messages.Add(m);
                messages.Add(new AiChatMessage("user", userText));

                string reply = await Task.Run(() => AiChatService.ChatCompletion(settings, messages));

                replyBubble.SetContent(reply);
                RenderPlanIfPresent(conversation, reply);
                conversation.View.ScrollToBottom();

                conversation.History.Add(new AiChatMessage("user", userText));
                conversation.History.Add(new AiChatMessage("assistant", reply));

                conversation.LastAssistantSql = AiChatService.ExtractLastSqlBlock(reply);
                conversation.LastAssistantSqlReviewAction = reviewSqlAction;
                if (ReferenceEquals(conversation, _activeConversation))
                {
                    _lastAssistantSql = conversation.LastAssistantSql;
                    _lastAssistantSqlReviewAction = conversation.LastAssistantSqlReviewAction;
                    UpdateSqlActions();
                }
            }
            catch (Exception ex)
            {
                conversation.View.RemoveBubble(replyBubble);
                conversation.View.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
                if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
                {
                    conversation.View.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                }
            }
            finally
            {
                _busy = false;
                SetBusyState(false);
            }
        }

        private static bool IsAgentModeGloballyEnabled()
        {
            return ApplicationOptionSettings.GetBool("AiAgentModeEnabled");
        }

        /// <summary>上下文別無限長大：保留最近 24 則(代辦模式的工具軌跡已在收尾時壓縮)。</summary>
        private static void TrimHistory(AiConversationState conversation)
        {
            while (conversation.History.Count > 24) conversation.History.RemoveAt(0);
        }

        /// <summary>Punky 代為操作：多回合工具迴圈,每回合可執行多個動作,危險操作先徵求同意。</summary>
        private async void RunAgentLoopAsync(AiConversationState conversation, string userText)
        {
            _busy = true;
            _agentCts = new CancellationTokenSource();
            CancellationToken token = _agentCts.Token;
            SetBusyState(true, true);
            conversation.View.AddUser(userText);

            AiChatSettings settings = AiChatSettings.Load();
            bool isCli = string.Equals(settings.Preset.AuthStyle, "cli", StringComparison.OrdinalIgnoreCase);
            int maxTurns = isCli ? 4 : 8;
            string modelLabel = string.IsNullOrWhiteSpace(settings.Model) ? Localization.T("Ai.CliDefaultModel") : settings.Model;

            TrimHistory(conversation);
            List<AiChatMessage> messages = new List<AiChatMessage>();
            messages.Add(new AiChatMessage("system", Localization.T("Ai.SystemPrompt")));
            messages.Add(new AiChatMessage("system",
                Localization.T("Ai.AgentSystemPrompt") + "\n" + AiAgentToolService.BuildToolCatalogPrompt()));
            messages.Add(new AiChatMessage("system", Localization.T("Ai.AgentPlanPromptAddition")));
            if (conversation.IncludeContext && _contextProvider != null)
            {
                string context = "";
                try { context = _contextProvider() ?? ""; } catch { }
                if (context.Length > 0)
                    messages.Add(new AiChatMessage("system", Localization.T("Ai.ContextPrefix") + "\n" + context));
            }
            foreach (AiChatMessage m in conversation.History) messages.Add(m);
            messages.Add(new AiChatMessage("user", userText));

            // 本次 run 新增到 messages 的索引起點,收尾時把工具軌跡壓縮成一則摘要寫回 History
            var runTranscript = new List<AiChatMessage>();
            runTranscript.Add(new AiChatMessage("user", userText));
            int actionsUsed = 0;
            int okCount = 0;
            int failCount = 0;
            string finalReply = null;

            try
            {
                for (int turn = 0; turn < maxTurns; turn++)
                {
                    if (token.IsCancellationRequested) break;

                    AiChatBubble replyBubble = conversation.View.AddAssistant("Punky · " + modelLabel, Localization.T("Ai.Thinking"));
                    string reply = await Task.Run(() => AiChatService.ChatCompletion(settings, messages));
                    if (token.IsCancellationRequested) { conversation.View.RemoveBubble(replyBubble); break; }

                    List<AiAgentAction> actions = AiAgentProtocol.ParseActions(reply);
                    string prose = AiAgentProtocol.StripProtocolBlocks(reply);

                    if (actions.Count == 0)
                    {
                        replyBubble.SetContent(prose.Length > 0 ? prose : reply);
                        RenderPlanIfPresent(conversation, reply);
                        messages.Add(new AiChatMessage("assistant", reply));
                        runTranscript.Add(new AiChatMessage("assistant", reply));
                        finalReply = reply;
                        conversation.View.ScrollToBottom();
                        break;
                    }

                    replyBubble.SetContent(prose.Length > 0 ? prose : Localization.T("Ai.AgentWorking"));
                    messages.Add(new AiChatMessage("assistant", reply));
                    runTranscript.Add(new AiChatMessage("assistant", reply));

                    var results = new List<AiAgentToolResult>();
                    bool hadError = false;
                    int actionsThisTurn = 0;
                    foreach (AiAgentAction action in actions)
                    {
                        if (token.IsCancellationRequested) break;
                        if (actionsThisTurn >= AiAgentToolService.MaxActionsPerTurn)
                        {
                            conversation.View.AddSystem(Localization.Format("Ai.AgentTurnCapped", AiAgentToolService.MaxActionsPerTurn));
                            break;
                        }
                        if (actionsUsed >= AiAgentToolService.MaxActionsPerRun)
                        {
                            conversation.View.AddSystem(Localization.T("Ai.AgentMaxStepsReached"));
                            hadError = true;
                            break;
                        }

                        actionsThisTurn++;
                        actionsUsed++;
                        string why = string.IsNullOrWhiteSpace(action.Why) ? (action.Tool ?? "") : action.Why;
                        conversation.View.AddSystem(Localization.Format("Ai.AgentStepRunning", action.Tool ?? "?", why));
                        AiAgentToolResult result = await AiAgentToolService.ExecuteAsync(action, _agentHost);
                        results.Add(result);
                        if (result.Ok)
                        {
                            okCount++;
                            conversation.View.AddSystem(Localization.Format("Ai.AgentStepOk", result.Summary ?? action.Tool));
                        }
                        else
                        {
                            failCount++;
                            hadError = true;
                            conversation.View.AddSystem(Localization.Format("Ai.AgentStepFailed", action.Tool ?? "?", result.Error ?? ""));
                            break; // 首錯即止,把錯誤回饋給模型調整
                        }
                    }

                    if (results.Count > 0)
                    {
                        string envelope = AiAgentProtocol.BuildToolResultMessage(results);
                        messages.Add(new AiChatMessage("user", envelope));
                    }

                    if (token.IsCancellationRequested) break;
                    if (actionsUsed >= AiAgentToolService.MaxActionsPerRun && !hadError)
                    {
                        conversation.View.AddSystem(Localization.T("Ai.AgentMaxStepsReached"));
                        break;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    conversation.View.AddSystem(Localization.T("Ai.AgentStopped"));
                }
                else
                {
                    conversation.View.AddSystem(Localization.Format("Ai.AgentRunSummary", okCount, failCount));
                }
            }
            catch (Exception ex)
            {
                conversation.View.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
            }
            finally
            {
                // 工具軌跡壓縮:把本次 run 的多回合對話收斂成 user 摘要 + 最終回覆,避免 CLI 後端 O(N²) 膨脹
                CompactRunIntoHistory(conversation, userText, finalReply, okCount, failCount, token.IsCancellationRequested);
                conversation.View.ScrollToBottom();
                _agentCts.Dispose();
                _agentCts = null;
                _busy = false;
                SetBusyState(false, false);
            }
        }

        private static void CompactRunIntoHistory(AiConversationState conversation, string userText, string finalReply, int okCount, int failCount, bool stopped)
        {
            conversation.History.Add(new AiChatMessage("user", userText));
            string note = stopped
                ? Localization.T("Ai.AgentStopped")
                : Localization.Format("Ai.AgentHistoryNote", okCount, failCount);
            string assistantEntry = string.IsNullOrWhiteSpace(finalReply)
                ? "[" + note + "]"
                : finalReply + "\n\n[" + note + "]";
            conversation.History.Add(new AiChatMessage("assistant", assistantEntry));
            TrimHistory(conversation);
        }

        private void SetBusyState(bool busy)
        {
            SetBusyState(busy, false);
        }

        private void SetBusyState(bool busy, bool agentRunning)
        {
            sendButton.Enabled = !busy;
            sendButton.Visible = !agentRunning;
            if (stopButton != null)
            {
                stopButton.Visible = agentRunning;
                stopButton.Enabled = agentRunning;
            }
            compareModelsButton.Enabled = !busy;
            inputBox.Enabled = !busy;
            includeContextBox.Enabled = !busy;
            if (agentModeBox != null) agentModeBox.Enabled = !busy;
            conversationCombo.Enabled = !busy;
            newConversationButton.Enabled = !busy;
            renameConversationButton.Enabled = !busy;
            closeConversationButton.Enabled = !busy && _conversations.Count > 1;
            providerCombo.Enabled = !busy;
            modelCombo.Enabled = !busy;
            refreshModelsButton.Enabled = !busy;
        }

        private void UpdateSqlActions()
        {
            bool hasSql = !string.IsNullOrWhiteSpace(_lastAssistantSql);
            bool canReview = hasSql && _lastAssistantSqlReviewAction != null;
            if (reviewSqlButton != null) reviewSqlButton.Visible = canReview;
            if (actionPanel != null) actionPanel.Visible = hasSql;
        }

        /// <summary>回覆若含 punky-plan,在其下方掛一張可勾選、可一鍵執行的清單卡。</summary>
        private void RenderPlanIfPresent(AiConversationState conversation, string reply)
        {
            if (_agentHost == null || !IsAgentModeGloballyEnabled()) return;
            AiAgentPlan plan = AiAgentProtocol.ParsePlan(reply);
            if (plan == null) return;
            var card = new AiPlanChecklistCard(plan, (c, items) => ExecutePlanItemsAsync(conversation, c, items));
            conversation.View.AddRow(card);
        }

        /// <summary>執行使用者勾選的清單項目;與 agent 迴圈共用同一條安全閘門(危險操作仍會確認)。</summary>
        private async void ExecutePlanItemsAsync(AiConversationState conversation, AiPlanChecklistCard card, List<AiAgentPlanItem> items)
        {
            if (_busy || _agentHost == null || items.Count == 0) return;
            _busy = true;
            SetBusyState(true, false);
            card.SetRunning(true);

            int okCount = 0;
            int failCount = 0;
            int skipCount = 0;
            var results = new List<AiAgentToolResult>();
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    AiAgentPlanItem item = items[i];
                    AiAgentToolResult result = await AiAgentToolService.ExecuteAsync(item.Action, _agentHost);
                    results.Add(result);
                    if (result.Ok)
                    {
                        okCount++;
                        card.SetItemStatus(item.Id, Localization.T("Ai.PlanItemDone"), false);
                    }
                    else
                    {
                        failCount++;
                        card.SetItemStatus(item.Id, Localization.T("Ai.PlanItemFailed") + "：" + (result.Error ?? ""), true);
                        DialogResult cont = MessageBox.Show(
                            Localization.Format("Ai.PlanContinueOnError", item.Id, result.Error ?? ""),
                            Localization.T("Ai.PlanTitle"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (cont != DialogResult.Yes)
                        {
                            for (int j = i + 1; j < items.Count; j++)
                            {
                                skipCount++;
                                card.SetItemStatus(items[j].Id, Localization.T("Ai.PlanItemSkipped"), false);
                            }
                            break;
                        }
                    }
                }

                conversation.History.Add(new AiChatMessage("user", AiAgentProtocol.BuildToolResultMessage(results)));
                TrimHistory(conversation);
                conversation.View.AddSystem(Localization.Format("Ai.PlanRunSummary", okCount, failCount, skipCount));
            }
            finally
            {
                card.SetRunning(false);
                card.MarkExecuted();
                _busy = false;
                SetBusyState(false, false);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                conversationToolTip.Dispose();
                foreach (AiConversationState conversation in _conversations)
                {
                    if (!conversation.View.IsDisposed) conversation.View.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>對話列（泡泡或勾選清單卡）都要能依面板寬度重算高度。</summary>
    internal interface IAiChatRow
    {
        void RecalculateRowHeight(int width);
        void ApplyRowTheme();
    }

    /// <summary>對話清單：自己管排版與捲動，泡泡與清單卡的寬度跟著面板寬度走。</summary>
    internal class AiChatView : Panel
    {
        private readonly List<Control> _rows = new List<Control>();

        public AiChatView()
        {
            AutoScroll = true;
            DoubleBuffered = true;
        }

        public AiChatBubble AddUser(string text)
        {
            return Add(new AiChatBubble(AiChatBubbleKind.User, null, text));
        }

        public AiChatBubble AddAssistant(string header, string text)
        {
            return Add(new AiChatBubble(AiChatBubbleKind.Assistant, header, text));
        }

        public AiChatBubble AddSystem(string text)
        {
            return Add(new AiChatBubble(AiChatBubbleKind.System, null, text));
        }

        public AiChatBubble AddError(string text)
        {
            return Add(new AiChatBubble(AiChatBubbleKind.Error, null, text));
        }

        private AiChatBubble Add(AiChatBubble bubble)
        {
            // 相同的系統/錯誤訊息別重複洗版（例如連按幾次 ↻ 或連續切供應商）
            if (_rows.Count > 0
                && (bubble.Kind == AiChatBubbleKind.System || bubble.Kind == AiChatBubbleKind.Error))
            {
                AiChatBubble last = _rows[_rows.Count - 1] as AiChatBubble;
                if (last != null && last.Kind == bubble.Kind && last.PlainText == bubble.PlainText)
                {
                    bubble.Dispose();
                    ScrollToBottom();
                    return last;
                }
            }
            _rows.Add(bubble);
            Controls.Add(bubble);
            LayoutBubbles();
            ScrollToBottom();
            return bubble;
        }

        /// <summary>加入非泡泡列(例如 punky-plan 勾選清單卡)。</summary>
        internal Control AddRow(Control row)
        {
            _rows.Add(row);
            Controls.Add(row);
            LayoutBubbles();
            ScrollToBottom();
            return row;
        }

        public void RemoveBubble(AiChatBubble bubble)
        {
            if (bubble == null) return;
            _rows.Remove(bubble);
            Controls.Remove(bubble);
            bubble.Dispose();
            LayoutBubbles();
        }

        public void RefreshTheme()
        {
            foreach (Control row in _rows)
            {
                IAiChatRow chatRow = row as IAiChatRow;
                if (chatRow != null) chatRow.ApplyRowTheme();
                else row.Invalidate();
            }
        }

        public void ScrollToBottom()
        {
            if (_rows.Count == 0) return;
            ScrollControlIntoView(_rows[_rows.Count - 1]);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutBubbles();
        }

        /// <summary>由上而下排：每列都是全寬 row，泡泡本體在 row 裡靠左/靠右，清單卡自行排版。</summary>
        internal void LayoutBubbles()
        {
            int width = ClientSize.Width;
            if (width <= 0) return;
            int y = 6 + AutoScrollPosition.Y;
            SuspendLayout();
            foreach (Control row in _rows)
            {
                row.Location = new Point(AutoScrollPosition.X, y);
                row.Width = width;
                AiChatBubble bubble = row as AiChatBubble;
                if (bubble != null)
                {
                    bubble.RecalculateHeight();
                }
                else
                {
                    IAiChatRow chatRow = row as IAiChatRow;
                    if (chatRow != null) chatRow.RecalculateRowHeight(width);
                }
                y += row.Height + 6;
            }
            ResumeLayout();
        }
    }

    internal enum AiChatBubbleKind { User, Assistant, System, Error }

    /// <summary>
    /// 一則對話泡泡。內容切成一般文字與 ```code``` 兩種區段：
    /// 一般文字直接畫在泡泡上，code 區段畫成等寬字型的內嵌區塊。
    /// </summary>
    internal class AiChatBubble : Control
    {
        private const int RowPadding = 10;    // row 左右留白
        private const int BubblePadding = 10; // 泡泡內距
        private const int CodePadding = 8;

        private readonly AiChatBubbleKind _kind;
        private readonly string _header;
        private List<KeyValuePair<string, bool>> _segments; // (文字, 是否為 code)
        private string _plainText;

        public AiChatBubbleKind Kind => _kind;
        public string PlainText => _plainText;

        public AiChatBubble(AiChatBubbleKind kind, string header, string text)
        {
            _kind = kind;
            _header = header;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            ParseSegments(text);

            // 右鍵可以把整則內容複製走
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copyItem = new ToolStripMenuItem(Localization.T("Ai.CopyMessage"));
            copyItem.Click += (s, e) =>
            {
                try { if (!string.IsNullOrEmpty(_plainText)) Clipboard.SetText(_plainText); } catch { }
            };
            menu.Items.Add(copyItem);
            ThemeManager.ApplyToolStrip(menu);
            ContextMenuStrip = menu;
        }

        public void SetContent(string text)
        {
            ParseSegments(text);
            AiChatView view = Parent as AiChatView;
            if (view != null) view.LayoutBubbles();
            Invalidate();
        }

        private void ParseSegments(string text)
        {
            _plainText = text ?? "";
            _segments = new List<KeyValuePair<string, bool>>();
            string remaining = _plainText.Replace("\r\n", "\n");
            while (true)
            {
                int start = remaining.IndexOf("```", StringComparison.Ordinal);
                if (start < 0) break;
                int lineEnd = remaining.IndexOf('\n', start);
                if (lineEnd < 0) break;
                int end = remaining.IndexOf("```", lineEnd, StringComparison.Ordinal);
                if (end < 0) break;

                string before = remaining.Substring(0, start).Trim();
                if (before.Length > 0) _segments.Add(new KeyValuePair<string, bool>(before, false));
                string code = remaining.Substring(lineEnd + 1, end - lineEnd - 1).Trim('\n', '\r');
                if (code.Length > 0) _segments.Add(new KeyValuePair<string, bool>(code, true));
                remaining = remaining.Substring(Math.Min(remaining.Length, end + 3));
            }
            string tail = remaining.Trim();
            if (tail.Length > 0) _segments.Add(new KeyValuePair<string, bool>(tail, false));
            if (_segments.Count == 0) _segments.Add(new KeyValuePair<string, bool>("", false));
        }

        private Font BodyFont => UiKit.Body;
        private Font CodeFont => UiKit.GetFont(9f, FontStyle.Regular); // 共用快取，不能 dispose
        private Font HeaderFont => UiKit.GetFont(8.5f, FontStyle.Bold);

        private const TextFormatFlags MeasureFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        /// <summary>依目前寬度重算泡泡高度（由 AiChatView 在排版時呼叫）。</summary>
        public void RecalculateHeight()
        {
            int rowWidth = Math.Max(60, Width - RowPadding * 2);

            // 系統訊息沒有泡泡：量測寬度要跟繪製時一致，不然會多出一截空白
            if (_kind == AiChatBubbleKind.System)
            {
                Size systemSize = TextRenderer.MeasureText(_plainText, UiKit.GetFont(8.5f, FontStyle.Regular),
                    new Size(rowWidth, int.MaxValue), MeasureFlags);
                _bubbleWidth = rowWidth;
                Height = systemSize.Height + 10;
                return;
            }

            int maxBubbleWidth = (int)(rowWidth * 0.9);
            int contentWidth = Math.Max(30, ComputeContentWidth(maxBubbleWidth - BubblePadding * 2));

            int height = BubblePadding;
            if (!string.IsNullOrEmpty(_header))
            {
                height += TextRenderer.MeasureText(_header, HeaderFont, new Size(contentWidth, int.MaxValue), MeasureFlags).Height + 4;
            }
            for (int i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (i > 0) height += 6;
                if (segment.Value)
                {
                    Size size = TextRenderer.MeasureText(segment.Key, CodeFont, new Size(contentWidth - CodePadding * 2, int.MaxValue), MeasureFlags);
                    height += size.Height + CodePadding * 2;
                }
                else
                {
                    height += TextRenderer.MeasureText(segment.Key, BodyFont, new Size(contentWidth, int.MaxValue), MeasureFlags).Height;
                }
            }
            height += BubblePadding;

            _bubbleWidth = contentWidth + BubblePadding * 2;
            Height = height + 2;
        }

        private int _bubbleWidth;

        /// <summary>泡泡寬度貼合內容：短訊息小泡泡、長訊息吃滿可用寬度。</summary>
        private int ComputeContentWidth(int maxContentWidth)
        {
            int widest = 0;
            if (!string.IsNullOrEmpty(_header))
            {
                widest = TextRenderer.MeasureText(_header, HeaderFont).Width;
            }
            foreach (var segment in _segments)
            {
                Font font = segment.Value ? CodeFont : BodyFont;
                int natural = TextRenderer.MeasureText(segment.Key, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (segment.Value) natural += CodePadding * 2;
                if (natural > widest) widest = natural;
            }
            return Math.Min(maxContentWidth, Math.Max(24, widest));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ThemeManager.WindowBackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int rowWidth = Math.Max(60, Width - RowPadding * 2);
            int bubbleWidth = Math.Min(_bubbleWidth, rowWidth);
            int contentWidth = bubbleWidth - BubblePadding * 2;

            Color bubbleBack;
            Color bubbleBorder;
            Color textColor;
            Color codeBack;
            Color codeText;
            int x;
            switch (_kind)
            {
                case AiChatBubbleKind.User:
                    bubbleBack = ThemeManager.AccentColor;
                    bubbleBorder = ThemeManager.AccentColor;
                    textColor = Color.White;
                    codeBack = Color.FromArgb(46, Color.White);
                    codeText = Color.White;
                    x = Width - RowPadding - bubbleWidth;
                    break;
                case AiChatBubbleKind.Error:
                    bubbleBack = UiKit.Mix(ThemeManager.DangerColor, ThemeManager.WindowBackColor, 0.88f);
                    bubbleBorder = UiKit.Mix(ThemeManager.DangerColor, ThemeManager.WindowBackColor, 0.55f);
                    textColor = ThemeManager.DangerColor;
                    codeBack = ThemeManager.WindowBackColor;
                    codeText = ThemeManager.DangerColor;
                    x = RowPadding;
                    break;
                case AiChatBubbleKind.System:
                    // 系統訊息：沒有泡泡，置中灰字
                    Rectangle systemRect = new Rectangle(RowPadding, 4, rowWidth, Height - 8);
                    TextRenderer.DrawText(g, _plainText, UiKit.GetFont(8.5f, FontStyle.Regular), systemRect,
                        ThemeManager.MutedTextColor, MeasureFlags | TextFormatFlags.HorizontalCenter);
                    return;
                default: // Assistant
                    bubbleBack = ThemeManager.SurfaceColor;
                    bubbleBorder = ThemeManager.BorderColor;
                    textColor = ThemeManager.TextColor;
                    codeBack = ThemeManager.WindowBackColor;
                    codeText = ThemeManager.TextColor;
                    x = RowPadding;
                    break;
            }

            Rectangle bubbleRect = new Rectangle(x, 0, bubbleWidth, Height - 2);
            UiKit.FillRounded(g, bubbleRect, 10f, bubbleBack);
            if (bubbleBorder != bubbleBack) UiKit.DrawRounded(g, bubbleRect, 10f, bubbleBorder, 1f);

            int y = BubblePadding;
            if (!string.IsNullOrEmpty(_header))
            {
                Rectangle headerRect = new Rectangle(x + BubblePadding, y, contentWidth, int.MaxValue / 2);
                Color headerColor = _kind == AiChatBubbleKind.User ? Color.White : ThemeManager.SuccessColor;
                Size headerSize = TextRenderer.MeasureText(_header, HeaderFont, new Size(contentWidth, int.MaxValue), MeasureFlags);
                TextRenderer.DrawText(g, _header, HeaderFont, new Rectangle(headerRect.X, headerRect.Y, contentWidth, headerSize.Height), headerColor, MeasureFlags);
                y += headerSize.Height + 4;
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (i > 0) y += 6;
                if (segment.Value)
                {
                    Size size = TextRenderer.MeasureText(segment.Key, CodeFont, new Size(contentWidth - CodePadding * 2, int.MaxValue), MeasureFlags);
                    Rectangle codeRect = new Rectangle(x + BubblePadding, y, contentWidth, size.Height + CodePadding * 2);
                    UiKit.FillRounded(g, codeRect, 6f, codeBack);
                    TextRenderer.DrawText(g, segment.Key, CodeFont,
                        new Rectangle(codeRect.X + CodePadding, codeRect.Y + CodePadding, contentWidth - CodePadding * 2, size.Height),
                        codeText, MeasureFlags);
                    y += codeRect.Height;
                }
                else
                {
                    Size size = TextRenderer.MeasureText(segment.Key, BodyFont, new Size(contentWidth, int.MaxValue), MeasureFlags);
                    TextRenderer.DrawText(g, segment.Key, BodyFont,
                        new Rectangle(x + BubblePadding, y, contentWidth, size.Height), textColor, MeasureFlags);
                    y += size.Height;
                }
            }
        }
    }

    /// <summary>
    /// punky-plan 的可勾選清單卡:每個項目一個 CheckBox + 說明 + SQL 預覽 + 狀態,
    /// 底部一個「執行勾選項目」按鈕。危險項目預設不勾並標記。用真實子控制項,不做 owner-draw 命中測試。
    /// </summary>
    internal sealed class AiPlanChecklistCard : Panel, IAiChatRow
    {
        private sealed class ItemRow
        {
            public AiAgentPlanItem Item;
            public CheckBox Check;
            public Label Sql;
            public Label Status;
            public bool Dangerous;
        }

        private const int SidePad = 12;
        private const int InnerPad = 10;

        private readonly AiAgentPlan _plan;
        private readonly Action<AiPlanChecklistCard, List<AiAgentPlanItem>> _runAction;
        private readonly List<ItemRow> _items = new List<ItemRow>();
        private readonly Label _titleLabel;
        private readonly Button _runButton;
        private bool _executed;

        public AiPlanChecklistCard(AiAgentPlan plan, Action<AiPlanChecklistCard, List<AiAgentPlanItem>> runAction)
        {
            _plan = plan;
            _runAction = runAction;
            DoubleBuffered = true;
            Margin = Padding.Empty;

            _titleLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(plan.Title) ? Localization.T("Ai.PlanTitle") : plan.Title,
                AutoSize = false,
                Font = UiKit.BodyBold
            };
            Controls.Add(_titleLabel);

            foreach (AiAgentPlanItem item in plan.Items)
            {
                string reason;
                bool dangerous = item.SqlText != null
                    && AiAgentSqlClassifier.Classify(item.SqlText, out reason) == AiSqlRisk.Dangerous;

                CheckBox check = new CheckBox
                {
                    Text = FormatItemLabel(item, dangerous),
                    Checked = !dangerous, // 危險項目預設不勾,避免誤按整批放行
                    AutoSize = false,
                    Font = UiKit.Body
                };
                Controls.Add(check);

                Label sqlLabel = null;
                if (!string.IsNullOrWhiteSpace(item.SqlText))
                {
                    sqlLabel = new Label
                    {
                        Text = item.SqlText,
                        AutoSize = false,
                        Font = UiKit.GetFont(9f, FontStyle.Regular)
                    };
                    Controls.Add(sqlLabel);
                }

                Label status = new Label { Text = string.Empty, AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
                Controls.Add(status);

                _items.Add(new ItemRow { Item = item, Check = check, Sql = sqlLabel, Status = status, Dangerous = dangerous });
            }

            _runButton = new Button { Text = Localization.T("Ai.PlanRun"), AutoSize = false };
            ThemeManager.MarkAsPrimary(_runButton);
            _runButton.Click += (s, e) => OnRunClicked();
            Controls.Add(_runButton);

            ApplyRowTheme();
        }

        private static string FormatItemLabel(AiAgentPlanItem item, bool dangerous)
        {
            string note = string.IsNullOrWhiteSpace(item.Note) ? (item.SqlText ?? item.Action.Tool) : item.Note;
            string prefix = dangerous ? Localization.T("Ai.PlanDangerTag") + " " : "";
            return prefix + note;
        }

        private void OnRunClicked()
        {
            if (_executed || _runAction == null) return;
            var selected = new List<AiAgentPlanItem>();
            foreach (ItemRow row in _items)
            {
                if (row.Check.Checked) selected.Add(row.Item);
            }
            if (selected.Count == 0) return;
            _runAction(this, selected);
        }

        public void SetItemStatus(int itemId, string text, bool failed)
        {
            foreach (ItemRow row in _items)
            {
                if (row.Item.Id != itemId) continue;
                row.Status.Text = text;
                row.Status.ForeColor = failed ? ThemeManager.DangerColor : ThemeManager.SuccessColor;
                break;
            }
        }

        public void SetRunning(bool running)
        {
            _runButton.Enabled = !running;
            foreach (ItemRow row in _items) row.Check.Enabled = !running && !_executed;
        }

        /// <summary>執行過就鎖定,重跑要靠新的計畫,避免同一批動作被重複套用。</summary>
        public void MarkExecuted()
        {
            _executed = true;
            _runButton.Enabled = false;
            foreach (ItemRow row in _items) row.Check.Enabled = false;
        }

        public void ApplyRowTheme()
        {
            BackColor = ThemeManager.SurfaceColor;
            _titleLabel.ForeColor = ThemeManager.TextColor;
            foreach (ItemRow row in _items)
            {
                row.Check.ForeColor = row.Dangerous ? ThemeManager.DangerColor : ThemeManager.TextColor;
                row.Check.BackColor = ThemeManager.SurfaceColor;
                if (row.Sql != null)
                {
                    row.Sql.ForeColor = ThemeManager.MutedTextColor;
                    row.Sql.BackColor = ThemeManager.WindowBackColor;
                }
            }
            Invalidate();
        }

        public void RecalculateRowHeight(int width)
        {
            int cardWidth = Math.Max(120, width - SidePad * 2);
            int contentWidth = cardWidth - InnerPad * 2;
            int x = SidePad + InnerPad;
            int y = InnerPad;

            _titleLabel.SetBounds(x, y, contentWidth, 20);
            y += 24;

            foreach (ItemRow row in _items)
            {
                int statusWidth = 96;
                int checkWidth = contentWidth - statusWidth - 8;
                int checkHeight = Math.Max(20, TextRenderer.MeasureText(row.Check.Text, row.Check.Font,
                    new Size(checkWidth - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 4);
                row.Check.SetBounds(x, y, checkWidth, checkHeight);
                row.Status.SetBounds(x + checkWidth + 8, y, statusWidth, checkHeight);
                y += checkHeight + 2;

                if (row.Sql != null)
                {
                    int sqlHeight = Math.Max(18, TextRenderer.MeasureText(row.Sql.Text, row.Sql.Font,
                        new Size(contentWidth - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 6);
                    row.Sql.SetBounds(x + 24, y, contentWidth - 24, sqlHeight);
                    y += sqlHeight + 2;
                }
                y += 4;
            }

            _runButton.SetBounds(x + contentWidth - 132, y, 132, UiMetrics.ControlHeight);
            y += UiMetrics.ControlHeight + InnerPad;
            Height = y;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle cardRect = new Rectangle(SidePad, 0, Math.Max(60, Width - SidePad * 2) - 1, Height - 4);
            UiKit.DrawRounded(e.Graphics, cardRect, 8f, ThemeManager.BorderColor, 1f);
        }
    }
}

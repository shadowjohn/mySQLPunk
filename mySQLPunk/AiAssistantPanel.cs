using System;
using System.Collections.Generic;
using System.Drawing;
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

        private Label titleLabel;
        private Button settingsButton;
        private Button closeButton;
        private Panel headerPanel;
        private AiChatView chatView;
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

            // ── 對話區（泡泡）──
            chatView = new AiChatView { Dock = DockStyle.Fill };
            chatView.AddAssistant("Punky", Localization.T("Ai.Welcome"));

            // ── 供應商／模型快速切換列 ──
            BuildPickerRow();

            Controls.Add(chatView);
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
                    chatView.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                }
            };
            modelCombo.Leave += (s, e) => SaveModelFromPicker();
            modelCombo.SelectedIndexChanged += (s, e) => { if (!_suppressPickerEvents) SaveModelFromPicker(); };
            refreshModelsButton.Click += async (s, e) =>
            {
                AiChatSettings settings = AiChatSettings.Load();
                if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
                {
                    chatView.AddSystem(Localization.T("Ai.NoApiKeyHint"));
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
                    chatView.AddSystem(Localization.Format("Ai.ModelsLoaded", models.Count));
                }
                catch (Exception ex)
                {
                    chatView.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
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
            chatView.BackColor = ThemeManager.WindowBackColor;
            inputPanel.BackColor = ThemeManager.SurfaceColor;
            actionPanel.BackColor = ThemeManager.SurfaceColor;
            suggestionPanel.BackColor = ThemeManager.WindowBackColor;
            includeContextBox.ForeColor = ThemeManager.TextColor;
            if (pickerPanel != null) pickerPanel.BackColor = ThemeManager.SurfaceColor;
            chatView.RefreshTheme();
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
            SendAsync(text);
        }

        private async void SendAsync(string userText)
        {
            _busy = true;
            sendButton.Enabled = false;
            chatView.AddUser(userText);

            AiChatSettings settings = AiChatSettings.Load();
            AiChatBubble replyBubble = chatView.AddAssistant("Punky · " + settings.Model, Localization.T("Ai.Thinking"));
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

                string reply = await Task.Run(() => AiChatService.ChatCompletion(settings, messages));

                replyBubble.SetContent(reply);
                chatView.ScrollToBottom();

                _history.Add(new AiChatMessage("user", userText));
                _history.Add(new AiChatMessage("assistant", reply));
                // 上下文別無限長大：保留最近 12 則
                while (_history.Count > 12) _history.RemoveAt(0);

                _lastAssistantSql = AiChatService.ExtractLastSqlBlock(reply);
                actionPanel.Visible = !string.IsNullOrWhiteSpace(_lastAssistantSql);
            }
            catch (Exception ex)
            {
                chatView.RemoveBubble(replyBubble);
                chatView.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
                if (settings.Preset.NeedsKey && !AiChatService.HasApiKey(settings.Provider))
                {
                    chatView.AddSystem(Localization.T("Ai.NoApiKeyHint"));
                }
            }
            finally
            {
                _busy = false;
                sendButton.Enabled = true;
            }
        }
    }

    /// <summary>對話泡泡清單：自己管排版與捲動，泡泡寬度跟著面板寬度走。</summary>
    internal class AiChatView : Panel
    {
        private readonly List<AiChatBubble> _bubbles = new List<AiChatBubble>();

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
            if (_bubbles.Count > 0
                && (bubble.Kind == AiChatBubbleKind.System || bubble.Kind == AiChatBubbleKind.Error))
            {
                AiChatBubble last = _bubbles[_bubbles.Count - 1];
                if (last.Kind == bubble.Kind && last.PlainText == bubble.PlainText)
                {
                    bubble.Dispose();
                    ScrollToBottom();
                    return last;
                }
            }
            _bubbles.Add(bubble);
            Controls.Add(bubble);
            LayoutBubbles();
            ScrollToBottom();
            return bubble;
        }

        public void RemoveBubble(AiChatBubble bubble)
        {
            if (bubble == null) return;
            _bubbles.Remove(bubble);
            Controls.Remove(bubble);
            bubble.Dispose();
            LayoutBubbles();
        }

        public void RefreshTheme()
        {
            foreach (AiChatBubble bubble in _bubbles) bubble.Invalidate();
        }

        public void ScrollToBottom()
        {
            if (_bubbles.Count == 0) return;
            ScrollControlIntoView(_bubbles[_bubbles.Count - 1]);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutBubbles();
        }

        /// <summary>由上而下排：每個泡泡都是全寬的 row，泡泡本體在 row 裡靠左/靠右。</summary>
        internal void LayoutBubbles()
        {
            int width = ClientSize.Width;
            if (width <= 0) return;
            int y = 6 + AutoScrollPosition.Y;
            SuspendLayout();
            foreach (AiChatBubble bubble in _bubbles)
            {
                bubble.Location = new Point(AutoScrollPosition.X, y);
                bubble.Width = width;
                bubble.RecalculateHeight();
                y += bubble.Height + 6;
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
}

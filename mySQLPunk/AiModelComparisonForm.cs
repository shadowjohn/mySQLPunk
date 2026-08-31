using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>把相同提示與聊天室上下文交給兩個模型，結果只在此視窗並排顯示。</summary>
    public sealed class AiModelComparisonForm : Form
    {
        private readonly AiChatSettings _currentSettings;
        private readonly string _prompt;
        private readonly string _schemaContext;
        private readonly List<AiChatMessage> _history;
        private readonly ComboBox _leftProviderCombo;
        private readonly ComboBox _leftModelCombo;
        private readonly ComboBox _rightProviderCombo;
        private readonly ComboBox _rightModelCombo;
        private readonly Button _compareButton;
        private readonly Label _statusLabel;
        private readonly Panel _leftResultHost;
        private readonly Panel _rightResultHost;
        private AiChatView _leftView;
        private AiChatView _rightView;
        private bool _busy;

        public AiModelComparisonForm(
            AiChatSettings currentSettings,
            string prompt,
            string schemaContext,
            IList<AiChatMessage> history)
        {
            _currentSettings = currentSettings ?? AiChatSettings.Load();
            _prompt = (prompt ?? string.Empty).Trim();
            _schemaContext = schemaContext ?? string.Empty;
            _history = CloneHistory(history);

            Text = Localization.T("Ai.CompareTitle");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, 720);
            MinimumSize = new Size(820, 540);
            Font = UiKit.Body;

            UiSectionHeader header = new UiSectionHeader
            {
                Dock = DockStyle.Top,
                Title = Localization.T("Ai.CompareTitle"),
                Subtitle = Localization.T("Ai.CompareSubtitle"),
                Glyph = UiGlyph.Model
            };
            Label intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(UiMetrics.Space3, 12, UiMetrics.Space3, 0),
                Text = Localization.T("Ai.CompareIntro"),
                ForeColor = ThemeManager.WarningColor
            };

            _leftProviderCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _leftModelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
            _rightProviderCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _rightModelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
            _leftProviderCombo.AccessibleName = Localization.T("Ai.CompareLeftProvider");
            _leftModelCombo.AccessibleName = Localization.T("Ai.CompareLeftModel");
            _rightProviderCombo.AccessibleName = Localization.T("Ai.CompareRightProvider");
            _rightModelCombo.AccessibleName = Localization.T("Ai.CompareRightModel");

            string rightProvider;
            string rightModel;
            ResolveInitialRightChoice(out rightProvider, out rightModel);
            TableLayoutPanel selectors = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 86,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(UiMetrics.Space3, 6, UiMetrics.Space3, 6)
            };
            selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            selectors.Controls.Add(BuildChoicePanel(
                Localization.T("Ai.CompareLeft"),
                _leftProviderCombo,
                _leftModelCombo,
                _currentSettings.Provider,
                _currentSettings.Model), 0, 0);
            selectors.Controls.Add(BuildChoicePanel(
                Localization.T("Ai.CompareRight"),
                _rightProviderCombo,
                _rightModelCombo,
                rightProvider,
                rightModel), 1, 0);

            _compareButton = new Button
            {
                Text = Localization.T("Ai.CompareStart"),
                Dock = DockStyle.Right,
                Width = 126
            };
            ThemeManager.MarkAsPrimary(_compareButton);
            _compareButton.Click += async (sender, args) => await StartComparisonAsync();
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = Localization.T("Ai.CompareReady"),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeManager.MutedTextColor
            };
            Panel actionRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(UiMetrics.Space3, 4, UiMetrics.Space3, 6)
            };
            actionRow.Controls.Add(_statusLabel);
            actionRow.Controls.Add(_compareButton);

            SplitContainer results = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(1060, 500),
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BorderStyle = BorderStyle.FixedSingle,
                Panel1MinSize = 300,
                Panel2MinSize = 300
            };
            results.SplitterDistance = 535;
            results.Panel1.Padding = new Padding(4);
            results.Panel2.Padding = new Padding(4);
            _leftResultHost = new Panel { Dock = DockStyle.Fill };
            _rightResultHost = new Panel { Dock = DockStyle.Fill };
            results.Panel1.Controls.Add(_leftResultHost);
            results.Panel2.Controls.Add(_rightResultHost);
            ResetResultViews();

            Controls.Add(results);
            Controls.Add(actionRow);
            Controls.Add(selectors);
            Controls.Add(intro);
            Controls.Add(header);
            FormClosing += OnComparisonFormClosing;

            ThemeManager.ApplyTo(this);
            RefreshResultThemes();
        }

        private TableLayoutPanel BuildChoicePanel(
            string title,
            ComboBox providerCombo,
            ComboBox modelCombo,
            string initialProvider,
            string initialModel)
        {
            TableLayoutPanel panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0, 0, UiMetrics.Space2, 0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            Label providerLabel = new Label
            {
                Text = title + " · " + Localization.T("Ai.CompareProvider"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft
            };
            Label modelLabel = new Label
            {
                Text = Localization.T("Ai.CompareModel"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft
            };
            panel.Controls.Add(providerLabel, 0, 0);
            panel.Controls.Add(modelLabel, 1, 0);
            panel.Controls.Add(providerCombo, 0, 1);
            panel.Controls.Add(modelCombo, 1, 1);

            PopulateProviders(providerCombo, initialProvider);
            PopulateModels(providerCombo, modelCombo, initialModel);
            providerCombo.SelectedIndexChanged += (sender, args) => PopulateModels(providerCombo, modelCombo, string.Empty);
            return panel;
        }

        private static void PopulateProviders(ComboBox combo, string selectedProvider)
        {
            combo.Items.Clear();
            int selectedIndex = 0;
            for (int i = 0; i < AiChatService.Presets.Length; i++)
            {
                combo.Items.Add(AiChatService.Presets[i].DisplayName);
                if (string.Equals(AiChatService.Presets[i].Id, selectedProvider, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = i;
            }
            combo.SelectedIndex = selectedIndex;
        }

        private void PopulateModels(ComboBox providerCombo, ComboBox modelCombo, string preferredModel)
        {
            string provider = GetProviderId(providerCombo);
            AiProviderPreset preset = AiChatService.FindPreset(provider);
            modelCombo.BeginUpdate();
            modelCombo.Items.Clear();
            if (preset.AuthStyle == "cli") AddModel(modelCombo, Localization.T("Ai.CliDefaultModel"));
            else AddModel(modelCombo, preset.DefaultModel);
            foreach (string model in AiChatService.KnownCliModels(provider)) AddModel(modelCombo, model);
            if (string.Equals(provider, _currentSettings.Provider, StringComparison.OrdinalIgnoreCase))
                AddModel(modelCombo, _currentSettings.Model);
            AddModel(modelCombo, preferredModel);
            modelCombo.Text = !string.IsNullOrWhiteSpace(preferredModel)
                ? preferredModel
                : (preset.AuthStyle == "cli" ? Localization.T("Ai.CliDefaultModel") : (preset.DefaultModel ?? string.Empty));
            modelCombo.EndUpdate();
        }

        private static void AddModel(ComboBox combo, string model)
        {
            string value = (model ?? string.Empty).Trim();
            if (value.Length == 0) return;
            foreach (object item in combo.Items)
            {
                if (string.Equals(Convert.ToString(item), value, StringComparison.OrdinalIgnoreCase)) return;
            }
            combo.Items.Add(value);
        }

        private void ResolveInitialRightChoice(out string providerId, out string model)
        {
            string savedProvider = ApplicationOptionSettings.GetString("AiCompareProvider");
            string savedModel = ApplicationOptionSettings.GetString("AiCompareModel");
            AiChatSettings saved = AiModelComparisonService.CreateSettings(savedProvider, savedModel, _currentSettings);
            AiModelComparisonFailure ignored;
            if (AiModelComparisonService.FindExactPreset(savedProvider) != null
                && AiModelComparisonService.TryValidate(_currentSettings, saved, out ignored))
            {
                providerId = savedProvider;
                model = savedModel;
                return;
            }

            foreach (string candidate in AiChatService.KnownCliModels(_currentSettings.Provider))
            {
                if (!string.Equals(candidate, _currentSettings.Model, StringComparison.OrdinalIgnoreCase))
                {
                    providerId = _currentSettings.Provider;
                    model = candidate;
                    return;
                }
            }

            foreach (AiProviderPreset installed in AiChatService.DetectInstalledClis())
            {
                if (string.Equals(installed.Id, _currentSettings.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                providerId = installed.Id;
                string[] models = AiChatService.KnownCliModels(installed.Id);
                model = models.Length == 0 ? string.Empty : models[0];
                return;
            }

            foreach (AiProviderPreset preset in AiChatService.Presets)
            {
                if (string.Equals(preset.Id, _currentSettings.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                if (preset.AuthStyle == "cli") continue;
                if (preset.NeedsKey && !AiChatService.HasApiKey(preset.Id)) continue;
                providerId = preset.Id;
                model = preset.DefaultModel ?? string.Empty;
                return;
            }

            foreach (AiProviderPreset preset in AiChatService.Presets)
            {
                if (string.Equals(preset.Id, _currentSettings.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                providerId = preset.Id;
                model = preset.DefaultModel ?? string.Empty;
                return;
            }

            providerId = _currentSettings.Provider;
            model = _currentSettings.Model;
        }

        private async Task StartComparisonAsync()
        {
            if (_busy) return;
            if (_prompt.Length == 0)
            {
                SetStatus(Localization.T("Ai.CompareNoPrompt"), true);
                return;
            }

            AiChatSettings left = AiModelComparisonService.CreateSettings(
                GetProviderId(_leftProviderCombo), GetModelValue(_leftProviderCombo, _leftModelCombo), _currentSettings);
            AiChatSettings right = AiModelComparisonService.CreateSettings(
                GetProviderId(_rightProviderCombo), GetModelValue(_rightProviderCombo, _rightModelCombo), _currentSettings);
            AiModelComparisonFailure failure;
            if (!AiModelComparisonService.TryValidate(left, right, out failure))
            {
                SetStatus(GetFailureMessage(failure), true);
                return;
            }
            if (!HasRequiredCredential(left) || !HasRequiredCredential(right)) return;

            ApplicationOptionSettings.SetString("AiCompareProvider", right.Provider);
            ApplicationOptionSettings.SetString("AiCompareModel", right.Model);
            ApplicationOptionSettings.Save();

            ResetResultViews();
            string leftLabel = BuildChoiceLabel(left);
            string rightLabel = BuildChoiceLabel(right);
            AiChatBubble leftBubble = _leftView.AddAssistant(leftLabel, Localization.T("Ai.Thinking"));
            AiChatBubble rightBubble = _rightView.AddAssistant(rightLabel, Localization.T("Ai.Thinking"));
            List<AiChatMessage> leftMessages = BuildMessages();
            List<AiChatMessage> rightMessages = BuildMessages();

            SetBusy(true);
            SetStatus(Localization.T("Ai.CompareRunning"), false);
            Task leftTask = RunChoiceAsync(left, leftMessages, _leftView, leftBubble);
            Task rightTask = RunChoiceAsync(right, rightMessages, _rightView, rightBubble);
            await Task.WhenAll(leftTask, rightTask);
            if (IsDisposed) return;
            SetBusy(false);
            SetStatus(Localization.T("Ai.CompareCompleted"), false);
        }

        private List<AiChatMessage> BuildMessages()
        {
            return AiModelComparisonService.BuildMessages(
                Localization.T("Ai.SystemPrompt"),
                Localization.T("Ai.ContextPrefix"),
                _schemaContext,
                _history,
                _prompt);
        }

        private async Task RunChoiceAsync(
            AiChatSettings settings,
            IList<AiChatMessage> messages,
            AiChatView view,
            AiChatBubble bubble)
        {
            try
            {
                string reply = await Task.Run(() => AiChatService.ChatCompletion(settings, messages));
                if (IsDisposed || view.IsDisposed) return;
                bubble.SetContent(reply);
                view.ScrollToBottom();
            }
            catch (Exception ex)
            {
                if (IsDisposed || view.IsDisposed) return;
                view.RemoveBubble(bubble);
                view.AddError(Localization.Format("Ai.RequestFailed", ex.Message));
            }
        }

        private bool HasRequiredCredential(AiChatSettings settings)
        {
            if (!settings.Preset.NeedsKey || AiChatService.HasApiKey(settings.Provider)) return true;
            SetStatus(Localization.Format("Ai.CompareMissingKey", settings.Preset.DisplayName), true);
            return false;
        }

        private void ResetResultViews()
        {
            if (_leftView != null) _leftView.Dispose();
            if (_rightView != null) _rightView.Dispose();
            _leftView = new AiChatView { Dock = DockStyle.Fill };
            _rightView = new AiChatView { Dock = DockStyle.Fill };
            _leftResultHost.Controls.Clear();
            _rightResultHost.Controls.Clear();
            _leftResultHost.Controls.Add(_leftView);
            _rightResultHost.Controls.Add(_rightView);
            _leftView.AddSystem(Localization.T("Ai.CompareWaiting"));
            _rightView.AddSystem(Localization.T("Ai.CompareWaiting"));
            RefreshResultThemes();
        }

        private void RefreshResultThemes()
        {
            if (_leftView != null)
            {
                _leftView.BackColor = ThemeManager.WindowBackColor;
                _leftView.RefreshTheme();
            }
            if (_rightView != null)
            {
                _rightView.BackColor = ThemeManager.WindowBackColor;
                _rightView.RefreshTheme();
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _leftProviderCombo.Enabled = !busy;
            _leftModelCombo.Enabled = !busy;
            _rightProviderCombo.Enabled = !busy;
            _rightModelCombo.Enabled = !busy;
            _compareButton.Enabled = !busy;
        }

        private void SetStatus(string text, bool isError)
        {
            _statusLabel.Text = text ?? string.Empty;
            _statusLabel.ForeColor = isError ? ThemeManager.DangerColor : ThemeManager.MutedTextColor;
        }

        private static string GetProviderId(ComboBox combo)
        {
            int index = combo.SelectedIndex;
            return index >= 0 && index < AiChatService.Presets.Length
                ? AiChatService.Presets[index].Id
                : string.Empty;
        }

        private static string GetModelValue(ComboBox providerCombo, ComboBox modelCombo)
        {
            string value = (modelCombo.Text ?? string.Empty).Trim();
            AiProviderPreset preset = AiModelComparisonService.FindExactPreset(GetProviderId(providerCombo));
            return preset != null
                && preset.AuthStyle == "cli"
                && string.Equals(value, Localization.T("Ai.CliDefaultModel"), StringComparison.Ordinal)
                    ? string.Empty
                    : value;
        }

        private static string BuildChoiceLabel(AiChatSettings settings)
        {
            string model = string.IsNullOrWhiteSpace(settings.Model)
                ? Localization.T("Ai.CliDefaultModel")
                : settings.Model;
            return settings.Preset.DisplayName + " · " + model;
        }

        private static string GetFailureMessage(AiModelComparisonFailure failure)
        {
            switch (failure)
            {
                case AiModelComparisonFailure.InvalidProvider: return Localization.T("Ai.CompareInvalidProvider");
                case AiModelComparisonFailure.MissingEndpoint: return Localization.T("Ai.CompareMissingEndpoint");
                case AiModelComparisonFailure.MissingModel: return Localization.T("Ai.CompareMissingModel");
                case AiModelComparisonFailure.SameModel: return Localization.T("Ai.CompareSameModel");
                default: return Localization.T("Ai.CompareInvalidProvider");
            }
        }

        private static List<AiChatMessage> CloneHistory(IList<AiChatMessage> history)
        {
            List<AiChatMessage> copy = new List<AiChatMessage>();
            if (history == null) return copy;
            foreach (AiChatMessage message in history)
            {
                if (message != null) copy.Add(new AiChatMessage(message.Role, message.Content));
            }
            return copy;
        }

        private void OnComparisonFormClosing(object sender, FormClosingEventArgs args)
        {
            if (!_busy) return;
            args.Cancel = true;
            SetStatus(Localization.T("Ai.CompareWaitToClose"), true);
        }
    }
}

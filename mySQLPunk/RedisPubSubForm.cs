using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>Redis Pub/Sub 訊息工作區；每個頁籤只維護一條接收用專線。</summary>
    public sealed class RedisPubSubForm : Form, IDockableForm
    {
        private const int MaxMessages = 1000;

        private readonly my_redis _database;
        private readonly string _databaseName;
        private readonly ComboBox _modeCombo;
        private readonly TextBox _topicText;
        private readonly Button _subscribeButton;
        private readonly Button _clearButton;
        private readonly DataGridView _messagesGrid;
        private readonly TextBox _publishChannelText;
        private readonly TextBox _publishMessageText;
        private readonly Button _publishButton;
        private readonly Label _statusLabel;
        private Form1 _mainHost;
        private RedisPubSubSubscription _subscription;
        private bool _busy;

        public RedisPubSubForm(my_redis database, string databaseName)
        {
            _database = database ?? throw new ArgumentNullException("database");
            _databaseName = databaseName ?? string.Empty;

            Text = Localization.Format("Redis.PubSubWindowTitle", _databaseName);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 680);
            MinimumSize = new Size(760, 500);
            Font = UiKit.Body;

            UiSectionHeader header = new UiSectionHeader
            {
                Dock = DockStyle.Top,
                Title = Localization.T("Redis.PubSubTitle"),
                Subtitle = Localization.Format("Redis.PubSubSubtitle", _databaseName),
                Glyph = UiGlyph.Plug
            };

            FlowLayoutPanel subscriptionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(UiMetrics.Space3, 6, UiMetrics.Space3, 4),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            subscriptionBar.Controls.Add(CreateBarLabel(Localization.T("Redis.PubSubMode")));
            _modeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                Margin = new Padding(0, 1, UiMetrics.Space3, 0)
            };
            _modeCombo.Items.Add(Localization.T("Redis.PubSubChannelMode"));
            _modeCombo.Items.Add(Localization.T("Redis.PubSubPatternMode"));
            _modeCombo.SelectedIndex = 0;
            Control modeField = UiField.Wrap(_modeCombo);
            modeField.Width = 120;
            modeField.Margin = new Padding(0, 1, UiMetrics.Space3, 0);
            subscriptionBar.Controls.Add(modeField);

            subscriptionBar.Controls.Add(CreateBarLabel(Localization.T("Redis.PubSubTopic")));
            _topicText = new TextBox { Width = 290 };
            Control topicField = UiField.Wrap(_topicText);
            topicField.Width = 290;
            topicField.Margin = new Padding(0, 1, UiMetrics.Space3, 0);
            subscriptionBar.Controls.Add(topicField);

            _subscribeButton = new Button
            {
                Text = Localization.T("Redis.PubSubSubscribe"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Margin = new Padding(0, 1, UiMetrics.Space2, 0)
            };
            _subscribeButton.Click += async (sender, args) => await ToggleSubscriptionAsync();
            ThemeManager.MarkAsPrimary(_subscribeButton);
            subscriptionBar.Controls.Add(_subscribeButton);

            _clearButton = new Button
            {
                Text = Localization.T("Redis.PubSubClear"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Margin = new Padding(0, 1, 0, 0)
            };
            _clearButton.Click += (sender, args) => _messagesGrid.Rows.Clear();
            subscriptionBar.Controls.Add(_clearButton);

            _messagesGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = ThemeManager.WindowBackColor
            };
            _messagesGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "time",
                HeaderText = Localization.T("Redis.PubSubReceivedAt"),
                FillWeight = 18
            });
            _messagesGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "pattern",
                HeaderText = Localization.T("Redis.PubSubPattern"),
                FillWeight = 18
            });
            _messagesGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "channel",
                HeaderText = Localization.T("Redis.PubSubChannel"),
                FillWeight = 24
            });
            _messagesGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "message",
                HeaderText = Localization.T("Redis.PubSubMessage"),
                FillWeight = 40
            });

            TableLayoutPanel publisher = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 142,
                Padding = new Padding(UiMetrics.Space3, UiMetrics.Space2, UiMetrics.Space3, UiMetrics.Space2),
                ColumnCount = 3,
                RowCount = 2
            };
            publisher.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            publisher.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            publisher.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            publisher.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            publisher.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            publisher.Controls.Add(CreateComposerLabel(Localization.T("Redis.PubSubPublishChannel")), 0, 0);
            _publishChannelText = new TextBox { Dock = DockStyle.Fill };
            Control channelField = UiField.Wrap(_publishChannelText);
            channelField.Dock = DockStyle.Fill;
            channelField.Margin = new Padding(0, 2, UiMetrics.Space3, 4);
            publisher.Controls.Add(channelField, 1, 0);

            _publishButton = new Button
            {
                Text = Localization.T("Redis.PubSubPublish"),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 4)
            };
            _publishButton.Click += async (sender, args) => await PublishAsync();
            ThemeManager.MarkAsPrimary(_publishButton);
            publisher.Controls.Add(_publishButton, 2, 0);

            publisher.Controls.Add(CreateComposerLabel(Localization.T("Redis.PubSubPayload")), 0, 1);
            _publishMessageText = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical
            };
            Control messageField = UiField.Wrap(_publishMessageText);
            messageField.Dock = DockStyle.Fill;
            messageField.Margin = new Padding(0, 0, UiMetrics.Space3, 0);
            publisher.Controls.Add(messageField, 1, 1);
            publisher.SetColumnSpan(messageField, 2);

            Panel statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 32 };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space2, 0),
                Text = Localization.T("Redis.PubSubReady")
            };
            statusPanel.Controls.Add(_statusLabel);

            Controls.Add(_messagesGrid);
            Controls.Add(publisher);
            Controls.Add(statusPanel);
            Controls.Add(subscriptionBar);
            Controls.Add(header);
            ThemeManager.ApplyTo(this);
        }

        public void SetMainHost(Form1 mainHost) { _mainHost = mainHost; }
        public string GetDisplayTitle() { return Text; }
        public bool HasUnsavedChanges() { return false; }
        public bool UsesDatabase(IDatabase database) { return database != null && ReferenceEquals(_database, database); }

        public void PrepareForDocking()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            TopMost = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        public void PrepareForFloating()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            Dock = DockStyle.None;
            TopLevel = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
        }

        private async Task ToggleSubscriptionAsync()
        {
            if (_busy) return;
            if (_subscription != null)
            {
                StopSubscription(Localization.T("Redis.PubSubStopped"));
                return;
            }

            string topic = _topicText.Text;
            if (string.IsNullOrWhiteSpace(topic))
            {
                _statusLabel.Text = Localization.T("Redis.PubSubTopicRequired");
                _topicText.Focus();
                return;
            }

            bool pattern = _modeCombo.SelectedIndex == 1;
            SetBusy(true);
            _statusLabel.Text = Localization.T("Redis.PubSubConnecting");
            try
            {
                RedisPubSubSubscription created = await Task.Run(
                    () => _database.CreatePubSubSubscription(_databaseName, topic, pattern));
                if (IsDisposed)
                {
                    created.Dispose();
                    return;
                }
                created.MessageReceived += SubscriptionMessageReceived;
                created.Failed += SubscriptionFailed;
                _subscription = created;
                created.Start();
                _modeCombo.Enabled = false;
                _topicText.ReadOnly = true;
                _subscribeButton.Text = Localization.T("Redis.PubSubStop");
                _statusLabel.Text = Localization.Format("Redis.PubSubSubscribed", topic);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    _statusLabel.Text = Localization.Format("Redis.PubSubSubscribeFailed", ex.Message);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        private async Task PublishAsync()
        {
            if (_busy) return;
            string channel = _publishChannelText.Text;
            if (string.IsNullOrWhiteSpace(channel))
            {
                _statusLabel.Text = Localization.T("Redis.PubSubChannelRequired");
                _publishChannelText.Focus();
                return;
            }

            SetBusy(true);
            try
            {
                string payload = _publishMessageText.Text;
                long receivers = await Task.Run(() => _database.Publish(channel, payload));
                if (!IsDisposed)
                    _statusLabel.Text = Localization.Format("Redis.PubSubPublished", receivers);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    _statusLabel.Text = Localization.Format("Redis.PubSubPublishFailed", ex.Message);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
        }

        private void SubscriptionMessageReceived(object sender, RedisPubSubMessageEventArgs args)
        {
            if (IsDisposed || !ReferenceEquals(sender, _subscription)) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !ReferenceEquals(sender, _subscription)) return;
                    _messagesGrid.Rows.Add(
                        args.ReceivedAtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
                        args.Pattern ?? string.Empty,
                        args.Channel ?? string.Empty,
                        args.Message ?? string.Empty);
                    while (_messagesGrid.Rows.Count > MaxMessages) _messagesGrid.Rows.RemoveAt(0);
                    _statusLabel.Text = Localization.Format("Redis.PubSubReceived", _messagesGrid.Rows.Count);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void SubscriptionFailed(object sender, RedisPubSubErrorEventArgs args)
        {
            if (IsDisposed || !ReferenceEquals(sender, _subscription)) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !ReferenceEquals(sender, _subscription)) return;
                    string reason = args.Error == null ? string.Empty : args.Error.Message;
                    StopSubscription(Localization.Format("Redis.PubSubDisconnected", reason));
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void StopSubscription(string status)
        {
            RedisPubSubSubscription current = _subscription;
            _subscription = null;
            if (current != null)
            {
                current.MessageReceived -= SubscriptionMessageReceived;
                current.Failed -= SubscriptionFailed;
                current.Dispose();
            }
            _modeCombo.Enabled = true;
            _topicText.ReadOnly = false;
            _subscribeButton.Text = Localization.T("Redis.PubSubSubscribe");
            if (!string.IsNullOrWhiteSpace(status)) _statusLabel.Text = status;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _subscribeButton.Enabled = !busy;
            _publishButton.Enabled = !busy;
        }

        private static Label CreateBarLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 7, UiMetrics.Space2, 0) };
        }

        private static Label CreateComposerLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopSubscription(string.Empty);
            if (_mainHost != null) _mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _subscription != null) StopSubscription(string.Empty);
            base.Dispose(disposing);
        }
    }
}

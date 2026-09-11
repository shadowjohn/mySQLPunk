using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>Redis／Garnet INFO 即時監控：摘要指標與命令統計共用目前已開啟的 provider 連線。</summary>
    public sealed class RedisMonitorForm : Form, IDockableForm
    {
        private readonly my_redis _database;
        private readonly string _databaseName;
        private readonly DataGridView _metricsGrid;
        private readonly DataGridView _commandsGrid;
        private readonly Button _refreshButton;
        private readonly CheckBox _autoRefreshCheck;
        private readonly ComboBox _intervalCombo;
        private readonly Label _statusLabel;
        private readonly Timer _timer;
        private Form1 _mainHost;
        private bool _loading;
        private bool _started;

        public RedisMonitorForm(my_redis database, string databaseName)
        {
            _database = database ?? throw new ArgumentNullException("database");
            _databaseName = databaseName ?? string.Empty;

            Text = Localization.Format("Redis.MonitorWindowTitle", _databaseName);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 680);
            MinimumSize = new Size(760, 500);
            Font = UiKit.Body;

            UiSectionHeader header = new UiSectionHeader
            {
                Dock = DockStyle.Top,
                Title = Localization.T("Redis.MonitorTitle"),
                Subtitle = _databaseName + " · Redis / Garnet INFO",
                Glyph = UiGlyph.Chart
            };

            FlowLayoutPanel commands = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space3, 5),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            _refreshButton = new Button
            {
                Text = Localization.T("Query.Refresh"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Margin = new Padding(0, 1, UiMetrics.Space3, 0)
            };
            _refreshButton.Click += async (sender, args) => await RefreshSnapshotAsync();
            ThemeManager.MarkAsPrimary(_refreshButton);
            commands.Controls.Add(_refreshButton);

            _autoRefreshCheck = new CheckBox
            {
                Text = Localization.T("Redis.MonitorAutoRefresh"),
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 6, UiMetrics.Space3, 0)
            };
            _autoRefreshCheck.CheckedChanged += (sender, args) => UpdateTimer();
            commands.Controls.Add(_autoRefreshCheck);

            commands.Controls.Add(new Label
            {
                Text = Localization.T("Redis.MonitorInterval"),
                AutoSize = true,
                Margin = new Padding(0, 7, UiMetrics.Space2, 0)
            });
            _intervalCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 105,
                Margin = new Padding(0, 2, 0, 0)
            };
            foreach (int seconds in new[] { 1, 5, 10, 30 })
                _intervalCombo.Items.Add(Localization.Format("Redis.MonitorSeconds", seconds));
            _intervalCombo.SelectedIndex = 1;
            _intervalCombo.SelectedIndexChanged += (sender, args) => UpdateTimer();
            Control intervalField = UiField.Wrap(_intervalCombo);
            intervalField.Width = 105;
            intervalField.Margin = new Padding(0, 2, 0, 0);
            commands.Controls.Add(intervalField);

            _metricsGrid = CreateReadOnlyGrid();
            _metricsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "section",
                HeaderText = Localization.T("Redis.MonitorSection"),
                FillWeight = 24
            });
            _metricsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "metric",
                HeaderText = Localization.T("Redis.MonitorMetric"),
                FillWeight = 42
            });
            _metricsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "value",
                HeaderText = Localization.T("Redis.MonitorValue"),
                FillWeight = 34
            });

            _commandsGrid = CreateReadOnlyGrid();
            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "command",
                HeaderText = Localization.T("Redis.MonitorCommand"),
                FillWeight = 30
            });
            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "calls",
                HeaderText = Localization.T("Redis.MonitorCalls"),
                FillWeight = 18
            });
            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "average",
                HeaderText = Localization.T("Redis.MonitorAverageUsec"),
                FillWeight = 20
            });
            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "failed",
                HeaderText = Localization.T("Redis.MonitorFailedCalls"),
                FillWeight = 16
            });
            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "rejected",
                HeaderText = Localization.T("Redis.MonitorRejectedCalls"),
                FillWeight = 16
            });

            Panel commandPanel = new Panel { Dock = DockStyle.Fill };
            commandPanel.Controls.Add(_commandsGrid);
            commandPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space2, 0),
                Text = Localization.T("Redis.MonitorCommandStats"),
                Font = UiKit.BodyBold
            });

            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 360,
                Panel1MinSize = 220,
                Panel2MinSize = 120
            };
            split.Panel1.Controls.Add(_metricsGrid);
            split.Panel2.Controls.Add(commandPanel);

            Panel statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 32 };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space2, 0),
                Text = Localization.T("Redis.MonitorReady")
            };
            statusPanel.Controls.Add(_statusLabel);

            Controls.Add(split);
            Controls.Add(statusPanel);
            Controls.Add(commands);
            Controls.Add(header);

            _timer = new Timer { Interval = 5000 };
            _timer.Tick += async (sender, args) => await RefreshSnapshotAsync();
            Shown += async (sender, args) =>
            {
                if (_started) return;
                _started = true;
                await RefreshSnapshotAsync();
                UpdateTimer();
            };
            ThemeManager.ApplyTo(this);
        }

        public void SetMainHost(Form1 mainHost)
        {
            _mainHost = mainHost;
        }

        public string GetDisplayTitle()
        {
            return Text;
        }

        public bool HasUnsavedChanges()
        {
            return false;
        }

        public bool UsesDatabase(IDatabase database)
        {
            return database != null && ReferenceEquals(_database, database);
        }

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

        private async Task RefreshSnapshotAsync()
        {
            if (_loading || IsDisposed) return;
            _loading = true;
            _refreshButton.Enabled = false;
            _statusLabel.Text = Localization.T("Redis.MonitorLoading");
            try
            {
                RedisMonitorSnapshot snapshot = await Task.Run(() => _database.GetMonitorSnapshot(_databaseName));
                if (IsDisposed) return;
                PopulateSnapshot(snapshot);
                _statusLabel.Text = Localization.Format(
                    "Redis.MonitorUpdated",
                    snapshot.CapturedAtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture));
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _statusLabel.Text = Localization.Format("Redis.MonitorFailed", ex.Message);
            }
            finally
            {
                if (!IsDisposed)
                {
                    _loading = false;
                    _refreshButton.Enabled = true;
                }
            }
        }

        private void PopulateSnapshot(RedisMonitorSnapshot snapshot)
        {
            _metricsGrid.Rows.Clear();
            foreach (RedisMonitorMetric metric in snapshot.Metrics)
                _metricsGrid.Rows.Add(GetSectionText(metric.Section), metric.Name, metric.Value);

            _commandsGrid.Rows.Clear();
            foreach (RedisCommandMetric command in snapshot.Commands)
            {
                _commandsGrid.Rows.Add(
                    command.Command,
                    command.Calls.ToString("N0", CultureInfo.CurrentCulture),
                    command.MicrosecondsPerCall.ToString("0.###", CultureInfo.CurrentCulture),
                    command.FailedCalls.ToString("N0", CultureInfo.CurrentCulture),
                    command.RejectedCalls.ToString("N0", CultureInfo.CurrentCulture));
            }
        }

        private string GetSectionText(string section)
        {
            switch (section)
            {
                case "database": return Localization.T("Redis.MonitorSectionDatabase");
                case "server": return Localization.T("Redis.MonitorSectionServer");
                case "clients": return Localization.T("Redis.MonitorSectionClients");
                case "memory": return Localization.T("Redis.MonitorSectionMemory");
                case "activity": return Localization.T("Redis.MonitorSectionActivity");
                case "network": return Localization.T("Redis.MonitorSectionNetwork");
                case "cpu": return Localization.T("Redis.MonitorSectionCpu");
                case "persistence": return Localization.T("Redis.MonitorSectionPersistence");
                case "replication": return Localization.T("Redis.MonitorSectionReplication");
                default: return section ?? string.Empty;
            }
        }

        private void UpdateTimer()
        {
            if (_timer == null) return;
            int[] seconds = { 1, 5, 10, 30 };
            int index = Math.Max(0, Math.Min(seconds.Length - 1, _intervalCombo.SelectedIndex));
            _timer.Interval = seconds[index] * 1000;
            _timer.Enabled = _started && _autoRefreshCheck.Checked && !IsDisposed;
        }

        private static DataGridView CreateReadOnlyGrid()
        {
            return new DataGridView
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
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            if (_mainHost != null) _mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

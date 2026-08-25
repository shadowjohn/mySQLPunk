using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 資料表欄位分析工作區：摘要網格顯示 NULL、相異值、極值與平均值，下方以長條比例
    /// 顯示目前欄位的 Top 值。選取分佈值後可開啟帶 WHERE 的查詢做互動鑽取。
    /// </summary>
    public sealed class DataProfileForm : Form, IDockableForm
    {
        private readonly IDatabase _database;
        private readonly string _databaseName;
        private readonly string _tableName;
        private readonly Action<string> _openQuery;
        private readonly UiSectionHeader _header;
        private readonly ComboBox _sampleRows;
        private readonly Button _runButton;
        private readonly Button _cancelButton;
        private readonly Button _drilldownButton;
        private readonly DataGridView _summaryGrid;
        private readonly DataGridView _distributionGrid;
        private readonly Label _distributionTitle;
        private readonly Label _statusLabel;
        private readonly ProgressBar _progressBar;
        private CancellationTokenSource _analysisCancellation;
        private Form1 _mainHost;
        private bool _started;

        public DataProfileForm(
            IDatabase database,
            string databaseName,
            string tableName,
            Action<string> openQuery)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _databaseName = databaseName ?? string.Empty;
            _tableName = tableName ?? string.Empty;
            _openQuery = openQuery;

            Text = Localization.Format("DataProfile.WindowTitle", _tableName);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1120, 760);
            MinimumSize = new Size(860, 560);
            Font = UiKit.Body;

            _header = new UiSectionHeader
            {
                Dock = DockStyle.Top,
                Title = Localization.T("DataProfile.Title"),
                Subtitle = _databaseName + " · " + _tableName + " · " + _database.ProviderName,
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
            commands.Controls.Add(new Label
            {
                Text = Localization.T("DataProfile.SampleRows"),
                AutoSize = true,
                Margin = new Padding(0, 7, UiMetrics.Space2, 0)
            });

            _sampleRows = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 130,
                Margin = new Padding(0, 2, UiMetrics.Space3, 0)
            };
            _sampleRows.Items.Add("1,000");
            _sampleRows.Items.Add("10,000");
            _sampleRows.Items.Add("50,000");
            _sampleRows.Items.Add(Localization.T("DataProfile.AllRows"));
            _sampleRows.SelectedIndex = 1;
            commands.Controls.Add(_sampleRows);

            _runButton = new Button
            {
                Text = Localization.T("DataProfile.Analyze"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Margin = new Padding(0, 1, UiMetrics.Space2, 0)
            };
            _runButton.Click += async (sender, args) => await RunAnalysisAsync();
            ThemeManager.MarkAsPrimary(_runButton);
            commands.Controls.Add(_runButton);

            _cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Enabled = false,
                Margin = new Padding(0, 1, UiMetrics.Space3, 0)
            };
            _cancelButton.Click += (sender, args) => _analysisCancellation?.Cancel();
            commands.Controls.Add(_cancelButton);

            _drilldownButton = new Button
            {
                Text = Localization.T("DataProfile.Drilldown"),
                AutoSize = true,
                Height = UiMetrics.ControlHeight,
                Enabled = false,
                Margin = new Padding(0, 1, 0, 0)
            };
            _drilldownButton.Click += (sender, args) => OpenSelectedBucket();
            commands.Controls.Add(_drilldownButton);

            _summaryGrid = CreateReadOnlyGrid();
            ConfigureSummaryColumns();
            _summaryGrid.SelectionChanged += (sender, args) => ShowSelectedColumnDistribution();

            Panel distributionPanel = new Panel { Dock = DockStyle.Fill };
            _distributionTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space2, 0),
                Text = Localization.T("DataProfile.TopValues"),
                Font = UiKit.BodyBold
            };
            _distributionGrid = CreateReadOnlyGrid();
            ConfigureDistributionColumns();
            _distributionGrid.SelectionChanged += (sender, args) => UpdateDrilldownButton();
            _distributionGrid.CellDoubleClick += (sender, args) =>
            {
                if (args.RowIndex >= 0) OpenSelectedBucket();
            };
            _distributionGrid.CellPainting += DistributionGridCellPainting;
            distributionPanel.Controls.Add(_distributionGrid);
            distributionPanel.Controls.Add(_distributionTitle);

            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 365,
                Panel1MinSize = 220,
                Panel2MinSize = 150
            };
            split.Panel1.Controls.Add(_summaryGrid);
            split.Panel2.Controls.Add(distributionPanel);

            Panel statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 32 };
            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Right,
                Width = 190,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space3, 7, UiMetrics.Space2, 0),
                Text = Localization.T("DataProfile.Ready")
            };
            statusPanel.Controls.Add(_statusLabel);
            statusPanel.Controls.Add(_progressBar);

            Controls.Add(split);
            Controls.Add(statusPanel);
            Controls.Add(commands);
            Controls.Add(_header);

            Shown += async (sender, args) =>
            {
                if (_started) return;
                _started = true;
                await RunAnalysisAsync();
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

        private async Task RunAnalysisAsync()
        {
            CancellationTokenSource previous = _analysisCancellation;
            previous?.Cancel();
            previous?.Dispose();
            _analysisCancellation = new CancellationTokenSource();
            CancellationTokenSource current = _analysisCancellation;

            _summaryGrid.Rows.Clear();
            _distributionGrid.Rows.Clear();
            _distributionTitle.Text = Localization.T("DataProfile.TopValues");
            _runButton.Enabled = false;
            _cancelButton.Enabled = true;
            _drilldownButton.Enabled = false;
            _sampleRows.Enabled = false;
            _progressBar.Visible = true;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1;
            _progressBar.Value = 0;
            _statusLabel.Text = Localization.T("DataProfile.LoadingMetadata");

            Progress<DataProfileProgress> progress = new Progress<DataProfileProgress>(value =>
            {
                if (IsDisposed || current != _analysisCancellation) return;
                _progressBar.Maximum = Math.Max(1, value.TotalColumns);
                _progressBar.Value = Math.Min(_progressBar.Maximum, value.CompletedColumns);
                _statusLabel.Text = Localization.Format(
                    "DataProfile.AnalyzingColumn",
                    value.ColumnName,
                    value.CompletedColumns,
                    value.TotalColumns);
            });

            try
            {
                DataProfileReport report = await DataProfilingService.AnalyzeAsync(
                    _database,
                    _databaseName,
                    _tableName,
                    GetSampleLimit(),
                    10,
                    progress,
                    current.Token);
                if (IsDisposed || current != _analysisCancellation) return;

                PopulateSummary(report);
                string totalRows = report.TotalRowCount < 0
                    ? Localization.T("Common.NotAvailable")
                    : FormatLong(report.TotalRowCount);
                _statusLabel.Text = Localization.Format(
                    "DataProfile.Completed",
                    report.Columns.Count,
                    FormatLong(report.SampleRowCount),
                    totalRows);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && current == _analysisCancellation)
                {
                    _statusLabel.Text = Localization.T("DataProfile.Cancelled");
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && current == _analysisCancellation)
                {
                    _statusLabel.Text = Localization.Format("DataProfile.Failed", ex.Message);
                    MessageBox.Show(this,
                        Localization.Format("DataProfile.Failed", ex.Message),
                        Localization.T("DataProfile.Title"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (!IsDisposed && current == _analysisCancellation)
                {
                    _runButton.Enabled = true;
                    _cancelButton.Enabled = false;
                    _sampleRows.Enabled = true;
                    _progressBar.Visible = false;
                }
            }
        }

        private int GetSampleLimit()
        {
            switch (_sampleRows.SelectedIndex)
            {
                case 0: return 1000;
                case 2: return 50000;
                case 3: return 0;
                default: return 10000;
            }
        }

        private void PopulateSummary(DataProfileReport report)
        {
            _summaryGrid.Rows.Clear();
            foreach (DataProfileColumnResult column in report.Columns)
            {
                string topValue = column.TopValues.Count == 0
                    ? string.Empty
                    : FormatValue(column.TopValues[0].Value) + " × " + FormatLong(column.TopValues[0].Count);
                int rowIndex = _summaryGrid.Rows.Add(
                    column.ColumnName,
                    column.DataType,
                    FormatLong(column.SampleCount),
                    FormatLong(column.NullCount),
                    column.HasDistinctCount ? FormatLong(column.DistinctCount) : Localization.T("Common.NotAvailable"),
                    column.HasRange ? FormatValue(column.Minimum) : string.Empty,
                    column.HasRange ? FormatValue(column.Maximum) : string.Empty,
                    column.HasAverage ? FormatValue(column.Average) : string.Empty,
                    topValue,
                    column.IsPartial ? Localization.T("DataProfile.Partial") : Localization.T("DataProfile.Complete"));
                DataGridViewRow row = _summaryGrid.Rows[rowIndex];
                row.Tag = column;
                if (column.Warnings.Count > 0)
                {
                    row.Cells[9].ToolTipText = string.Join(Environment.NewLine, column.Warnings.Distinct().ToArray());
                }
            }

            if (_summaryGrid.Rows.Count > 0)
            {
                _summaryGrid.ClearSelection();
                _summaryGrid.Rows[0].Selected = true;
                _summaryGrid.CurrentCell = _summaryGrid.Rows[0].Cells[0];
                ShowSelectedColumnDistribution();
            }
        }

        private void ShowSelectedColumnDistribution()
        {
            DataProfileColumnResult column = GetSelectedColumn();
            _distributionGrid.Rows.Clear();
            _drilldownButton.Enabled = false;
            if (column == null)
            {
                _distributionTitle.Text = Localization.T("DataProfile.TopValues");
                return;
            }

            _distributionTitle.Text = Localization.Format("DataProfile.TopValuesForColumn", column.ColumnName);
            long maximum = column.TopValues.Count == 0 ? 0L : column.TopValues.Max(bucket => bucket.Count);
            foreach (DataProfileValueBucket bucket in column.TopValues)
            {
                double ratio = column.SampleCount <= 0 ? 0d : (double)bucket.Count / column.SampleCount;
                int rowIndex = _distributionGrid.Rows.Add(
                    FormatValue(bucket.Value),
                    FormatLong(bucket.Count),
                    ratio.ToString("P1", CultureInfo.CurrentCulture));
                DataGridViewRow row = _distributionGrid.Rows[rowIndex];
                row.Tag = bucket;
                row.Cells[2].Tag = maximum <= 0 ? 0d : (double)bucket.Count / maximum;
            }

            if (_distributionGrid.Rows.Count > 0)
            {
                _distributionGrid.ClearSelection();
                _distributionGrid.Rows[0].Selected = true;
                _distributionGrid.CurrentCell = _distributionGrid.Rows[0].Cells[0];
            }
            UpdateDrilldownButton();
        }

        private DataProfileColumnResult GetSelectedColumn()
        {
            if (_summaryGrid.CurrentRow != null) return _summaryGrid.CurrentRow.Tag as DataProfileColumnResult;
            return _summaryGrid.SelectedRows.Count > 0 ? _summaryGrid.SelectedRows[0].Tag as DataProfileColumnResult : null;
        }

        private DataProfileValueBucket GetSelectedBucket()
        {
            if (_distributionGrid.CurrentRow != null) return _distributionGrid.CurrentRow.Tag as DataProfileValueBucket;
            return _distributionGrid.SelectedRows.Count > 0 ? _distributionGrid.SelectedRows[0].Tag as DataProfileValueBucket : null;
        }

        private void UpdateDrilldownButton()
        {
            _drilldownButton.Enabled = _openQuery != null && GetSelectedColumn() != null && GetSelectedBucket() != null;
        }

        private void OpenSelectedBucket()
        {
            DataProfileColumnResult column = GetSelectedColumn();
            DataProfileValueBucket bucket = GetSelectedBucket();
            if (column == null || bucket == null || _openQuery == null) return;

            string sql = DataProfilingService.BuildDrilldownSql(
                _database,
                _databaseName,
                _tableName,
                column.ColumnName,
                bucket.Value,
                200);
            _openQuery(sql);
        }

        private void DistributionGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 2) return;
            object tag = _distributionGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag;
            double ratio = tag is double ? (double)tag : 0d;

            e.PaintBackground(e.CellBounds, true);
            Rectangle bar = Rectangle.Inflate(e.CellBounds, -4, -5);
            bar.Width = (int)Math.Round(Math.Max(0d, Math.Min(1d, ratio)) * bar.Width);
            if (bar.Width > 0)
            {
                using (SolidBrush brush = new SolidBrush(ThemeManager.AccentSoftColor))
                {
                    e.Graphics.FillRectangle(brush, bar);
                }
            }
            e.PaintContent(e.CellBounds);
            e.Handled = true;
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
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = ThemeManager.WindowBackColor
            };
        }

        private void ConfigureSummaryColumns()
        {
            AddTextColumn(_summaryGrid, "Column", Localization.T("DataProfile.Column"), 150, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Type", Localization.T("DataProfile.Type"), 125, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Sample", Localization.T("DataProfile.Scanned"), 90, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Nulls", Localization.T("DataProfile.Nulls"), 80, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Distinct", Localization.T("DataProfile.Distinct"), 90, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Minimum", Localization.T("DataProfile.Minimum"), 105, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Maximum", Localization.T("DataProfile.Maximum"), 105, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Average", Localization.T("DataProfile.Average"), 95, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_summaryGrid, "Top", Localization.T("DataProfile.TopValue"), 160, DataGridViewAutoSizeColumnMode.Fill);
            AddTextColumn(_summaryGrid, "Status", Localization.T("DataProfile.Status"), 90, DataGridViewAutoSizeColumnMode.None);
        }

        private void ConfigureDistributionColumns()
        {
            AddTextColumn(_distributionGrid, "Value", Localization.T("DataProfile.Value"), 260, DataGridViewAutoSizeColumnMode.Fill);
            AddTextColumn(_distributionGrid, "Count", Localization.T("DataProfile.Count"), 120, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn(_distributionGrid, "Ratio", Localization.T("DataProfile.Ratio"), 190, DataGridViewAutoSizeColumnMode.None);
        }

        private static void AddTextColumn(DataGridView grid, string name, string header, int width, DataGridViewAutoSizeColumnMode sizeMode)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                MinimumWidth = 60,
                AutoSizeMode = sizeMode,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private static string FormatLong(long value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string FormatValue(object value)
        {
            if (value == null || value == DBNull.Value) return Localization.T("DataProfile.NullValue");
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                int previewLength = Math.Min(16, bytes.Length);
                string preview = BitConverter.ToString(bytes, 0, previewLength).Replace("-", string.Empty);
                return "0x" + preview + (bytes.Length > previewLength ? "…" : string.Empty) + " (" + FormatLong(bytes.Length) + " B)";
            }

            string text;
            IFormattable formattable = value as IFormattable;
            if (formattable != null) text = formattable.ToString(null, CultureInfo.CurrentCulture);
            else text = value.ToString();
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 160 ? text : text.Substring(0, 157) + "…";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancellationTokenSource cancellation = _analysisCancellation;
            _analysisCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            if (_mainHost != null) _mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }
    }
}

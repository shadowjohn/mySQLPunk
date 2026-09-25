using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// BI 儀表板：資料集是唯讀查詢（MongoDB 為 JSON 查詢）加計算欄位，圖表在本機彙總；
    /// 點選長條／折線點／圓餅扇形會篩選其他有同名欄位的圖表。儀表板存成 .punkbi。
    /// </summary>
    public sealed class BiDashboardForm : Form
    {
        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly ToolStripTextBox titleBox;
        private readonly ToolStripButton saveButton;
        private readonly ToolStripButton refreshButton;
        private readonly ToolStripButton addWidgetButton;
        private readonly ListBox datasetList;
        private readonly FlowLayoutPanel filterBar;
        private readonly Panel canvas;
        private readonly Label emptyLabel;
        private readonly ToolStripStatusLabel statusLabel;
        private readonly List<WidgetCard> cards = new List<WidgetCard>();
        private readonly Dictionary<string, BiDatasetData> data = new Dictionary<string, BiDatasetData>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> datasetErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private BiDashboard dashboard = new BiDashboard();
        private List<BiFilter> filters = new List<BiFilter>();
        private string filePath;
        private bool dirty;
        private bool busy;

        public BiDashboardForm(IDatabase database, string databaseName)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;

            Width = 1240;
            Height = 820;
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            ToolStrip toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
            ToolStripButton newButton = new ToolStripButton(Localization.T("Bi.New"));
            ToolStripButton openButton = new ToolStripButton(Localization.T("Bi.Open"));
            saveButton = new ToolStripButton(Localization.T("Bi.Save"));
            ToolStripButton saveAsButton = new ToolStripButton(Localization.T("Bi.SaveAs"));
            ToolStripButton addDatasetButton = new ToolStripButton(Localization.T("Bi.AddDataset"));
            addWidgetButton = new ToolStripButton(Localization.T("Bi.AddWidget"));
            refreshButton = new ToolStripButton(Localization.T("Bi.Refresh"));
            ToolStripButton exportButton = new ToolStripButton(Localization.T("Bi.ExportPng"));
            ToolStripButton exportHtmlButton = new ToolStripButton(Localization.T("Bi.ExportHtml"));
            titleBox = new ToolStripTextBox { Width = 220, ToolTipText = Localization.T("Bi.TitleHint") };
            toolbar.Items.AddRange(new ToolStripItem[]
            {
                newButton, openButton, saveButton, saveAsButton, new ToolStripSeparator(),
                new ToolStripLabel(Localization.T("Bi.Title")), titleBox, new ToolStripSeparator(),
                addDatasetButton, addWidgetButton, new ToolStripSeparator(), refreshButton, exportButton, exportHtmlButton
            });

            datasetList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            Button editDataset = new Button { Text = Localization.T("Bi.Edit"), AutoSize = true };
            Button removeDataset = new Button { Text = Localization.T("Bi.Remove"), AutoSize = true };
            FlowLayoutPanel datasetButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
            datasetButtons.Controls.AddRange(new Control[] { editDataset, removeDataset });
            Label datasetHeader = new Label { Text = Localization.T("Bi.Datasets"), Dock = DockStyle.Top, Height = 24, Font = UiKit.BodyBold, TextAlign = ContentAlignment.MiddleLeft };
            Panel side = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8) };
            side.Controls.Add(datasetList);
            side.Controls.Add(datasetButtons);
            side.Controls.Add(datasetHeader);

            filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2), Visible = false, WrapContents = true };
            canvas = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
            emptyLabel = new Label
            {
                Text = Localization.T("Bi.EmptyHint"),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = ThemeManager.MutedTextColor
            };
            canvas.Controls.Add(emptyLabel);

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("Bi.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(canvas);
            Controls.Add(filterBar);
            Controls.Add(side);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            newButton.Click += (sender, args) => { if (ConfirmDiscard()) LoadDashboard(new BiDashboard(), null); };
            openButton.Click += (sender, args) => OpenWithDialog();
            saveButton.Click += (sender, args) => Guard(() => SaveTo(filePath ?? AskSavePath()));
            saveAsButton.Click += (sender, args) => Guard(() => SaveTo(AskSavePath()));
            addDatasetButton.Click += (sender, args) => EditDataset(-1);
            editDataset.Click += (sender, args) => EditDataset(datasetList.SelectedIndex);
            datasetList.DoubleClick += (sender, args) => EditDataset(datasetList.SelectedIndex);
            removeDataset.Click += (sender, args) => RemoveDataset(datasetList.SelectedIndex);
            addWidgetButton.Click += (sender, args) => EditWidget(-1);
            refreshButton.Click += async (sender, args) => await RefreshDataAsync();
            exportButton.Click += (sender, args) => ExportPngWithDialog();
            exportHtmlButton.Click += (sender, args) => ExportHtmlWithDialog();
            titleBox.TextChanged += (sender, args) =>
            {
                if (dashboard.Title == titleBox.Text) return;
                dashboard.Title = titleBox.Text;
                MarkDirty();
            };
            canvas.Resize += (sender, args) => LayoutCards();
            KeyDown += async (sender, args) =>
            {
                if (args.KeyCode == Keys.F5)
                {
                    args.Handled = true;
                    await RefreshDataAsync();
                }
                else if (args.Control && args.KeyCode == Keys.S)
                {
                    args.Handled = true;
                    Guard(() => SaveTo(filePath ?? AskSavePath()));
                }
            };
            FormClosing += (sender, args) =>
            {
                if (busy || !ConfirmDiscard()) args.Cancel = true;
            };
            ThemeManager.ApplyTo(this);
            UpdateTitle();
        }

        public BiDashboard Dashboard { get { return dashboard; } }
        public IList<BiFilter> Filters { get { return filters; } }
        public bool IsDirty { get { return dirty; } }

        public BiWidgetResult ResultOf(int widgetIndex)
        {
            return widgetIndex >= 0 && widgetIndex < cards.Count ? cards[widgetIndex].Result : null;
        }

        public BiChartControl ChartOf(int widgetIndex)
        {
            return widgetIndex >= 0 && widgetIndex < cards.Count ? cards[widgetIndex].Chart : null;
        }

        public string DatasetError(string name)
        {
            string error;
            return datasetErrors.TryGetValue(name, out error) ? error : null;
        }

        // ------------------------------------------------------------ Dashboard

        public void LoadDashboard(BiDashboard value, string path)
        {
            BiDashboardService.Validate(value);
            dashboard = value;
            filePath = path;
            filters = new List<BiFilter>();
            data.Clear();
            datasetErrors.Clear();
            titleBox.Text = dashboard.Title ?? string.Empty;
            RebuildDatasetList();
            RebuildCards();
            dirty = false;
            UpdateTitle();
        }

        private void OpenWithDialog()
        {
            if (!ConfirmDiscard()) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = Localization.T("Bi.FileFilter") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Guard(() =>
                {
                    LoadDashboard(BiDashboardService.Load(dialog.FileName), dialog.FileName);
                    RefreshData();
                });
            }
        }

        private string AskSavePath()
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = Localization.T("Bi.FileFilter"),
                DefaultExt = BiDashboardService.FileExtension,
                FileName = MakeFileName(string.IsNullOrWhiteSpace(dashboard.Title) ? databaseName : dashboard.Title) + BiDashboardService.FileExtension
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        public void SaveTo(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            BiDashboardService.Save(path, dashboard);
            filePath = path;
            dirty = false;
            UpdateTitle();
            statusLabel.Text = Localization.Format("Bi.Saved", path);
        }

        private bool ConfirmDiscard()
        {
            if (!dirty) return true;
            DialogResult answer = MessageBox.Show(this, Localization.T("Bi.UnsavedChanges"), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.No) return true;
            string path = filePath ?? AskSavePath();
            if (path == null) return false;
            try
            {
                SaveTo(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
            {
                ShowError(exception);
                return false;
            }
        }

        private void MarkDirty()
        {
            dirty = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = string.IsNullOrWhiteSpace(dashboard.Title) ? Localization.T("Bi.Untitled") : dashboard.Title;
            Text = Localization.Format("Bi.WindowTitle", name + (dirty ? " *" : string.Empty), databaseName);
            addWidgetButton.Enabled = dashboard.Datasets.Count > 0 && !busy;
        }

        private static string MakeFileName(string value)
        {
            string name = new string((value ?? "dashboard").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return name.Length == 0 ? "dashboard" : name;
        }

        // ------------------------------------------------------------ Datasets

        private void RebuildDatasetList()
        {
            int selected = datasetList.SelectedIndex;
            datasetList.Items.Clear();
            foreach (BiDataset dataset in dashboard.Datasets)
            {
                string suffix;
                BiDatasetData loaded;
                if (datasetErrors.ContainsKey(dataset.Name)) suffix = " — " + Localization.T("Bi.DatasetFailed");
                else if (data.TryGetValue(dataset.Name, out loaded)) suffix = " — " + Localization.Format("Bi.DatasetRows", loaded.Rows.Count);
                else suffix = string.Empty;
                datasetList.Items.Add(dataset.Name + suffix);
            }
            if (selected >= 0 && selected < datasetList.Items.Count) datasetList.SelectedIndex = selected;
            else if (datasetList.Items.Count > 0) datasetList.SelectedIndex = 0;
            UpdateTitle();
        }

        private void EditDataset(int index)
        {
            if (busy) return;
            if (index < 0 && dashboard.Datasets.Count >= BiDashboardService.MaximumDatasets)
            {
                MessageBox.Show(this, Localization.Format("Bi.Error.TooManyDatasets", BiDashboardService.MaximumDatasets), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            BiDataset original = index >= 0 && index < dashboard.Datasets.Count ? dashboard.Datasets[index] : null;
            if (index >= 0 && original == null) return;
            HashSet<string> otherNames = new HashSet<string>(dashboard.Datasets.Where(item => item != original).Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
            string template = BiDashboardService.IsJsonQueryProvider(database)
                ? "{\n  \"collection\": \"\",\n  \"filter\": {},\n  \"limit\": 1000\n}"
                : "SELECT ";
            using (BiDatasetEditorForm editor = new BiDatasetEditorForm(original, otherNames, template, PreviewDataset))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                BiDataset updated = editor.Result;
                if (original == null)
                {
                    dashboard.Datasets.Add(updated);
                }
                else
                {
                    if (!string.Equals(original.Name, updated.Name, StringComparison.Ordinal))
                    {
                        foreach (BiWidget widget in dashboard.Widgets.Where(item => string.Equals(item.Dataset, original.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            widget.Dataset = updated.Name;
                        }
                    }
                    data.Remove(original.Name);
                    datasetErrors.Remove(original.Name);
                    dashboard.Datasets[index] = updated;
                }
                if (editor.Preview != null) data[updated.Name] = editor.Preview;
                MarkDirty();
                RebuildDatasetList();
                datasetList.SelectedIndex = original == null ? dashboard.Datasets.Count - 1 : index;
                if (editor.Preview == null) RefreshData(updated.Name);
                RenderAll();
            }
        }

        private void RemoveDataset(int index)
        {
            if (busy || index < 0 || index >= dashboard.Datasets.Count) return;
            BiDataset dataset = dashboard.Datasets[index];
            int used = dashboard.Widgets.Count(widget => string.Equals(widget.Dataset, dataset.Name, StringComparison.OrdinalIgnoreCase));
            string message = used > 0 ? Localization.Format("Bi.RemoveDatasetWithWidgets", dataset.Name, used) : Localization.Format("Bi.RemoveDataset", dataset.Name);
            if (MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            for (int i = dashboard.Widgets.Count - 1; i >= 0; i--)
            {
                if (string.Equals(dashboard.Widgets[i].Dataset, dataset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    dashboard.Widgets.RemoveAt(i);
                    filters = BiDashboardService.RemoveWidget(filters, i);
                }
            }
            dashboard.Datasets.RemoveAt(index);
            data.Remove(dataset.Name);
            datasetErrors.Remove(dataset.Name);
            MarkDirty();
            RebuildDatasetList();
            RebuildCards();
        }

        private BiDatasetData PreviewDataset(BiDataset dataset)
        {
            return BiDashboardService.Prepare(BiDashboardService.Query(database, databaseName, dataset), dataset);
        }

        /// <summary>同步重新載入資料集（name 為 null 代表全部）；測試與開檔使用。</summary>
        public void RefreshData(string name = null)
        {
            foreach (BiDataset dataset in dashboard.Datasets.Where(item => name == null || string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                LoadDatasetResult(dataset, TryLoad(dataset));
            }
            AfterRefresh();
        }

        private async Task RefreshDataAsync()
        {
            if (busy || dashboard.Datasets.Count == 0) return;
            SetBusy(true);
            try
            {
                foreach (BiDataset dataset in dashboard.Datasets.ToList())
                {
                    statusLabel.Text = Localization.Format("Bi.Loading", dataset.Name);
                    BiDataset current = dataset;
                    KeyValuePair<BiDatasetData, string> outcome = await Task.Run(() => TryLoad(current));
                    if (IsDisposed) return;
                    LoadDatasetResult(current, outcome);
                }
            }
            finally
            {
                if (!IsDisposed) SetBusy(false);
            }
            if (!IsDisposed) AfterRefresh();
        }

        private KeyValuePair<BiDatasetData, string> TryLoad(BiDataset dataset)
        {
            try
            {
                return new KeyValuePair<BiDatasetData, string>(PreviewDataset(dataset), null);
            }
            catch (Exception exception)
            {
                return new KeyValuePair<BiDatasetData, string>(null, ExceptionMessageService.GetReason(exception));
            }
        }

        private void LoadDatasetResult(BiDataset dataset, KeyValuePair<BiDatasetData, string> outcome)
        {
            if (outcome.Key != null)
            {
                data[dataset.Name] = outcome.Key;
                datasetErrors.Remove(dataset.Name);
            }
            else
            {
                data.Remove(dataset.Name);
                datasetErrors[dataset.Name] = outcome.Value;
            }
        }

        private void AfterRefresh()
        {
            RebuildDatasetList();
            RenderAll();
            int failed = datasetErrors.Count;
            int truncated = data.Values.Count(item => item.Truncated);
            string status = Localization.Format("Bi.Refreshed", data.Count, data.Values.Sum(item => item.Rows.Count));
            if (failed > 0) status += " " + Localization.Format("Bi.RefreshFailures", failed, string.Join("; ", datasetErrors.Select(pair => pair.Key + ": " + pair.Value)));
            if (truncated > 0) status += " " + Localization.Format("Bi.Truncated", BiDashboardService.MaximumRows);
            statusLabel.Text = status;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refreshButton.Enabled = !value;
            UseWaitCursor = value;
            UpdateTitle();
        }

        // ------------------------------------------------------------ Widgets

        private void EditWidget(int index)
        {
            if (busy || dashboard.Datasets.Count == 0) return;
            if (index < 0 && dashboard.Widgets.Count >= BiDashboardService.MaximumWidgets)
            {
                MessageBox.Show(this, Localization.Format("Bi.Error.TooManyWidgets", BiDashboardService.MaximumWidgets), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            BiWidget original = index >= 0 && index < dashboard.Widgets.Count ? dashboard.Widgets[index] : null;
            string defaultDataset = datasetList.SelectedIndex >= 0 && datasetList.SelectedIndex < dashboard.Datasets.Count
                ? dashboard.Datasets[datasetList.SelectedIndex].Name
                : dashboard.Datasets[0].Name;
            using (BiWidgetEditorForm editor = new BiWidgetEditorForm(original, dashboard.Datasets.Select(item => item.Name).ToList(), defaultDataset, ColumnsOf))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                if (original == null)
                {
                    dashboard.Widgets.Add(editor.Result);
                }
                else
                {
                    dashboard.Widgets[index] = editor.Result;
                    filters = filters.Where(filter => filter.SourceWidget != index).ToList();
                }
                MarkDirty();
                RebuildCards();
            }
        }

        private List<string> ColumnsOf(string datasetName)
        {
            BiDatasetData loaded;
            if (!data.TryGetValue(datasetName ?? string.Empty, out loaded))
            {
                BiDataset dataset = dashboard.Datasets.FirstOrDefault(item => string.Equals(item.Name, datasetName, StringComparison.OrdinalIgnoreCase));
                if (dataset != null && !datasetErrors.ContainsKey(dataset.Name)) RefreshData(dataset.Name);
                data.TryGetValue(datasetName ?? string.Empty, out loaded);
            }
            return loaded == null ? new List<string>() : loaded.Columns.ToList();
        }

        private void RemoveWidget(int index)
        {
            if (busy || index < 0 || index >= dashboard.Widgets.Count) return;
            if (MessageBox.Show(this, Localization.Format("Bi.RemoveWidget", WidgetTitle(dashboard.Widgets[index])), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            dashboard.Widgets.RemoveAt(index);
            filters = BiDashboardService.RemoveWidget(filters, index);
            MarkDirty();
            RebuildCards();
        }

        private void MoveWidget(int index, int offset)
        {
            int target = index + offset;
            if (busy || index < 0 || target < 0 || target >= dashboard.Widgets.Count) return;
            BiWidget widget = dashboard.Widgets[index];
            dashboard.Widgets[index] = dashboard.Widgets[target];
            dashboard.Widgets[target] = widget;
            filters = filters.Select(filter => filter.SourceWidget == index || filter.SourceWidget == target
                ? new BiFilter(filter.SourceWidget == index ? target : index, filter.Field, filter.Grain, filter.Key, filter.Label)
                : filter).ToList();
            MarkDirty();
            RebuildCards();
        }

        private static string WidgetTitle(BiWidget widget)
        {
            if (!string.IsNullOrWhiteSpace(widget.Title)) return widget.Title;
            return BiDashboardService.AggregateCaption(widget) + (widget.Category == null ? string.Empty : " / " + widget.Category);
        }

        /// <summary>點選分類，更新跨圖表篩選；也供測試直接呼叫。</summary>
        public void ClickCategory(int widgetIndex, string key)
        {
            if (widgetIndex < 0 || widgetIndex >= dashboard.Widgets.Count) return;
            filters = BiDashboardService.Toggle(filters, widgetIndex, dashboard.Widgets[widgetIndex], key);
            RenderAll();
        }

        public void ClearFilters()
        {
            filters = new List<BiFilter>();
            RenderAll();
        }

        private void RebuildCards()
        {
            canvas.SuspendLayout();
            foreach (WidgetCard card in cards)
            {
                canvas.Controls.Remove(card.Panel);
                card.Panel.Dispose();
            }
            cards.Clear();
            for (int i = 0; i < dashboard.Widgets.Count; i++) cards.Add(CreateCard(i));
            emptyLabel.Visible = cards.Count == 0;
            emptyLabel.Text = Localization.T(dashboard.Datasets.Count == 0 ? "Bi.EmptyHint" : "Bi.NoWidgetsHint");
            canvas.ResumeLayout();
            LayoutCards();
            RenderAll();
        }

        private WidgetCard CreateCard(int index)
        {
            BiWidget widget = dashboard.Widgets[index];
            WidgetCard card = new WidgetCard();
            card.Panel = new Panel { BackColor = ThemeManager.ElevatedColor, Padding = new Padding(1) };
            card.Panel.Paint += (sender, args) =>
            {
                Panel panel = (Panel)sender;
                using (Pen border = new Pen(ThemeManager.BorderStrongColor)) args.Graphics.DrawRectangle(border, 0, 0, panel.Width - 1, panel.Height - 1);
            };
            Panel header = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 0, 4, 0) };
            card.Title = new Label { Dock = DockStyle.Fill, Font = UiKit.BodyBold, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = WidgetTitle(widget) };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            actions.Controls.Add(CardLink(Localization.T("Bi.MoveLeft"), () => MoveWidget(index, -1)));
            actions.Controls.Add(CardLink(Localization.T("Bi.MoveRight"), () => MoveWidget(index, 1)));
            actions.Controls.Add(CardLink(Localization.T("Bi.Edit"), () => EditWidget(index)));
            actions.Controls.Add(CardLink(Localization.T("Bi.Remove"), () => RemoveWidget(index)));
            header.Controls.Add(card.Title);
            header.Controls.Add(actions);
            card.Footer = new Label { Dock = DockStyle.Bottom, Height = 20, Padding = new Padding(8, 0, 8, 0), ForeColor = ThemeManager.MutedTextColor, Font = UiKit.Caption, AutoEllipsis = true };

            Control body;
            if (widget.Kind == BiChartKind.Table)
            {
                card.Grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    RowHeadersVisible = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    MultiSelect = false,
                    BorderStyle = BorderStyle.None,
                    BackgroundColor = ThemeManager.ElevatedColor
                };
                card.Grid.CellDoubleClick += (sender, args) =>
                {
                    if (args.RowIndex < 0 || card.Result == null || widget.Category == null || args.RowIndex >= card.Result.Points.Count) return;
                    ClickCategory(index, card.Result.Points[args.RowIndex].Key);
                };
                body = card.Grid;
            }
            else
            {
                card.Chart = new BiChartControl { Dock = DockStyle.Fill };
                card.Chart.CategoryClicked += key => ClickCategory(index, key);
                body = card.Chart;
            }
            card.Panel.Controls.Add(body);
            card.Panel.Controls.Add(card.Footer);
            card.Panel.Controls.Add(header);
            canvas.Controls.Add(card.Panel);
            ThemeManager.ApplyTo(card.Panel);
            card.Panel.BackColor = ThemeManager.ElevatedColor;
            return card;
        }

        private static LinkLabel CardLink(string text, Action action)
        {
            LinkLabel link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(6, 0, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline };
            link.LinkClicked += (sender, args) => action();
            return link;
        }

        private void LayoutCards()
        {
            if (cards.Count == 0) return;
            const int gap = 10;
            // 預留垂直捲軸的寬度，捲軸出現時卡片才不會被擠出水平捲軸。
            int width = canvas.Width - canvas.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2;
            int columns = width >= 900 ? 3 : width >= 560 ? 2 : 1;
            int cellWidth = Math.Max(200, (width - gap * (columns - 1)) / columns);
            int cellHeight = 280;
            int column = 0;
            int row = 0;
            Point scroll = canvas.AutoScrollPosition;
            for (int i = 0; i < cards.Count; i++)
            {
                int span = Math.Min(columns, dashboard.Widgets[i].Span);
                if (column + span > columns)
                {
                    column = 0;
                    row++;
                }
                cards[i].Panel.SetBounds(
                    canvas.Padding.Left + column * (cellWidth + gap) + scroll.X,
                    canvas.Padding.Top + row * (cellHeight + gap) + scroll.Y,
                    cellWidth * span + gap * (span - 1),
                    cellHeight);
                column += span;
                if (column >= columns)
                {
                    column = 0;
                    row++;
                }
            }
        }

        private void RenderAll()
        {
            for (int i = 0; i < cards.Count && i < dashboard.Widgets.Count; i++) RenderCard(i);
            RenderFilterBar();
        }

        private void RenderCard(int index)
        {
            BiWidget widget = dashboard.Widgets[index];
            WidgetCard card = cards[index];
            BiDatasetData loaded;
            string datasetError;
            BiWidgetResult result;
            if (data.TryGetValue(widget.Dataset, out loaded))
            {
                result = BiDashboardService.Compute(widget, index, loaded, filters);
            }
            else
            {
                result = new BiWidgetResult
                {
                    Error = datasetErrors.TryGetValue(widget.Dataset, out datasetError)
                        ? Localization.Format("Bi.DatasetError", widget.Dataset, datasetError)
                        : Localization.T("Bi.NotLoaded")
                };
            }
            card.Result = result;
            BiFilter own = filters.FirstOrDefault(filter => filter.SourceWidget == index);
            if (card.Chart != null)
            {
                card.Chart.SetData(widget.Kind, result, own == null ? null : own.Key, BiDashboardService.AggregateCaption(widget));
            }
            if (card.Grid != null)
            {
                card.Grid.DataSource = null;
                if (!string.IsNullOrEmpty(result.Error))
                {
                    DataTable error = new DataTable();
                    error.Columns.Add(Localization.T("Bi.ErrorColumn"));
                    error.Rows.Add(result.Error);
                    card.Grid.DataSource = error;
                }
                else
                {
                    card.Grid.DataSource = result.Table;
                    foreach (DataGridViewColumn column in card.Grid.Columns)
                    {
                        if (column.ValueType == typeof(decimal))
                        {
                            column.DefaultCellStyle.Format = "#,0.####";
                            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                        }
                    }
                }
            }
            int applied = loaded == null ? 0 : filters.Count(filter => BiDashboardService.FilterApplies(filter, index, widget, loaded));
            string footer = loaded == null ? string.Empty : Localization.Format("Bi.CardFooter", widget.Dataset, result.MatchedRows);
            if (result.GroupCount > result.Points.Count) footer += " · " + Localization.Format("Bi.CardTop", result.Points.Count, result.GroupCount);
            if (applied > 0) footer += " · " + Localization.Format("Bi.CardFiltered", applied);
            if (!widget.CrossFilter) footer += " · " + Localization.T("Bi.CardNoCrossFilter");
            card.Footer.Text = footer;
        }

        private void RenderFilterBar()
        {
            filterBar.SuspendLayout();
            foreach (Control control in filterBar.Controls.Cast<Control>().ToList())
            {
                filterBar.Controls.Remove(control);
                control.Dispose();
            }
            if (filters.Count > 0)
            {
                filterBar.Controls.Add(new Label { Text = Localization.T("Bi.ActiveFilters"), AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
                foreach (BiFilter filter in filters.ToList())
                {
                    BiFilter current = filter;
                    Button chip = new Button { Text = current.Field + " = " + current.Label + "  " + Localization.T("Bi.RemoveFilterSuffix"), AutoSize = true };
                    chip.Click += (sender, args) =>
                    {
                        filters = filters.Where(item => item != current).ToList();
                        RenderAll();
                    };
                    filterBar.Controls.Add(chip);
                }
                LinkLabel clear = new LinkLabel { Text = Localization.T("Bi.ClearFilters"), AutoSize = true, Padding = new Padding(4, 6, 0, 0) };
                clear.LinkClicked += (sender, args) => ClearFilters();
                filterBar.Controls.Add(clear);
                ThemeManager.ApplyTo(filterBar);
            }
            filterBar.Visible = filters.Count > 0;
            filterBar.ResumeLayout();
        }

        // ------------------------------------------------------------ Export

        public Bitmap RenderSnapshot()
        {
            int width = Math.Max(1, cards.Count == 0 ? canvas.Width : cards.Max(card => card.Panel.Right - canvas.AutoScrollPosition.X) + canvas.Padding.Right);
            int height = Math.Max(1, cards.Count == 0 ? canvas.Height : cards.Max(card => card.Panel.Bottom - canvas.AutoScrollPosition.Y) + canvas.Padding.Bottom);
            int headerHeight = 44;
            Bitmap bitmap = new Bitmap(width, height + headerHeight);
            // 逐張卡片自己畫外框、標題與頁尾，主體再用圖表／表格的 DrawToBitmap，
            // 不依賴容器對子控制項的 DrawToBitmap（部分平台不會畫出子控制項）。
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Pen border = new Pen(ThemeManager.BorderStrongColor))
            {
                g.Clear(ThemeManager.WindowBackColor);
                string title = string.IsNullOrWhiteSpace(dashboard.Title) ? Localization.T("Bi.Untitled") : dashboard.Title;
                if (filters.Count > 0) title += "  (" + string.Join(", ", filters.Select(filter => filter.Field + " = " + filter.Label)) + ")";
                UiKit.DrawText(g, title, UiKit.Subtitle, new Rectangle(12, 8, width - 24, headerHeight - 12), ThemeManager.TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                foreach (WidgetCard card in cards)
                {
                    Rectangle frame = new Rectangle(card.Panel.Left - canvas.AutoScrollPosition.X, card.Panel.Top - canvas.AutoScrollPosition.Y + headerHeight, card.Panel.Width, card.Panel.Height);
                    using (SolidBrush background = new SolidBrush(ThemeManager.ElevatedColor)) g.FillRectangle(background, frame);
                    g.DrawRectangle(border, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);
                    UiKit.DrawText(g, card.Title.Text, UiKit.BodyBold, new Rectangle(frame.X + 8, frame.Y + 1, frame.Width - 16, 30), ThemeManager.TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    UiKit.DrawText(g, card.Footer.Text, UiKit.Caption, new Rectangle(frame.X + 8, frame.Bottom - 21, frame.Width - 16, 20), ThemeManager.MutedTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    Control body = (Control)card.Chart ?? card.Grid;
                    if (body == null || body.Width <= 0 || body.Height <= 0) continue;
                    using (Bitmap part = new Bitmap(body.Width, body.Height))
                    {
                        body.DrawToBitmap(part, new Rectangle(Point.Empty, part.Size));
                        g.DrawImage(part, frame.X + body.Left, frame.Y + body.Top);
                    }
                }
            }
            return bitmap;
        }

        private void ExportPngWithDialog()
        {
            if (cards.Count == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                DefaultExt = ".png",
                FileName = MakeFileName(string.IsNullOrWhiteSpace(dashboard.Title) ? databaseName : dashboard.Title) + ".png"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Guard(() =>
                {
                    using (Bitmap bitmap = RenderSnapshot()) bitmap.Save(dialog.FileName, ImageFormat.Png);
                    statusLabel.Text = Localization.Format("Bi.Exported", dialog.FileName);
                });
            }
        }

        /// <summary>目前資料與篩選輸出成 HTML 報表（內嵌 SVG 圖表）；也供測試直接呼叫。</summary>
        public string BuildHtml()
        {
            return BiReportService.BuildHtml(dashboard, data, datasetErrors, filters, databaseName, DateTime.Now);
        }

        private void ExportHtmlWithDialog()
        {
            if (cards.Count == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "HTML (*.html)|*.html",
                DefaultExt = ".html",
                FileName = MakeFileName(string.IsNullOrWhiteSpace(dashboard.Title) ? databaseName : dashboard.Title) + ".html"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Guard(() =>
                {
                    File.WriteAllText(dialog.FileName, BuildHtml(), new System.Text.UTF8Encoding(true));
                    statusLabel.Text = Localization.Format("Bi.Exported", dialog.FileName);
                });
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException || exception is FormatException || exception is System.Runtime.InteropServices.ExternalException)
            {
                ShowError(exception);
            }
        }

        private void ShowError(Exception exception)
        {
            statusLabel.Text = ExceptionMessageService.GetReason(exception);
            MessageBox.Show(this, ExceptionMessageService.GetReason(exception), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private sealed class WidgetCard
        {
            public Panel Panel;
            public Label Title;
            public Label Footer;
            public BiChartControl Chart;
            public DataGridView Grid;
            public BiWidgetResult Result;
        }
    }

    /// <summary>資料集編輯：名稱、唯讀查詢、計算欄位，並可預覽結果。</summary>
    public sealed class BiDatasetEditorForm : Form
    {
        private readonly HashSet<string> otherNames;
        private readonly Func<BiDataset, BiDatasetData> preview;
        private readonly TextBox nameBox;
        private readonly TextBox queryBox;
        private readonly DataGridView fieldsGrid;
        private readonly DataGridView previewGrid;
        private readonly Label previewLabel;

        public BiDatasetEditorForm(BiDataset dataset, HashSet<string> otherNames, string queryTemplate, Func<BiDataset, BiDatasetData> preview)
        {
            this.otherNames = otherNames;
            this.preview = preview;
            Text = Localization.T(dataset == null ? "Bi.DatasetEditor.AddTitle" : "Bi.DatasetEditor.EditTitle");
            Width = 900;
            Height = 720;
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterParent;

            nameBox = new TextBox { Dock = DockStyle.Fill, Text = dataset == null ? string.Empty : dataset.Name };
            queryBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                AcceptsTab = true,
                Font = UiKit.GetMonoFont(10f),
                Text = (dataset == null ? queryTemplate : dataset.Query ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n")
            };
            fieldsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                RowHeadersVisible = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = Localization.T("Bi.DatasetEditor.FieldName"), FillWeight = 25 });
            fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Expression", HeaderText = Localization.T("Bi.DatasetEditor.FieldExpression"), FillWeight = 75 });
            if (dataset != null)
            {
                foreach (BiCalculatedField field in dataset.CalculatedFields) fieldsGrid.Rows.Add(field.Name, field.Expression);
            }
            previewGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
            };
            previewLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = Localization.T("Bi.DatasetEditor.PreviewHint") };
            Button previewButton = new Button { Text = Localization.T("Bi.DatasetEditor.Preview"), AutoSize = true };
            Label helpLabel = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = ThemeManager.MutedTextColor, Text = Localization.T("Bi.DatasetEditor.ExpressionHelp") };

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            layout.Controls.Add(new Label { Text = Localization.T("Bi.DatasetEditor.Name"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(nameBox, 1, 0);
            layout.Controls.Add(new Label { Text = Localization.T(BiQueryLabelKey(queryTemplate)), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 1);
            layout.Controls.Add(queryBox, 1, 1);
            layout.Controls.Add(new Label { Text = Localization.T("Bi.DatasetEditor.Fields"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 2);
            layout.Controls.Add(fieldsGrid, 1, 2);
            layout.Controls.Add(helpLabel, 1, 3);
            FlowLayoutPanel previewRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            previewRow.Controls.Add(previewButton);
            layout.Controls.Add(previewRow, 0, 4);
            layout.Controls.Add(previewLabel, 1, 4);
            layout.Controls.Add(previewGrid, 0, 5);
            layout.SetColumnSpan(previewGrid, 2);

            Button okButton = new Button { Text = Localization.T("Common.OK"), AutoSize = true };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            ThemeManager.MarkAsPrimary(okButton);
            Controls.Add(layout);
            Controls.Add(buttons);
            CancelButton = cancelButton;

            previewButton.Click += (sender, args) => RunPreview();
            okButton.Click += (sender, args) =>
            {
                try
                {
                    Result = Build();
                    DialogResult = DialogResult.OK;
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            ThemeManager.ApplyTo(this);
        }

        private static string BiQueryLabelKey(string template)
        {
            return template != null && template.TrimStart().StartsWith("{", StringComparison.Ordinal) ? "Bi.DatasetEditor.JsonQuery" : "Bi.DatasetEditor.Query";
        }

        public BiDataset Result { get; private set; }

        /// <summary>最後一次成功預覽的資料（查詢與欄位都沒變時沿用，免得重跑）。</summary>
        public BiDatasetData Preview { get; private set; }

        private string previewSignature;

        private BiDataset Build()
        {
            fieldsGrid.EndEdit();
            BiDataset dataset = new BiDataset { Name = nameBox.Text, Query = queryBox.Text };
            foreach (DataGridViewRow row in fieldsGrid.Rows)
            {
                if (row.IsNewRow) continue;
                string name = Convert.ToString(row.Cells[0].Value);
                string expression = Convert.ToString(row.Cells[1].Value);
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(expression)) continue;
                dataset.CalculatedFields.Add(new BiCalculatedField { Name = name, Expression = expression });
            }
            BiDashboardService.ValidateDataset(dataset, new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase));
            if (Signature(dataset) != previewSignature) Preview = null;
            return dataset;
        }

        private static string Signature(BiDataset dataset)
        {
            return dataset.Query + "\u0001" + string.Join("\u0002", dataset.CalculatedFields.Select(field => field.Name + "\u0003" + field.Expression));
        }

        private void RunPreview()
        {
            try
            {
                BiDataset dataset = Build();
                UseWaitCursor = true;
                Application.DoEvents();
                BiDatasetData loaded = preview(dataset);
                Preview = loaded;
                previewSignature = Signature(dataset);
                DataTable table = new DataTable();
                foreach (string column in loaded.Columns)
                {
                    string name = column;
                    int suffix = 2;
                    while (table.Columns.Contains(name)) name = column + "_" + suffix++;
                    table.Columns.Add(name, typeof(string));
                }
                foreach (object[] row in loaded.Rows.Take(200))
                {
                    table.Rows.Add(row.Select(value => value == null ? (object)DBNull.Value : BiExpression.ToText(value)).ToArray());
                }
                previewGrid.DataSource = table;
                previewLabel.ForeColor = ThemeManager.TextColor;
                previewLabel.Text = Localization.Format("Bi.DatasetEditor.PreviewResult", loaded.Rows.Count, loaded.Columns.Count) +
                    (loaded.Truncated ? " " + Localization.Format("Bi.Truncated", BiDashboardService.MaximumRows) : string.Empty);
            }
            catch (Exception exception)
            {
                Preview = null;
                previewGrid.DataSource = null;
                previewLabel.ForeColor = ThemeManager.DangerColor;
                previewLabel.Text = ExceptionMessageService.GetReason(exception);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }
    }

    /// <summary>圖表設定：類型、資料集、分類（可依年／月／日分組）、彙總欄位與方式、篩選運算式。</summary>
    public sealed class BiWidgetEditorForm : Form
    {
        private readonly Func<string, List<string>> columnsOf;
        private readonly TextBox titleBox;
        private readonly ComboBox datasetBox;
        private readonly ComboBox kindBox;
        private readonly ComboBox categoryBox;
        private readonly ComboBox grainBox;
        private readonly ComboBox aggregateBox;
        private readonly ComboBox valueBox;
        private readonly TextBox filterBox;
        private readonly NumericUpDown topBox;
        private readonly CheckBox sortByValueBox;
        private readonly CheckBox crossFilterBox;
        private readonly CheckBox wideBox;
        private readonly List<string> datasetNames;

        public BiWidgetEditorForm(BiWidget widget, List<string> datasetNames, string defaultDataset, Func<string, List<string>> columnsOf)
        {
            this.columnsOf = columnsOf;
            this.datasetNames = datasetNames;
            Text = Localization.T(widget == null ? "Bi.WidgetEditor.AddTitle" : "Bi.WidgetEditor.EditTitle");
            Width = 560;
            Height = 540;
            MinimumSize = new Size(480, 500);
            StartPosition = FormStartPosition.CenterParent;

            titleBox = new TextBox { Dock = DockStyle.Fill };
            datasetBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            datasetBox.Items.AddRange(datasetNames.Cast<object>().ToArray());
            kindBox = EnumBox(typeof(BiChartKind), "Bi.Kind.");
            categoryBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            grainBox = EnumBox(typeof(BiDateGrain), "Bi.Grain.");
            aggregateBox = EnumBox(typeof(BiAggregate), "Bi.Aggregate.");
            valueBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            filterBox = new TextBox { Dock = DockStyle.Fill, Font = UiKit.GetMonoFont(9.5f) };
            topBox = new NumericUpDown { Minimum = 1, Maximum = BiDashboardService.MaximumTopN, Value = 12, Width = 80 };
            sortByValueBox = new CheckBox { Text = Localization.T("Bi.WidgetEditor.SortByValue"), AutoSize = true, Checked = true };
            crossFilterBox = new CheckBox { Text = Localization.T("Bi.WidgetEditor.CrossFilter"), AutoSize = true, Checked = true };
            wideBox = new CheckBox { Text = Localization.T("Bi.WidgetEditor.Wide"), AutoSize = true };

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            AddRow(layout, ref row, "Bi.WidgetEditor.Title", titleBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Dataset", datasetBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Kind", kindBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Category", categoryBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Grain", grainBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Aggregate", aggregateBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Value", valueBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Filter", filterBox);
            AddRow(layout, ref row, "Bi.WidgetEditor.Top", topBox);
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            options.Controls.AddRange(new Control[] { sortByValueBox, crossFilterBox, wideBox });
            AddRow(layout, ref row, "Bi.WidgetEditor.Options", options);

            Button okButton = new Button { Text = Localization.T("Common.OK"), AutoSize = true };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            ThemeManager.MarkAsPrimary(okButton);
            Controls.Add(layout);
            Controls.Add(buttons);
            CancelButton = cancelButton;

            datasetBox.SelectedIndexChanged += (sender, args) => LoadColumns();
            kindBox.SelectedIndexChanged += (sender, args) => UpdateEnabled();
            okButton.Click += (sender, args) =>
            {
                try
                {
                    Result = Build();
                    DialogResult = DialogResult.OK;
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            BiWidget source = widget ?? new BiWidget { Dataset = defaultDataset, Kind = BiChartKind.Bar, Aggregate = BiAggregate.Count };
            titleBox.Text = source.Title ?? string.Empty;
            int datasetIndex = datasetNames.FindIndex(name => string.Equals(name, source.Dataset, StringComparison.OrdinalIgnoreCase));
            datasetBox.SelectedIndex = datasetIndex >= 0 ? datasetIndex : 0;
            kindBox.SelectedIndex = (int)source.Kind;
            grainBox.SelectedIndex = (int)source.DateGrain;
            aggregateBox.SelectedIndex = (int)source.Aggregate;
            categoryBox.Text = source.Category ?? string.Empty;
            valueBox.Text = source.Value ?? string.Empty;
            filterBox.Text = source.Filter ?? string.Empty;
            topBox.Value = Math.Max(1, Math.Min(BiDashboardService.MaximumTopN, source.TopN <= 0 ? 12 : source.TopN));
            sortByValueBox.Checked = source.SortByValue;
            crossFilterBox.Checked = source.CrossFilter;
            wideBox.Checked = source.Span >= 2;
            UpdateEnabled();
            ThemeManager.ApplyTo(this);
        }

        public BiWidget Result { get; private set; }

        private static ComboBox EnumBox(Type type, string prefix)
        {
            ComboBox box = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (string name in Enum.GetNames(type)) box.Items.Add(Localization.T(prefix + name));
            return box;
        }

        private static void AddRow(TableLayoutPanel layout, ref int row, string labelKey, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = Localization.T(labelKey), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 6) }, 0, row);
            control.Margin = new Padding(3, 4, 3, 4);
            layout.Controls.Add(control, 1, row);
            row++;
        }

        private void LoadColumns()
        {
            string category = categoryBox.Text;
            string value = valueBox.Text;
            List<string> columns = datasetBox.SelectedIndex >= 0 ? columnsOf(datasetNames[datasetBox.SelectedIndex]) : new List<string>();
            categoryBox.Items.Clear();
            valueBox.Items.Clear();
            categoryBox.Items.AddRange(columns.Cast<object>().ToArray());
            valueBox.Items.AddRange(columns.Cast<object>().ToArray());
            categoryBox.Text = category;
            valueBox.Text = value;
        }

        private void UpdateEnabled()
        {
            BiChartKind kind = (BiChartKind)Math.Max(0, kindBox.SelectedIndex);
            bool categorical = kind != BiChartKind.Number;
            categoryBox.Enabled = categorical;
            grainBox.Enabled = categorical;
            topBox.Enabled = categorical;
            sortByValueBox.Enabled = categorical;
        }

        private BiWidget Build()
        {
            BiChartKind kind = (BiChartKind)kindBox.SelectedIndex;
            BiWidget widget = new BiWidget
            {
                Title = titleBox.Text,
                Dataset = datasetBox.SelectedIndex >= 0 ? datasetNames[datasetBox.SelectedIndex] : string.Empty,
                Kind = kind,
                Category = kind == BiChartKind.Number ? null : categoryBox.Text,
                DateGrain = kind == BiChartKind.Number ? BiDateGrain.None : (BiDateGrain)grainBox.SelectedIndex,
                Aggregate = (BiAggregate)aggregateBox.SelectedIndex,
                Value = valueBox.Text,
                Filter = filterBox.Text,
                TopN = (int)topBox.Value,
                SortByValue = sortByValueBox.Checked,
                CrossFilter = crossFilterBox.Checked,
                Span = wideBox.Checked ? 2 : 1
            };
            BiDashboardService.ValidateWidget(widget, datasetNames);
            List<string> columns = columnsOf(widget.Dataset);
            if (columns.Count > 0)
            {
                foreach (string field in new[] { widget.Category, widget.Value }.Where(item => item != null))
                {
                    if (!columns.Contains(field, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Localization.Format("Bi.Error.MissingColumn", field));
                }
                if (widget.Filter != null)
                {
                    foreach (string field in BiExpression.Parse(widget.Filter).Fields)
                    {
                        if (!columns.Contains(field, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException(Localization.Format("Bi.Error.MissingColumn", field));
                    }
                }
            }
            return widget;
        }
    }
}

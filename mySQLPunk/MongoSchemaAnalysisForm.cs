using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MongoDB.Bson;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// MongoDB collection 的結構描述分析：抽樣（前 N 筆或伺服器端隨機）後列出巢狀路徑的出現率、型別分佈、範圍、
    /// 常見值與異常；選取路徑可看極端值樣本。只讀取文件。
    /// </summary>
    public sealed class MongoSchemaAnalysisForm : Form
    {
        private static readonly int[] SampleSizes = { 100, 1000, 10000, 100000 };

        private readonly my_mongodb database;
        private readonly string databaseName;
        private readonly string collectionName;
        private readonly ComboBox sampleSize;
        private readonly CheckBox randomSample;
        private readonly CheckBox anomaliesOnly;
        private readonly Button analyzeButton;
        private readonly Button copyButton;
        private readonly DataGridView grid;
        private readonly TextBox details;
        private readonly ToolStripStatusLabel statusLabel;
        private MongoSchemaReport report;
        private bool busy;

        public MongoSchemaAnalysisForm(my_mongodb database, string databaseName, string collectionName)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;
            this.collectionName = collectionName;

            Text = Localization.Format("MongoSchema.WindowTitle", databaseName, collectionName);
            Width = 1180;
            Height = 720;
            MinimumSize = new Size(820, 480);
            StartPosition = FormStartPosition.CenterParent;

            sampleSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            sampleSize.Items.AddRange(SampleSizes.Select(size => (object)size.ToString("N0", CultureInfo.InvariantCulture)).ToArray());
            sampleSize.SelectedIndex = 1;
            randomSample = new CheckBox { Text = Localization.T("MongoSchema.Random"), AutoSize = true, Checked = true, Padding = new Padding(8, 4, 0, 0) };
            anomaliesOnly = new CheckBox { Text = Localization.T("MongoSchema.AnomaliesOnly"), AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            analyzeButton = new Button { Text = Localization.T("MongoSchema.Analyze"), AutoSize = true };
            copyButton = new Button { Text = Localization.T("MongoSchema.CopyReport"), AutoSize = true, Enabled = false };
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 8, 0) };
            toolbar.Controls.AddRange(new Control[]
            {
                new Label { Text = Localization.T("MongoSchema.SampleSize"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
                sampleSize, randomSample, analyzeButton, anomaliesOnly, copyButton
            });

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = Localization.T("MongoSchema.Column.Path"), FillWeight = 18 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Presence", HeaderText = Localization.T("MongoSchema.Column.Presence"), FillWeight = 8 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Types", HeaderText = Localization.T("MongoSchema.Column.Types"), FillWeight = 16 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nulls", HeaderText = Localization.T("MongoSchema.Column.Nulls"), FillWeight = 6 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Range", HeaderText = Localization.T("MongoSchema.Column.Range"), FillWeight = 18 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Top", HeaderText = Localization.T("MongoSchema.Column.Top"), FillWeight = 16 });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Anomalies",
                HeaderText = Localization.T("MongoSchema.Column.Anomalies"),
                FillWeight = 18,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, ForeColor = Color.FromArgb(181, 71, 8) }
            });

            details = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 150,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("MongoSchema.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(grid);
            Controls.Add(details);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            analyzeButton.Click += (sender, args) => RunGuarded(() => Analyze(SampleSizes[sampleSize.SelectedIndex], randomSample.Checked));
            anomaliesOnly.CheckedChanged += (sender, args) => Render();
            copyButton.Click += (sender, args) =>
            {
                if (report != null) Clipboard.SetText(MongoSchemaAnalyzer.BuildTextReport(report, Text));
                statusLabel.Text = Localization.T("MongoSchema.Copied");
            };
            grid.SelectionChanged += (sender, args) => ShowDetails();
            FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
            ThemeManager.ApplyTo(this);
        }

        public MongoSchemaReport Report { get { return report; } }

        /// <summary>抽樣並分析；也供測試直接呼叫。</summary>
        public MongoSchemaReport Analyze(int size, bool random)
        {
            statusLabel.Text = Localization.T("MongoSchema.Sampling");
            Application.DoEvents();
            List<BsonDocument> documents = database.SampleDocuments(databaseName, collectionName, size, random);
            report = MongoSchemaAnalyzer.Analyze(documents);
            Render();
            statusLabel.Text = Localization.Format("MongoSchema.Analyzed", report.DocumentCount, report.Fields.Count,
                report.Fields.Count(field => field.Anomalies.Count > 0)) +
                (report.Warnings.Count > 0 ? " " + string.Join(" ", report.Warnings) : string.Empty);
            copyButton.Enabled = true;
            return report;
        }

        private void Render()
        {
            grid.Rows.Clear();
            if (report == null) return;
            foreach (MongoFieldStats field in report.Fields.Where(item => !anomaliesOnly.Checked || item.Anomalies.Count > 0))
            {
                int index = grid.Rows.Add(
                    new string(' ', Math.Max(0, field.Depth - 1) * 2) + field.Path,
                    report.Presence(field).ToString("0.#", CultureInfo.InvariantCulture) + "%",
                    field.TypeSummary,
                    field.NullCount.ToString("N0", CultureInfo.InvariantCulture),
                    MongoSchemaAnalyzer.RangeText(field),
                    field.TopValues.Count == 0 ? string.Empty : MongoSchemaAnalyzer.TopText(field),
                    string.Join(Environment.NewLine, field.Anomalies));
                grid.Rows[index].Tag = field;
            }
        }

        private void ShowDetails()
        {
            MongoFieldStats field = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as MongoFieldStats;
            if (field == null || report == null)
            {
                details.Text = string.Empty;
                return;
            }

            List<string> lines = new List<string>
            {
                field.Path,
                Localization.Format("MongoSchema.Report.Presence", report.Presence(field).ToString("0.#", CultureInfo.InvariantCulture), field.DocumentCount, field.Occurrences),
                Localization.Format("MongoSchema.Report.Types", string.Join(", ", field.TypeCounts.OrderByDescending(item => item.Value).Select(item => item.Key + " × " + item.Value.ToString("N0", CultureInfo.InvariantCulture))))
            };
            string range = MongoSchemaAnalyzer.RangeText(field);
            if (range.Length > 0) lines.Add(range);
            if (field.LowerFence.HasValue)
            {
                lines.Add(Localization.Format("MongoSchema.Report.Fences", field.LowerFence.Value.ToString("0.####", CultureInfo.InvariantCulture),
                    field.UpperFence.Value.ToString("0.####", CultureInfo.InvariantCulture), field.OutlierCount));
            }
            if (field.OutlierSamples.Count > 0) lines.Add(Localization.Format("MongoSchema.Report.OutlierSamples", string.Join(", ", field.OutlierSamples)));
            if (field.TopValues.Count > 0) lines.Add(Localization.Format("MongoSchema.Report.Top", MongoSchemaAnalyzer.TopText(field)));
            lines.AddRange(field.Anomalies.Select(item => "! " + item));
            details.Text = string.Join(Environment.NewLine, lines);
        }

        private void RunGuarded(Action action)
        {
            if (busy) return;
            busy = true;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            analyzeButton.Enabled = false;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("MongoSchema.Failed", ExceptionMessageService.GetReason(ex));
            }
            finally
            {
                Cursor = previous;
                busy = false;
                analyzeButton.Enabled = true;
            }
        }
    }
}

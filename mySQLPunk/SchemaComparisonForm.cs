using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class SchemaComparisonEndpoint
    {
        public string ConnectionName { get; set; }
        public string DatabaseName { get; set; }
        public string ProviderName { get; set; }
        public IDatabase Database { get; set; }

        public string DisplayName
        {
            get
            {
                string database = DatabaseName ?? string.Empty;
                string connection = ConnectionName ?? string.Empty;
                string provider = ProviderName ?? string.Empty;
                string name = string.IsNullOrWhiteSpace(connection) ? database : connection + " / " + database;
                return string.IsNullOrWhiteSpace(provider) ? name : name + " (" + provider + ")";
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class SchemaComparisonTargetDialog : Form
    {
        private readonly ComboBox targetComboBox;

        public SchemaComparisonTargetDialog(SchemaComparisonEndpoint source, IEnumerable<SchemaComparisonEndpoint> targets)
        {
            Text = Localization.T("SchemaComparison.SelectTargetTitle");
            Width = 600;
            Height = 230;
            MinimumSize = new Size(480, 230);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 2,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label sourceLabel = new Label
            {
                AutoSize = true,
                Text = Localization.T("SchemaComparison.Source") + ":",
                Margin = new Padding(0, 8, 12, 8)
            };
            TextBox sourceTextBox = new TextBox
            {
                ReadOnly = true,
                Text = source == null ? string.Empty : source.DisplayName,
                Margin = new Padding(0, 5, 0, 8)
            };
            Label targetLabel = new Label
            {
                AutoSize = true,
                Text = Localization.T("SchemaComparison.Target") + ":",
                Margin = new Padding(0, 8, 12, 8)
            };
            targetComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 5, 0, 8)
            };
            if (targets != null)
            {
                foreach (SchemaComparisonEndpoint target in targets) targetComboBox.Items.Add(target);
            }
            if (targetComboBox.Items.Count > 0) targetComboBox.SelectedIndex = 0;

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 14, 0, 0)
            };
            Button okButton = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            okButton.Enabled = targetComboBox.Items.Count > 0;
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            layout.Controls.Add(sourceLabel, 0, 0);
            Control sourceField = UiField.Wrap(sourceTextBox);
            sourceField.Dock = DockStyle.Top;
            sourceField.Margin = sourceTextBox.Margin;
            layout.Controls.Add(sourceField, 1, 0);
            layout.Controls.Add(targetLabel, 0, 1);
            Control targetField = UiField.Wrap(targetComboBox);
            targetField.Dock = DockStyle.Top;
            targetField.Margin = targetComboBox.Margin;
            layout.Controls.Add(targetField, 1, 1);
            layout.Controls.Add(buttons, 0, 2);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            ThemeManager.ApplyTo(this);
        }

        public SchemaComparisonEndpoint SelectedTarget
        {
            get { return targetComboBox.SelectedItem as SchemaComparisonEndpoint; }
        }
    }

    public sealed class SchemaComparisonForm : Form, IDockableForm
    {
        private SchemaComparisonEndpoint source;
        private SchemaComparisonEndpoint target;
        private readonly ToolStripButton refreshButton;
        private readonly ToolStripButton swapButton;
        private readonly ToolStripButton exportButton;
        private readonly ToolStripButton floatButton;
        private readonly ToolStripButton dockButton;
        private readonly Label directionLabel;
        private readonly DataGridView differencesGrid;
        private readonly ToolStripStatusLabel statusLabel;
        private Form1 mainHost;
        private bool loaded;

        public SchemaComparisonForm(SchemaComparisonEndpoint source, SchemaComparisonEndpoint target)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");
            if (source.Database == null) throw new ArgumentException("Source database is required.", "source");
            if (target.Database == null) throw new ArgumentException("Target database is required.", "target");
            this.source = source;
            this.target = target;

            Width = 1120;
            Height = 720;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterParent;

            ToolStrip toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            refreshButton = new ToolStripButton(Localization.T("SchemaComparison.Refresh"));
            swapButton = new ToolStripButton(Localization.T("SchemaComparison.Swap"));
            exportButton = new ToolStripButton(Localization.T("SchemaComparison.ExportHtml"));
            floatButton = new ToolStripButton(Localization.T("Query.Float"));
            dockButton = new ToolStripButton(Localization.T("Query.Dock")) { Visible = false };
            toolStrip.Items.AddRange(new ToolStripItem[]
            {
                refreshButton,
                swapButton,
                new ToolStripSeparator(),
                exportButton,
                new ToolStripSeparator(),
                floatButton,
                dockButton
            });

            Panel header = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 8, 12, 6) };
            directionLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font, FontStyle.Bold)
            };
            header.Controls.Add(directionLabel);

            differencesGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            differencesGrid.Columns.Add(NewColumn("Category", Localization.T("SchemaComparison.Category"), 22));
            differencesGrid.Columns.Add(NewColumn("Object", Localization.T("SchemaComparison.Object"), 18));
            differencesGrid.Columns.Add(NewColumn("Detail", Localization.T("SchemaComparison.Detail"), 24));
            differencesGrid.Columns.Add(NewColumn("Source", Localization.T("SchemaComparison.Source"), 28));
            differencesGrid.Columns.Add(NewColumn("Target", Localization.T("SchemaComparison.Target"), 28));

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("SchemaComparison.Ready"))
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(differencesGrid);
            Controls.Add(header);
            Controls.Add(statusStrip);
            Controls.Add(toolStrip);

            refreshButton.Click += (sender, args) => RefreshComparison();
            swapButton.Click += (sender, args) => SwapEndpoints();
            exportButton.Click += (sender, args) => ExportHtml();
            floatButton.Click += (sender, args) => { if (mainHost != null) mainHost.FloatDockableForm(this); };
            dockButton.Click += (sender, args) => { if (mainHost != null) mainHost.DockDockableForm(this); };
            Shown += (sender, args) =>
            {
                if (loaded) return;
                loaded = true;
                RefreshComparison();
            };

            UpdateEndpointLabels();
            ThemeManager.ApplyTo(this);
        }

        public SchemaComparisonResult ComparisonResult { get; private set; }

        public void SetMainHost(Form1 mainHost)
        {
            this.mainHost = mainHost;
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
            return database != null && (ReferenceEquals(source.Database, database) || ReferenceEquals(target.Database, database));
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
            floatButton.Visible = true;
            dockButton.Visible = false;
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
            floatButton.Visible = false;
            dockButton.Visible = mainHost != null;
        }

        private static DataGridViewTextBoxColumn NewColumn(string name, string headerText, float fillWeight)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = headerText,
                FillWeight = fillWeight,
                SortMode = DataGridViewColumnSortMode.Automatic
            };
        }

        private void UpdateEndpointLabels()
        {
            Text = Localization.Format("SchemaComparison.Title", source.DatabaseName, target.DatabaseName);
            directionLabel.Text = source.DisplayName + "  →  " + target.DisplayName;
        }

        private void RefreshComparison()
        {
            Cursor previousCursor = Cursor;
            refreshButton.Enabled = false;
            swapButton.Enabled = false;
            exportButton.Enabled = false;
            statusLabel.Text = Localization.T("SchemaComparison.LoadingSource");
            Cursor = Cursors.WaitCursor;
            try
            {
                SchemaModelSnapshot sourceSnapshot = SchemaModelService.Load(source.Database, source.DatabaseName);
                statusLabel.Text = Localization.T("SchemaComparison.LoadingTarget");
                SchemaModelSnapshot targetSnapshot = SchemaModelService.Load(target.Database, target.DatabaseName);
                statusLabel.Text = Localization.T("SchemaComparison.Comparing");
                ComparisonResult = SchemaComparisonService.Compare(sourceSnapshot, targetSnapshot);
                BindDifferences(ComparisonResult);
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("SchemaComparison.LoadFailed", ExceptionMessageService.GetReason(ex));
                MessageBox.Show(statusLabel.Text, Localization.T("SchemaComparison.ReportTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                refreshButton.Enabled = true;
                swapButton.Enabled = true;
                exportButton.Enabled = ComparisonResult != null;
            }
        }

        private void BindDifferences(SchemaComparisonResult result)
        {
            differencesGrid.Rows.Clear();
            foreach (SchemaDifference difference in result.Differences)
            {
                differencesGrid.Rows.Add(
                    SchemaComparisonService.GetKindDisplayName(difference.Kind),
                    difference.ObjectName,
                    difference.DetailName,
                    difference.SourceValue,
                    difference.TargetValue);
            }

            statusLabel.Text = result.Differences.Count == 0
                ? Localization.T("SchemaComparison.NoDifferences")
                : Localization.Format("SchemaComparison.Status", result.Differences.Count, result.SourceOnlyCount,
                    result.TargetOnlyCount, result.ChangedCount, result.WarningCount);
        }

        private void SwapEndpoints()
        {
            SchemaComparisonEndpoint previousSource = source;
            source = target;
            target = previousSource;
            ComparisonResult = null;
            differencesGrid.Rows.Clear();
            UpdateEndpointLabels();
            RefreshComparison();
        }

        private void ExportHtml()
        {
            if (ComparisonResult == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "HTML|*.html",
                DefaultExt = "html",
                AddExtension = true,
                FileName = MakeSafeFileName(source.DatabaseName) + "_vs_" + MakeSafeFileName(target.DatabaseName) + "_schema_diff.html",
                Title = Localization.T("SchemaComparison.ExportHtml")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName,
                        SchemaComparisonService.BuildHtml(ComparisonResult, Application.ProductVersion),
                        new UTF8Encoding(true));
                    statusLabel.Text = Localization.Format("SchemaComparison.Exported", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("SchemaComparison.ExportFailed", ExceptionMessageService.GetReason(ex)),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string MakeSafeFileName(string value)
        {
            string output = string.IsNullOrWhiteSpace(value) ? "database" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) output = output.Replace(invalid, '_');
            return output;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (mainHost != null) mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }
    }
}

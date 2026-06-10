using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public class SqliteSpecialObjectWizardForm : Form
    {
        private readonly IDatabase _db;
        private readonly string _databaseName;

        private ComboBox cboKind;
        private TextBox txtObjectName;
        private TextBox txtColumns;
        private TextBox txtTokenizer;
        private TextBox txtContentTable;
        private TextBox txtTargetTable;
        private TextBox txtGeometryColumn;
        private RichTextBox rtbPreview;
        private Button btnExecute;

        public SqliteSpecialObjectWizardForm(IDatabase db, string databaseName)
        {
            _db = db;
            _databaseName = databaseName;
            InitializeComponent();
            ApplyLanguage();
            UpdateFieldVisibility();
            UpdatePreview();
        }

        private void InitializeComponent()
        {
            Text = Localization.T("SqliteWizard.Title");
            Size = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            cboKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            cboKind.Items.AddRange(new object[]
            {
                Localization.T("SqliteWizard.Fts"),
                Localization.T("SqliteWizard.RTree"),
                Localization.T("SqliteWizard.SpatialIndex")
            });
            cboKind.SelectedIndex = 0;
            cboKind.SelectedIndexChanged += (s, e) =>
            {
                UpdateFieldVisibility();
                UpdatePreview();
            };

            txtObjectName = CreateTextBox("search_idx");
            txtColumns = CreateTextBox("title, body");
            txtTokenizer = CreateTextBox("unicode61");
            txtContentTable = CreateTextBox("");
            txtTargetTable = CreateTextBox("");
            txtGeometryColumn = CreateTextBox("geometry");

            AddRow(layout, 0, Localization.T("SqliteWizard.Kind"), cboKind);
            AddRow(layout, 1, Localization.T("SqliteWizard.ObjectName"), txtObjectName);
            AddRow(layout, 2, Localization.T("SqliteWizard.Columns"), txtColumns);
            AddRow(layout, 3, Localization.T("SqliteWizard.Tokenizer"), txtTokenizer);
            AddRow(layout, 4, Localization.T("SqliteWizard.ContentTable"), txtContentTable);
            AddRow(layout, 5, Localization.T("SqliteWizard.TargetTable"), txtTargetTable);
            AddRow(layout, 6, Localization.T("SqliteWizard.GeometryColumn"), txtGeometryColumn);

            rtbPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10),
                ReadOnly = false
            };
            layout.Controls.Add(new Label { Text = Localization.T("SqliteWizard.SqlPreview"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
            layout.Controls.Add(rtbPreview, 1, 7);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            btnExecute = new Button { Text = Localization.T("Query.Execute"), Width = 90 };
            Button btnClose = new Button { Text = Localization.T("Common.Close"), Width = 90, DialogResult = DialogResult.Cancel };
            btnExecute.Click += BtnExecute_Click;
            buttons.Controls.Add(btnClose);
            buttons.Controls.Add(btnExecute);
            layout.Controls.Add(buttons, 1, 8);

            Controls.Add(layout);
        }

        private TextBox CreateTextBox(string value)
        {
            TextBox box = new TextBox { Text = value, Width = 420 };
            box.TextChanged += (s, e) => UpdatePreview();
            return box;
        }

        private void AddRow(TableLayoutPanel layout, int row, string labelText, Control control)
        {
            layout.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void ApplyLanguage()
        {
            Text = Localization.T("SqliteWizard.Title") + " - " + _databaseName;
        }

        private SqliteSpecialObjectKind SelectedKind
        {
            get
            {
                if (cboKind.SelectedIndex == 1) return SqliteSpecialObjectKind.RTreeVirtualTable;
                if (cboKind.SelectedIndex == 2) return SqliteSpecialObjectKind.SpatiaLiteSpatialIndex;
                return SqliteSpecialObjectKind.FtsVirtualTable;
            }
        }

        private void UpdateFieldVisibility()
        {
            SqliteSpecialObjectKind kind = SelectedKind;
            bool isFts = kind == SqliteSpecialObjectKind.FtsVirtualTable;
            bool isRTree = kind == SqliteSpecialObjectKind.RTreeVirtualTable;
            bool isSpatial = kind == SqliteSpecialObjectKind.SpatiaLiteSpatialIndex;

            txtObjectName.Enabled = isFts || isRTree;
            txtColumns.Enabled = isFts || isRTree;
            txtTokenizer.Enabled = isFts;
            txtContentTable.Enabled = isFts;
            txtTargetTable.Enabled = isSpatial;
            txtGeometryColumn.Enabled = isSpatial;

            if (isRTree && string.Equals(txtColumns.Text.Trim(), "title, body", StringComparison.OrdinalIgnoreCase))
            {
                txtObjectName.Text = "idx_rtree";
                txtColumns.Text = "minX, maxX, minY, maxY";
            }
        }

        private void UpdatePreview()
        {
            if (rtbPreview == null) return;
            try
            {
                rtbPreview.Text = BuildSql();
                if (btnExecute != null) btnExecute.Enabled = true;
            }
            catch (Exception ex)
            {
                rtbPreview.Text = "-- " + ex.Message;
                if (btnExecute != null) btnExecute.Enabled = false;
            }
        }

        private string BuildSql()
        {
            switch (SelectedKind)
            {
                case SqliteSpecialObjectKind.RTreeVirtualTable:
                    return SqliteSpecialObjectSqlBuilder.BuildRTreeVirtualTable(
                        txtObjectName.Text,
                        "id",
                        SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames(txtColumns.Text));

                case SqliteSpecialObjectKind.SpatiaLiteSpatialIndex:
                    return SqliteSpecialObjectSqlBuilder.BuildSpatiaLiteSpatialIndex(
                        txtTargetTable.Text,
                        txtGeometryColumn.Text);

                default:
                    return SqliteSpecialObjectSqlBuilder.BuildFtsVirtualTable(
                        txtObjectName.Text,
                        SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames(txtColumns.Text),
                        txtTokenizer.Text,
                        txtContentTable.Text);
            }
        }

        private void BtnExecute_Click(object sender, EventArgs e)
        {
            string sql = rtbPreview.Text == null ? "" : rtbPreview.Text.Trim();
            if (sql.Length == 0 || sql.StartsWith("--"))
            {
                MessageBox.Show(Localization.T("SqliteWizard.NoSql"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(Localization.Format("Designer.ConfirmExecuteSql", sql), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Dictionary<string, string> result = _db.ExecSQL(sql);
                if (result.ContainsKey("status") && string.Equals(result["status"], "OK", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(Localization.T("SqliteWizard.ExecuteSucceeded"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string reason = BuildExecutionFailureReason(result);
                MessageBox.Show(Localization.Format("SqliteWizard.ExecuteFailed", reason), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Format("SqliteWizard.ExecuteFailed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string BuildExecutionFailureReason(Dictionary<string, string> result)
        {
            return DatabaseExecutionResultService.GetFailureReason(result);
        }
    }
}

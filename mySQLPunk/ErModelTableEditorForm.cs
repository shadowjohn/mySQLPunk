using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>編輯模型中的資料表（不直接改資料庫）：名稱、欄位（型別、可為 NULL、主鍵）與這張表的外鍵。</summary>
    public sealed class ErModelTableEditorForm : Form
    {
        private readonly Func<ErModelTable, List<ErModelRelationship>, int> apply;
        private readonly TextBox nameBox;
        private readonly DataGridView columnsGrid;
        private readonly DataGridView keysGrid;

        public ErModelTableEditorForm(ErModelTable table, IList<ErModelRelationship> outgoing, IList<string> tableNames, Func<ErModelTable, List<ErModelRelationship>, int> apply)
        {
            this.apply = apply;
            Text = table == null ? Localization.T("ErModel.AddTable") : Localization.Format("ErModel.EditTableTitle", table.Name);
            Width = 760;
            Height = 620;
            MinimumSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;

            nameBox = new TextBox { Dock = DockStyle.Fill, Text = table == null ? string.Empty : table.Name };
            columnsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            columnsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = Localization.T("ErModel.Column.Name"), FillWeight = 35 });
            columnsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = Localization.T("ErModel.Column.Type"), FillWeight = 35 });
            columnsGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Nullable", HeaderText = Localization.T("ErModel.Column.Nullable"), FillWeight = 15 });
            columnsGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "PrimaryKey", HeaderText = Localization.T("ErModel.Column.PrimaryKey"), FillWeight = 15 });
            columnsGrid.DefaultValuesNeeded += (sender, args) => args.Row.Cells["Nullable"].Value = true;
            if (table != null)
            {
                foreach (ErModelColumn column in table.Columns) columnsGrid.Rows.Add(column.Name, column.DataType, column.Nullable, column.PrimaryKey);
            }
            columnsGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (columnsGrid.CurrentCell is DataGridViewCheckBoxCell) columnsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            columnsGrid.CellValueChanged += (sender, args) =>
            {
                // 使用者勾選主鍵時預設改成 NOT NULL；載入既有欄位不觸發（仍可手動改回，SQLite 的 INTEGER PRIMARY KEY 就回報為可為 NULL）。
                if (args.RowIndex < 0 || columnsGrid.CurrentCell == null || columnsGrid.CurrentCell.RowIndex != args.RowIndex || args.ColumnIndex != columnsGrid.Columns["PrimaryKey"].Index) return;
                if (columnsGrid.Rows[args.RowIndex].Cells["PrimaryKey"].Value is bool && (bool)columnsGrid.Rows[args.RowIndex].Cells["PrimaryKey"].Value)
                {
                    columnsGrid.Rows[args.RowIndex].Cells["Nullable"].Value = false;
                }
            };

            keysGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            keysGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Column", HeaderText = Localization.T("ErModel.Key.Column"), FillWeight = 25 });
            DataGridViewComboBoxColumn referenced = new DataGridViewComboBoxColumn { Name = "ToTable", HeaderText = Localization.T("ErModel.Key.ToTable"), FillWeight = 25, FlatStyle = FlatStyle.Flat };
            referenced.Items.AddRange(tableNames.Cast<object>().ToArray());
            keysGrid.Columns.Add(referenced);
            keysGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ToColumn", HeaderText = Localization.T("ErModel.Key.ToColumn"), FillWeight = 25 });
            keysGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "KeyName", HeaderText = Localization.T("ErModel.Key.Name"), FillWeight = 25 });
            foreach (ErModelRelationship relationship in outgoing ?? new List<ErModelRelationship>())
            {
                if (!referenced.Items.Contains(relationship.ToTable)) referenced.Items.Add(relationship.ToTable);
                keysGrid.Rows.Add(relationship.FromColumn, relationship.ToTable, relationship.ToColumn, relationship.Name);
            }
            keysGrid.DataError += (sender, args) => args.ThrowException = false;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = Localization.T("ErModel.TableName"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(nameBox, 1, 0);
            layout.Controls.Add(new Label { Text = Localization.T("ErModel.Columns"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 1);
            layout.Controls.Add(columnsGrid, 1, 1);
            layout.Controls.Add(new Label { Text = Localization.T("ErModel.ForeignKeys"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 2);
            layout.Controls.Add(keysGrid, 1, 2);
            layout.Controls.Add(new Label { Text = Localization.T("ErModel.TableEditorHint"), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = ThemeManager.MutedTextColor, Padding = new Padding(0, 6, 0, 0) }, 1, 3);

            Button okButton = new Button { Text = Localization.T("Common.OK"), AutoSize = true };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            Controls.Add(layout);
            Controls.Add(buttons);
            CancelButton = cancelButton;

            okButton.Click += (sender, args) =>
            {
                try
                {
                    columnsGrid.EndEdit();
                    keysGrid.EndEdit();
                    apply(BuildTable(), BuildKeys());
                    DialogResult = DialogResult.OK;
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            ThemeManager.ApplyTo(this);
        }

        public ErModelTable BuildTable()
        {
            ErModelTable table = new ErModelTable { Name = nameBox.Text };
            foreach (DataGridViewRow row in columnsGrid.Rows)
            {
                if (row.IsNewRow) continue;
                string name = Convert.ToString(row.Cells["Name"].Value);
                string type = Convert.ToString(row.Cells["Type"].Value);
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(type)) continue;
                table.Columns.Add(new ErModelColumn
                {
                    Name = name,
                    DataType = type,
                    Nullable = row.Cells["Nullable"].Value is bool && (bool)row.Cells["Nullable"].Value,
                    PrimaryKey = row.Cells["PrimaryKey"].Value is bool && (bool)row.Cells["PrimaryKey"].Value
                });
            }
            return table;
        }

        public List<ErModelRelationship> BuildKeys()
        {
            List<ErModelRelationship> keys = new List<ErModelRelationship>();
            foreach (DataGridViewRow row in keysGrid.Rows)
            {
                if (row.IsNewRow) continue;
                string column = Convert.ToString(row.Cells["Column"].Value);
                string toTable = Convert.ToString(row.Cells["ToTable"].Value);
                string toColumn = Convert.ToString(row.Cells["ToColumn"].Value);
                if (string.IsNullOrWhiteSpace(column) && string.IsNullOrWhiteSpace(toTable) && string.IsNullOrWhiteSpace(toColumn)) continue;
                string name = Convert.ToString(row.Cells["KeyName"].Value);
                keys.Add(new ErModelRelationship
                {
                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                    FromColumn = column,
                    ToTable = toTable,
                    ToColumn = toColumn
                });
            }
            return keys;
        }
    }
}

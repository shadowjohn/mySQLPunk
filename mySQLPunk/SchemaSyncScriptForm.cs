using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 同步 SQL 預覽。「腳本」分頁唯讀（複製／另存）；呼叫端提供執行委派時，「在目標執行」分頁可逐句勾選後
    /// 在目標連線執行。刪除類語句預設不勾，勾選時必須輸入目標資料庫名稱；不會送進查詢視窗。
    /// </summary>
    public sealed class SchemaSyncScriptForm : Form
    {
        private readonly SchemaSyncScript script;
        private readonly string targetDatabase;
        private readonly Func<IList<string>, SchemaSyncBatchResult> execute;
        private readonly ToolStripStatusLabel statusLabel;
        private readonly DataGridView statementGrid;
        private readonly Label executionLabel;
        private readonly Button executeButton;
        private bool running;

        public SchemaSyncScriptForm(SchemaSyncScript script, string targetDescription)
            : this(script, targetDescription, string.Empty, null)
        {
        }

        public SchemaSyncScriptForm(
            SchemaSyncScript script,
            string targetDescription,
            string targetDatabase,
            Func<IList<string>, SchemaSyncBatchResult> execute)
        {
            if (script == null) throw new ArgumentNullException("script");
            this.script = script;
            this.targetDatabase = targetDatabase ?? string.Empty;
            this.execute = execute;

            Text = Localization.T("SchemaSync.WindowTitle");
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(640, 440);
            StartPosition = FormStartPosition.CenterParent;

            Label targetLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(10, 6, 10, 0),
                AutoEllipsis = true,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(181, 71, 8),
                Text = Localization.Format("SchemaSync.RunOnTarget", SchemaSyncScriptService.SingleLine(targetDescription))
            };
            Label summaryLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(10, 2, 10, 0),
                AutoEllipsis = true,
                Text = script.Summary
            };

            TextBox editor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 10f),
                Text = script.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
            };

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage scriptPage = new TabPage(Localization.T("SchemaSync.ScriptTab"));
            scriptPage.Controls.Add(editor);
            tabs.TabPages.Add(scriptPage);

            statementGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            statementGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Run", HeaderText = Localization.T("SchemaSync.Column.Run"), Width = 56 });
            statementGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Statement",
                HeaderText = Localization.T("SchemaSync.Column.Statement"),
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 70,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Font = new Font(FontFamily.GenericMonospace, 9f) }
            });
            statementGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Result",
                HeaderText = Localization.T("SchemaSync.Column.Result"),
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 30,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
            });
            PopulateStatements();

            executionLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            executeButton = new Button { Text = Localization.T("SchemaSync.ExecuteButton"), AutoSize = true, Dock = DockStyle.Right };
            Button selectSafe = new Button { Text = Localization.T("SchemaSync.SelectSafe"), AutoSize = true };
            Button selectNone = new Button { Text = Localization.T("SchemaSync.SelectNone"), AutoSize = true };
            FlowLayoutPanel selection = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4) };
            selection.Controls.Add(selectSafe);
            selection.Controls.Add(selectNone);
            Label notice = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(6, 4, 6, 0),
                ForeColor = Color.FromArgb(181, 71, 8),
                Text = Localization.T("SchemaSync.ExecuteNotice")
            };
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6) };
            bottom.Controls.Add(executionLabel);
            bottom.Controls.Add(executeButton);

            if (execute != null && statementGrid.Rows.Count > 0)
            {
                TabPage executePage = new TabPage(Localization.T("SchemaSync.ExecuteTab"));
                executePage.Controls.Add(statementGrid);
                executePage.Controls.Add(selection);
                executePage.Controls.Add(notice);
                executePage.Controls.Add(bottom);
                tabs.TabPages.Add(executePage);
            }

            ToolStrip toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            ToolStripButton copyButton = new ToolStripButton(Localization.T("SchemaSync.Copy"));
            ToolStripButton saveButton = new ToolStripButton(Localization.T("SchemaSync.Save"));
            ToolStripButton closeButton = new ToolStripButton(Localization.T("Common.Close"));
            toolStrip.Items.AddRange(new ToolStripItem[] { copyButton, saveButton, new ToolStripSeparator(), closeButton });

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("SchemaSync.DestructiveNotice"))
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(tabs);
            Controls.Add(summaryLabel);
            Controls.Add(targetLabel);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            copyButton.Click += (sender, args) => CopyScript();
            saveButton.Click += (sender, args) => SaveScript();
            closeButton.Click += (sender, args) => Close();
            selectSafe.Click += (sender, args) => SetChecked(row => !IsDestructive(row));
            selectNone.Click += (sender, args) => SetChecked(row => false);
            executeButton.Click += (sender, args) => ExecuteSelected();
            FormClosing += (sender, args) => { if (running) args.Cancel = true; };
            ThemeManager.ApplyTo(this);
        }

        public SchemaSyncScript Script
        {
            get { return script; }
        }

        /// <summary>任一語句在目標成功執行後為 true，呼叫端據此重新比較。</summary>
        public bool ExecutedOnTarget { get; private set; }

        public int ExecutableRowCount
        {
            get { return statementGrid.Rows.Count; }
        }

        /// <summary>目前勾選的語句，依「非破壞性在前、原順序」排列；也供測試使用。</summary>
        public List<string> GetSelectedStatements()
        {
            return statementGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => Convert.ToBoolean(row.Cells["Run"].Value))
                .OrderBy(row => IsDestructive(row) ? 1 : 0)
                .ThenBy(row => row.Index)
                .Select(row => (string)row.Tag)
                .ToList();
        }

        public void SetStatementChecked(int index, bool isChecked)
        {
            statementGrid.Rows[index].Cells["Run"].Value = isChecked;
        }

        /// <summary>不經確認直接執行勾選語句；確認由 ExecuteSelected 負責，測試用這個入口驗證結果呈現。</summary>
        public SchemaSyncBatchResult RunSelected()
        {
            List<DataGridViewRow> rows = statementGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => Convert.ToBoolean(row.Cells["Run"].Value))
                .OrderBy(row => IsDestructive(row) ? 1 : 0)
                .ThenBy(row => row.Index)
                .ToList();
            SchemaSyncBatchResult result = execute(rows.Select(row => (string)row.Tag).ToList());
            ExecutedOnTarget = ExecutedOnTarget || result.Statements.Any(item => item.Outcome == SchemaSyncStatementOutcome.Succeeded);
            foreach (DataGridViewRow row in statementGrid.Rows) row.Cells["Result"].Value = string.Empty;
            for (int position = 0; position < rows.Count && position < result.Statements.Count; position++)
            {
                SchemaSyncStatementResult item = result.Statements[position];
                DataGridViewCell cell = rows[position].Cells["Result"];
                switch (item.Outcome)
                {
                    case SchemaSyncStatementOutcome.Succeeded:
                        cell.Value = Localization.Format("SchemaSync.Outcome.Succeeded", (int)item.Elapsed.TotalMilliseconds);
                        cell.Style.ForeColor = Color.FromArgb(6, 118, 71);
                        break;
                    case SchemaSyncStatementOutcome.Failed:
                        cell.Value = Localization.Format("SchemaSync.Outcome.Failed", item.Message);
                        cell.Style.ForeColor = Color.FromArgb(180, 35, 24);
                        break;
                    case SchemaSyncStatementOutcome.RolledBack:
                        cell.Value = Localization.T("SchemaSync.Outcome.RolledBack");
                        cell.Style.ForeColor = Color.FromArgb(181, 71, 8);
                        break;
                    default:
                        cell.Value = Localization.T("SchemaSync.Outcome.NotRun");
                        cell.Style.ForeColor = SystemColors.GrayText;
                        break;
                }
            }

            executionLabel.Text = result.Summary;
            executionLabel.ForeColor = result.Succeeded ? Color.FromArgb(6, 118, 71) : Color.FromArgb(180, 35, 24);
            return result;
        }

        private void PopulateStatements()
        {
            foreach (string sql in script.Statements)
            {
                int index = statementGrid.Rows.Add(true, sql, string.Empty);
                statementGrid.Rows[index].Tag = sql;
            }

            for (int i = 0; i < script.DestructiveStatements.Count; i++)
            {
                string description = i < script.DestructiveItems.Count ? script.DestructiveItems[i] : string.Empty;
                string sql = script.DestructiveStatements[i];
                int index = statementGrid.Rows.Add(false, Localization.T("SchemaSync.DestructivePrefix") + description + Environment.NewLine + sql, string.Empty);
                DataGridViewRow row = statementGrid.Rows[index];
                row.Tag = sql;
                row.Cells["Statement"].Style.ForeColor = Color.FromArgb(180, 35, 24);
                row.Cells["Statement"].Tag = "destructive";
            }
        }

        private static bool IsDestructive(DataGridViewRow row)
        {
            return string.Equals(row.Cells["Statement"].Tag as string, "destructive", StringComparison.Ordinal);
        }

        private void SetChecked(Func<DataGridViewRow, bool> predicate)
        {
            statementGrid.EndEdit();
            foreach (DataGridViewRow row in statementGrid.Rows) row.Cells["Run"].Value = predicate(row);
        }

        private void ExecuteSelected()
        {
            if (execute == null || running) return;
            statementGrid.EndEdit();
            List<DataGridViewRow> selected = statementGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => Convert.ToBoolean(row.Cells["Run"].Value))
                .ToList();
            if (selected.Count == 0)
            {
                executionLabel.Text = Localization.T("SchemaSync.NothingSelected");
                return;
            }

            int destructive = selected.Count(IsDestructive);
            bool confirmed = destructive > 0
                ? TypedConfirm(Localization.Format("SchemaSync.ConfirmDestructive", targetDatabase, selected.Count, destructive), targetDatabase)
                : MessageBox.Show(this, Localization.Format("SchemaSync.ConfirmExecute", targetDatabase, selected.Count),
                    Localization.T("SchemaSync.ConfirmTitle"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
            if (!confirmed) return;

            running = true;
            executeButton.Enabled = false;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            executionLabel.Text = Localization.T("SchemaSync.Running");
            try
            {
                RunSelected();
            }
            catch (Exception ex)
            {
                executionLabel.Text = ExceptionMessageService.GetReason(ex);
                executionLabel.ForeColor = Color.FromArgb(180, 35, 24);
            }
            finally
            {
                Cursor = previous;
                executeButton.Enabled = true;
                running = false;
            }
        }

        private bool TypedConfirm(string message, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return false;
            using (Form dialog = new Form
            {
                Text = Localization.T("SchemaSync.ConfirmTitle"),
                Width = 560,
                Height = 240,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                Label label = new Label { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12, 12, 12, 0), Text = message };
                TextBox input = new TextBox { Dock = DockStyle.Top };
                Button ok = new Button { Text = Localization.T("SchemaSync.ConfirmButton"), DialogResult = DialogResult.OK, Enabled = false, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                Panel inputPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 4, 12, 4) };
                inputPanel.Controls.Add(input);
                input.TextChanged += (sender, args) => ok.Enabled = string.Equals(input.Text, expected, StringComparison.Ordinal);
                dialog.Controls.Add(inputPanel);
                dialog.Controls.Add(label);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK && string.Equals(input.Text, expected, StringComparison.Ordinal);
            }
        }

        private void CopyScript()
        {
            try
            {
                Clipboard.SetText(script.Text);
                statusLabel.Text = Localization.T("SchemaSync.Copied");
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
            }
        }

        private void SaveScript()
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "SQL|*.sql",
                DefaultExt = "sql",
                AddExtension = true,
                FileName = "schema_sync_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql",
                Title = Localization.T("SchemaSync.Save")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, script.Text, new UTF8Encoding(false));
                    if (File.Exists(dialog.FileName)) File.Replace(temporary, dialog.FileName, null);
                    else File.Move(temporary, dialog.FileName);
                    statusLabel.Text = Localization.Format("SchemaSync.Saved", dialog.FileName);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    MessageBox.Show(Localization.Format("SchemaSync.SaveFailed", ExceptionMessageService.GetReason(ex)),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}

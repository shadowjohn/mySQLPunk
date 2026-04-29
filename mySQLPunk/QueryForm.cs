using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public partial class QueryForm : Form
    {
        private IDatabase _db;
        private string _databaseName;
        private List<string> _tableNames = new List<string>();
        private CancellationTokenSource _cts;

        // UI 元件
        private RichTextBox txtSql;
        private DataGridView dgvResults;
        private Button btnExecute;
        private Button btnCancel;
        private Button btnBeautify;
        private Button btnExportCsv;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private ListBox lstCompletion;
        private TabControl tabResults;

        private static readonly string[] Keywords =
        {
            "SELECT", "FROM", "WHERE", "INSERT", "INTO", "UPDATE", "DELETE",
            "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "ON",
            "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
            "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "DISTINCT",
            "COUNT", "SUM", "AVG", "MAX", "MIN", "CASE", "WHEN", "THEN",
            "ELSE", "END", "CREATE", "DROP", "ALTER", "TABLE", "INDEX",
            "DESC", "ASC", "SET", "VALUES", "UNION", "ALL", "EXISTS"
        };

        private static readonly string[] BreakKeywords =
        {
            "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY",
            "HAVING", "LIMIT", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
            "JOIN", "SET", "VALUES", "UNION"
        };

        private readonly string currentDatabase;
        private readonly string connectionHost;

        public QueryForm(IDatabase db, string dbName)
            : this(db, dbName, string.Empty)
        {
        }

        public QueryForm(IDatabase db, string dbName, string host)
        {
            InitializeQueryForm();
            this._db = db;
            this.currentDatabase = dbName ?? string.Empty;
            this.connectionHost = host ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(this.currentDatabase) || !string.IsNullOrWhiteSpace(this.connectionHost))
            {
                string title = "Query";
                if (!string.IsNullOrWhiteSpace(this.connectionHost))
                {
                    title += " - " + this.connectionHost;
                }
                if (!string.IsNullOrWhiteSpace(this.currentDatabase))
                {
                    title += " / " + this.currentDatabase;
                }
                this.Text = title;
            }
        }

        private void InitializeQueryForm()
        {
            Size = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 400);

            // ── 頂部工具列 ──
            Panel pnlTop = new Panel { Dock = DockStyle.Top, Height = 40 };

            btnExecute = new Button
            {
                Text = "Execute (F5)",
                Location = new Point(5, 5),
                Size = new Size(110, 30)
            };
            btnExecute.Click += (s, e) => ExecuteQueryAsync();

            btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(120, 5),
                Size = new Size(70, 30),
                Enabled = false
            };
            btnCancel.Click += (s, e) => CancelQuery();

            btnBeautify = new Button
            {
                Text = "Beautify",
                Location = new Point(195, 5),
                Size = new Size(80, 30)
            };
            btnBeautify.Click += (s, e) => BeautifySql();

            btnExportCsv = new Button
            {
                Text = "Export CSV",
                Location = new Point(280, 5),
                Size = new Size(90, 30),
                Enabled = false
            };
            btnExportCsv.Click += (s, e) => ExportCsv();

            pnlTop.Controls.Add(btnExecute);
            pnlTop.Controls.Add(btnCancel);
            pnlTop.Controls.Add(btnBeautify);
            pnlTop.Controls.Add(btnExportCsv);

            // ── 分割容器 ──
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 220
            };

            // ── SQL 編輯區 ──
            txtSql = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 11),
                AcceptsTab = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Text = "SELECT * FROM "
            };
            txtSql.KeyDown += TxtSql_KeyDown;
            txtSql.TextChanged += TxtSql_TextChanged;

            // 自動補完清單
            lstCompletion = new ListBox
            {
                Visible = false,
                Width = 180,
                Height = 120,
                IntegralHeight = false
            };
            lstCompletion.DoubleClick += (s, e) => ApplyCompletion();
            lstCompletion.KeyDown += LstCompletion_KeyDown;

            split.Panel1.Controls.Add(lstCompletion);
            split.Panel1.Controls.Add(txtSql);

            // ── 結果區 (TabControl) ──
            tabResults = new TabControl { Dock = DockStyle.Fill };

            TabPage tabData = new TabPage("Results");
            dgvResults = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackgroundColor = Color.White,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            tabData.Controls.Add(dgvResults);
            tabResults.TabPages.Add(tabData);

            split.Panel2.Controls.Add(tabResults);

            // ── 狀態列 ──
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(lblStatus);

            Controls.Add(split);
            Controls.Add(pnlTop);
            Controls.Add(statusStrip);
        }

        // ── 載入資料表名稱供自動補完 ──
        private void LoadTableNames()
        {
            try
            {
                if (!string.IsNullOrEmpty(_databaseName) && _db != null)
                {
                    _tableNames = _db.GetTables(_databaseName);
                }
            }
            catch
            {
                // 取得失敗時靜默忽略，不影響主要功能
            }
        }

        // ── SQL 語法高亮 ──
        private void ApplySyntaxHighlight()
        {
            txtSql.TextChanged -= TxtSql_TextChanged;

            int selStart = txtSql.SelectionStart;
            int selLen = txtSql.SelectionLength;

            txtSql.SuspendLayout();

            // 先全部重置為黑色
            txtSql.SelectAll();
            txtSql.SelectionColor = Color.Black;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);

            string text = txtSql.Text;
            string upper = text.ToUpperInvariant();

            // 上色關鍵字 (藍色粗體)
            foreach (string kw in Keywords)
            {
                int idx = 0;
                while ((idx = upper.IndexOf(kw, idx)) != -1)
                {
                    // 確認是完整單字邊界
                    bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_';
                    bool rightOk = idx + kw.Length >= text.Length
                        || (!char.IsLetterOrDigit(text[idx + kw.Length]) && text[idx + kw.Length] != '_');

                    if (leftOk && rightOk)
                    {
                        txtSql.Select(idx, kw.Length);
                        txtSql.SelectionColor = Color.FromArgb(0, 0, 205);
                        txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Bold);
                    }
                    idx += kw.Length;
                }
            }

            // 字串常值 (橙色)
            HighlightPattern(text, @"'[^']*'", Color.DarkOrange, FontStyle.Regular);

            // 單行注釋 (綠色)
            HighlightPattern(text, @"--[^\r\n]*", Color.DarkGreen, FontStyle.Italic);

            // 還原游標
            txtSql.Select(selStart, selLen);
            txtSql.SelectionColor = Color.Black;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);

            txtSql.ResumeLayout();
            txtSql.TextChanged += TxtSql_TextChanged;
        }

        private void HighlightPattern(string text, string pattern, Color color, FontStyle style)
        {
            foreach (Match m in Regex.Matches(text, pattern))
            {
                txtSql.Select(m.Index, m.Length);
                txtSql.SelectionColor = color;
                txtSql.SelectionFont = new Font(txtSql.Font, style);
            }
        }

        // ── Beautify SQL ──
        private void BeautifySql()
        {
            string sql = txtSql.Text;
            if (string.IsNullOrEmpty(sql)) return;

            // 關鍵字大寫
            foreach (string kw in Keywords)
            {
                sql = Regex.Replace(sql, @"\b" + kw + @"\b", kw, RegexOptions.IgnoreCase);
            }

            // 主要子句換行
            foreach (string bw in BreakKeywords)
            {
                sql = Regex.Replace(sql, @"\b" + Regex.Escape(bw) + @"\b", "\n" + bw, RegexOptions.IgnoreCase);
            }

            // AND / OR 縮排換行
            sql = Regex.Replace(sql, @"\bAND\b", "\n  AND", RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql, @"\bOR\b", "\n  OR", RegexOptions.IgnoreCase);

            // 清理多餘空行
            sql = Regex.Replace(sql, @"\n\s*\n", "\n");

            txtSql.Text = sql.Trim();
        }

        // ── 自動補完 ──
        private void ShowCompletion()
        {
            int pos = txtSql.SelectionStart;
            if (pos <= 0) { lstCompletion.Visible = false; return; }

            int start = txtSql.Text.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;
            string word = txtSql.Text.Substring(start, pos - start).ToUpperInvariant();

            if (word.Length < 2) { lstCompletion.Visible = false; return; }

            var allWords = new List<string>(Keywords);
            allWords.AddRange(_tableNames);
            allWords.Sort(StringComparer.OrdinalIgnoreCase);

            var matches = allWords.FindAll(k => k.ToUpperInvariant().StartsWith(word));

            if (matches.Count > 0)
            {
                lstCompletion.Items.Clear();
                foreach (var m in matches) lstCompletion.Items.Add(m);

                Point p = txtSql.GetPositionFromCharIndex(start);
                lstCompletion.Location = new Point(p.X, p.Y + txtSql.Font.Height + 4);
                lstCompletion.Visible = true;
                lstCompletion.BringToFront();
                lstCompletion.SelectedIndex = 0;
            }
            else
            {
                lstCompletion.Visible = false;
            }
        }

        private void ApplyCompletion()
        {
            if (lstCompletion.SelectedItem == null) return;

            string selected = lstCompletion.SelectedItem.ToString();
            int pos = txtSql.SelectionStart;
            int start = txtSql.Text.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;

            txtSql.Select(start, pos - start);
            txtSql.SelectedText = selected + " ";
            lstCompletion.Visible = false;
            txtSql.Focus();
        }

        // ── 取得要執行的 SQL 語句 ──
        private string GetSqlToExecute()
        {
            string sql = txtSql.Text;

            if (string.IsNullOrWhiteSpace(sql))
            {
                return sql;
            }

            if (string.IsNullOrWhiteSpace(currentDatabase))
            {
                return sql;
            }

            if (_db is my_mysql)
            {
                return "USE `" + currentDatabase.Replace("`", "``") + "`;\r\n" + sql;
            }

            return sql;
        }

        // ── 事件處理 ──
        private void TxtSql_TextChanged(object sender, EventArgs e)
        {
            ApplySyntaxHighlight();
            ShowCompletion();
        }

        private void TxtSql_KeyDown(object sender, KeyEventArgs e)
        {
            if (lstCompletion.Visible)
            {
                if (e.KeyCode == Keys.Down)
                {
                    lstCompletion.Focus();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Enter)
                {
                    ApplyCompletion();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.Escape)
                {
                    lstCompletion.Visible = false;
                    e.Handled = true;
                    return;
                }
            }

            // F5 或 Ctrl+Enter 執行
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Return))
            {
                ExecuteQueryAsync();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void LstCompletion_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
            {
                ApplyCompletion();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                lstCompletion.Visible = false;
                txtSql.Focus();
            }
        }

        // ── 取消查詢 ──
        private void CancelQuery()
        {
            _cts?.Cancel();
        }

        // ── 執行查詢 ──
        private async void ExecuteQueryAsync()
        {
            // 優先執行選取文字，否則全文
            string sql = txtSql.SelectionLength > 0
                ? txtSql.SelectedText.Trim()
                : txtSql.Text.Trim();

            if (string.IsNullOrEmpty(sql)) return;

            _cts = new CancellationTokenSource();
            btnExecute.Enabled = false;
            btnCancel.Enabled = true;
            btnExportCsv.Enabled = false;
            lblStatus.Text = "Executing...";

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                // 判斷是否為 SELECT/SHOW/EXPLAIN/DESC (顯示結果集) 或 DML (顯示影響行數)
                string firstWord = GetFirstWord(sql);
                bool isQuery = IsSelectStatement(firstWord);

                if (isQuery)
                {
                    DataTable dt = await Task.Run(
                        () => _db.SelectSQL(sql),
                        _cts.Token);

                    sw.Stop();
                    dgvResults.DataSource = dt;
                    AutoResizeColumns(dgvResults);
                    btnExportCsv.Enabled = dt.Rows.Count > 0;
                    lblStatus.Text = string.Format(
                        "OK  |  {0} rows  |  {1} ms",
                        dt.Rows.Count, sw.ElapsedMilliseconds);
                }
                else
                {
                    var result = await Task.Run(
                        () => _db.ExecSQL(sql),
                        _cts.Token);

                    sw.Stop();
                    dgvResults.DataSource = null;

                    if (result["status"] == "OK")
                    {
                        lblStatus.Text = string.Format(
                            "OK  |  {0} ms", sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        lblStatus.Text = "Error: " + result["reason"];
                        MessageBox.Show(result["reason"], "Execute Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                lblStatus.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                sw.Stop();
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.Message, "Query Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnExecute.Enabled = true;
                btnCancel.Enabled = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static string GetFirstWord(string sql)
        {
            string trimmed = sql.TrimStart();
            int i = 0;
            while (i < trimmed.Length && char.IsLetter(trimmed[i])) i++;
            return trimmed.Substring(0, i).ToUpperInvariant();
        }

        private static bool IsSelectStatement(string firstWord)
        {
            switch (firstWord)
            {
                case "SELECT":
                case "SHOW":
                case "EXPLAIN":
                case "DESC":
                case "DESCRIBE":
                case "WITH":
                    return true;
                default:
                    return false;
            }
        }

        private static void AutoResizeColumns(DataGridView dgv)
        {
            if (dgv.Columns.Count == 0) return;
            // 限制最大欄寬避免超寬欄位讓畫面難以閱讀
            dgv.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            foreach (DataGridViewColumn col in dgv.Columns)
            {
                if (col.Width > 300) col.Width = 300;
            }
        }

        // ── 匯出 CSV ──
        private void ExportCsv()
        {
            DataTable dt = dgvResults.DataSource as DataTable;
            if (dt == null || dt.Rows.Count == 0) return;

            using (SaveFileDialog dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = string.IsNullOrEmpty(_databaseName) ? "query" : _databaseName,
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    StringBuilder sb = new StringBuilder();

                    // 標頭
                    for (int c = 0; c < dt.Columns.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(CsvEscape(dt.Columns[c].ColumnName));
                    }
                    sb.AppendLine();

                    // 資料
                    foreach (DataRow row in dt.Rows)
                    {
                        for (int c = 0; c < dt.Columns.Count; c++)
                        {
                            if (c > 0) sb.Append(',');
                            sb.Append(CsvEscape(row[c]?.ToString() ?? ""));
                        }
                        sb.AppendLine();
                    }

                    File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                    lblStatus.Text = string.Format("Exported {0} rows to {1}", dt.Rows.Count, dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string CsvEscape(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnFormClosed(e);
        }
    }
}

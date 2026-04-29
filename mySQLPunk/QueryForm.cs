using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace mySQLPunk
{
    public class QueryForm : Form
    {
        // ... (中間代碼略) ...

        private void BeautifySql()
        {
            string sql = txtSql.Text;
            if (string.IsNullOrEmpty(sql)) return;

            // 1. 先處理關鍵字大寫化
            foreach (string kw in keywords)
            {
                sql = Regex.Replace(sql, @"\b" + kw + @"\b", kw, RegexOptions.IgnoreCase);
            }

            // 2. 處理核心關鍵字的換行
            string[] breakWords = { "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "JOIN", "LEFT JOIN", "RIGHT JOIN", "SET", "VALUES" };
            foreach (string bw in breakWords)
            {
                sql = Regex.Replace(sql, @"\b" + bw + @"\b", "\n" + bw, RegexOptions.IgnoreCase);
            }

            // 3. 處理 AND/OR 的縮進換行
            sql = Regex.Replace(sql, @"\bAND\b", "\n  AND", RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql, @"\bOR\b", "\n  OR", RegexOptions.IgnoreCase);

            // 4. 清理多餘空格與空行
            sql = Regex.Replace(sql, @"\n\s*\n", "\n"); 
            
            txtSql.Text = sql.Trim();
        }
        private IDatabase _db;
        private string _databaseName;
        
        private RichTextBox txtSql; // 改用 RichTextBox
        private DataGridView dgvResults;
        private Button btnExecute;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private ListBox lstCompletion; // 補完清單

        private string[] keywords = { "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "JOIN", "LEFT", "RIGHT", "ON", "GROUP", "BY", "ORDER", "LIMIT", "AND", "OR", "IN", "IS", "NULL", "NOT", "DESC", "ASC" };

        public QueryForm(IDatabase db, string databaseName)
        {
            _db = db;
            _databaseName = databaseName;
            InitializeComponent();
            this.Text = $"Query - {databaseName}";
        }

        private void InitializeComponent()
        {
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;

            SplitContainer split = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 200
            };

            txtSql = new RichTextBox()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 12),
                AcceptsTab = true,
                Text = "SELECT * FROM "
            };
            txtSql.TextChanged += TxtSql_TextChanged;
            txtSql.KeyDown += TxtSql_KeyDown;

            // 補完清單初始化
            lstCompletion = new ListBox()
            {
                Visible = false,
                Width = 150,
                Height = 100
            };
            lstCompletion.DoubleClick += (s, e) => ApplyCompletion();

            Panel pnlTop = new Panel() { Dock = DockStyle.Top, Height = 40 };
            btnExecute = new Button() { Text = "Execute (F5)", Location = new Point(10, 5), Size = new Size(120, 30) };
            btnExecute.Click += (s, e) => ExecuteQuery();
            
            Button btnBeautify = new Button()
            {
                Text = "Beautify SQL",
                Location = new Point(140, 5),
                Size = new Size(120, 30)
            };
            btnBeautify.Click += (s, e) => BeautifySql();
            
            pnlTop.Controls.Add(btnExecute);
            pnlTop.Controls.Add(btnBeautify);

            dgvResults = new DataGridView() { Dock = DockStyle.Fill, ReadOnly = true, BackgroundColor = Color.White };
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("Ready");
            statusStrip.Items.Add(lblStatus);

            split.Panel1.Controls.Add(lstCompletion); // 加入補完清單
            split.Panel1.Controls.Add(txtSql);
            split.Panel1.Controls.Add(pnlTop);
            split.Panel2.Controls.Add(dgvResults);

            this.Controls.Add(split);
            this.Controls.Add(statusStrip);
        }

        private void TxtSql_TextChanged(object sender, EventArgs e)
        {
            // 簡易語法高亮 (為了效能，這裡只做最基本的)
            int pos = txtSql.SelectionStart;
            string text = txtSql.Text;
            
            // 避免遞迴觸發 TextChanged
            txtSql.TextChanged -= TxtSql_TextChanged;

            foreach (string kw in keywords)
            {
                int start = 0;
                while ((start = text.ToUpper().IndexOf(kw, start)) != -1)
                {
                    txtSql.Select(start, kw.Length);
                    txtSql.SelectionColor = Color.Blue;
                    txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Bold);
                    start += kw.Length;
                }
            }

            txtSql.Select(pos, 0);
            txtSql.SelectionColor = Color.Black;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);
            
            txtSql.TextChanged += TxtSql_TextChanged;

            ShowCompletion();
        }

        private void ShowCompletion()
        {
            // 取得最後一個單字
            int pos = txtSql.SelectionStart;
            if (pos <= 0)
            {
                lstCompletion.Visible = false;
                return;
            }

            int start = txtSql.Text.LastIndexOfAny(new char[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;
            string word = txtSql.Text.Substring(start, pos - start).ToUpper();

            if (word.Length > 0)
            {
                var matches = Array.FindAll(keywords, k => k.StartsWith(word));
                if (matches.Length > 0)
                {
                    lstCompletion.Items.Clear();
                    lstCompletion.Items.AddRange(matches);
                    Point p = txtSql.GetPositionFromCharIndex(start);
                    lstCompletion.Location = new Point(p.X, p.Y + 20);
                    lstCompletion.Visible = true;
                    lstCompletion.BringToFront();
                    return;
                }
            }
            lstCompletion.Visible = false;
        }

        private void ApplyCompletion()
        {
            if (lstCompletion.SelectedItem == null) return;
            
            string selected = lstCompletion.SelectedItem.ToString();
            int pos = txtSql.SelectionStart;
            int start = txtSql.Text.LastIndexOfAny(new char[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;
            
            txtSql.Select(start, pos - start);
            txtSql.SelectedText = selected + " ";
            lstCompletion.Visible = false;
        }

        private void TxtSql_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                ExecuteQuery();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                lstCompletion.Visible = false;
            }
            else if (e.KeyCode == Keys.Down && lstCompletion.Visible)
            {
                lstCompletion.Focus();
            }
            else if (e.KeyCode == Keys.Tab && lstCompletion.Visible)
            {
                ApplyCompletion();
                e.Handled = true;
            }
        }

        private async void ExecuteQuery()
        {
            string sql = txtSql.Text.Trim();
            if (string.IsNullOrEmpty(sql)) return;

            try
            {
                btnExecute.Enabled = false; // 避免連點
                Stopwatch sw = Stopwatch.StartNew();
                lblStatus.Text = "Executing... (UI is still responsive)";
                
                // 調用非同步方法，這下子視窗就不會卡住了
                DataTable dt = await _db.SelectSQLAsync(sql);
                
                dgvResults.DataSource = dt;
                sw.Stop();
                
                lblStatus.Text = $"Success. Rows: {dt.Rows.Count}, Time: {sw.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Query failed.";
            }
            finally
            {
                btnExecute.Enabled = true;
            }
        }
    }
}

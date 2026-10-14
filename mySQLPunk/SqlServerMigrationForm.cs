using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class SqlServerMigrationForm : Form
    {
        private readonly SchemaComparisonEndpoint source;
        private readonly SchemaComparisonEndpoint target;
        private readonly ComboBox version = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly TextBox preview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
        private readonly TextBox confirmation = new TextBox { Width = 180 };
        private readonly Button inspect = new Button { Text = "唯讀預檢", AutoSize = true };
        private readonly Button start = new Button { Text = "確認搬至空目標", AutoSize = true, Enabled = false };
        private readonly Button cancel = new Button { Text = "取消", Enabled = false };
        private SqlServerMigrationPreview approved;
        private CancellationTokenSource cancellation;
        private bool running;

        public SqlServerMigrationForm(SchemaComparisonEndpoint source, SchemaComparisonEndpoint target)
        {
            this.source = source; this.target = target;
            Text = "SQL Server 降版相容搬移（有限範圍預覽版）";
            Width = 1000; Height = 680; StartPosition = FormStartPosition.CenterParent;
            version.Items.AddRange(new object[] { "2016 (13)", "2017 (14)", "2019 (15)", "2022 (16)", "2025 (17)" });
            var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76 };
            controls.Controls.Add(new Label { Text = "明確選取目標引擎", AutoSize = true }); controls.Controls.Add(version);
            controls.Controls.Add(inspect);
            controls.Controls.Add(new Label { Text = "輸入目標資料庫名稱", AutoSize = true }); controls.Controls.Add(confirmation);
            controls.Controls.Add(start); controls.Controls.Add(cancel);
            var notice = new Label { Dock = DockStyle.Top, Height = 100, Padding = new Padding(8), Text =
                "來源：" + source.DisplayName + " → 目標：" + target.DisplayName + "\n" +
                "只接受另建的空資料庫；不建立資料庫、不覆蓋、不自動重試。僅 dbo 一般 heap／白名單型別；identity、constraints、indexes、triggers、procs、schema 相依與進階功能全部阻擋。定序必須相同。\n" +
                "每次最多 100,000 列；分批寫入同一交易並逐欄內容驗證。2016–2025 目前僅 mock 驗證，尚無真實降版矩陣。來源使用 SERIALIZABLE，可能暫時阻擋其他寫入。" };
            Controls.Add(preview); Controls.Add(controls); Controls.Add(notice);
            inspect.Click += Inspect;
            start.Click += Start;
            cancel.Click += (sender, args) => cancellation?.Cancel();
            version.SelectedIndexChanged += (sender, args) => { approved = null; start.Enabled = false; };
            FormClosing += (sender, args) => { if (running) { args.Cancel = true; cancellation?.Cancel(); } };
        }

        private SqlServerMigrationSession Open(SchemaComparisonEndpoint endpoint)
        {
            var database = endpoint.Database as my_mssql;
            if (database == null) throw new InvalidOperationException("僅支援 SQL Server 來源與目標。");
            return new SqlServerMigrationSession(endpoint.ConnectionSettings, endpoint.DatabaseName);
        }

        private async void Inspect(object sender, EventArgs args)
        {
            if (running || version.SelectedIndex < 0) return;
            SetRunning(true); approved = null;
            int major = SqlServerCompatibilityMigration.SupportedTargetMajors[version.SelectedIndex];
            try
            {
                var result = await Task.Run(() =>
                {
                    using (var from = Open(source)) using (var to = Open(target))
                        return SqlServerCompatibilityMigration.Preview(from.ReadCatalog(), to.ReadCatalog(), major);
                });
                approved = result;
                preview.Text = "來源引擎 major " + result.Source.MajorVersion + "；目標 major " + result.Target.MajorVersion + Environment.NewLine +
                    "來源識別：" + result.Source.Identity + "；目標識別：" + result.Target.Identity + Environment.NewLine +
                    "定序：" + result.Source.Collation + " → " + result.Target.Collation + Environment.NewLine +
                    (result.CanStart ? "預檢通過；尚未寫入。" : "禁止執行：" + Environment.NewLine + string.Join(Environment.NewLine, result.Blockers)) +
                    Environment.NewLine + "資料預覽：500 列／批；保留 NULL 與原始型別；不顯示資料值。" + Environment.NewLine +
                    string.Join(Environment.NewLine, result.Source.Tables.Select(table => table.Name + ": " + table.RowCount + " 列")) + Environment.NewLine +
                    string.Join(Environment.NewLine, result.Statements);
            }
            catch { preview.Text = "無法完整讀取版本／metadata／權限；已停止。請確認連線與支援矩陣後重新預檢。"; }
            finally { SetRunning(false); }
        }

        private async void Start(object sender, EventArgs args)
        {
            if (running || approved == null || !approved.CanStart || confirmation.Text != target.DatabaseName) return;
            if (MessageBox.Show(this, "確認只寫入「" + target.DatabaseName + "」？所有資料與結構驗證成功才提交；不支援的物件不會略過。", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            cancellation = new CancellationTokenSource(); SetRunning(true);
            var plan = approved; string name = confirmation.Text;
            try
            {
                var result = await Task.Run(() =>
                {
                    using (var from = Open(source)) using (var to = Open(target))
                        return SqlServerCompatibilityMigration.Run(plan, name, from, to, cancellation.Token);
                });
                preview.Text += Environment.NewLine + (result.Succeeded ? "搬移已提交。" : "搬移未成功。") + Environment.NewLine + result.Report;
            }
            catch { preview.Text += Environment.NewLine + "連線或清理發生錯誤，無法確認搬移結果；請由 DBA 核對目標，禁止直接重試。"; }
            finally { cancellation.Dispose(); cancellation = null; approved = null; SetRunning(false); }
        }

        private void SetRunning(bool value)
        {
            running = value; inspect.Enabled = !value; version.Enabled = !value; confirmation.Enabled = !value;
            cancel.Enabled = value && cancellation != null;
            start.Enabled = !value && approved != null && approved.CanStart;
        }
    }
}

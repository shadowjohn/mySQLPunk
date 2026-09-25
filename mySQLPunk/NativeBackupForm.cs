using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 原生備份／還原：SQL Server 在伺服器端 BACKUP／RESTORE，PostgreSQL 與 MongoDB 呼叫本機的 pg_dump／mongodump 等工具。
    /// 還原一律建立新的資料庫，不覆蓋既有資料。
    /// </summary>
    public sealed class NativeBackupForm : Form
    {
        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly string provider;
        private readonly NativeBackupEndpoint endpoint;
        private readonly RadioButton backupMode;
        private readonly RadioButton restoreMode;
        private readonly TextBox pathBox;
        private readonly Button browseButton;
        private readonly TextBox newDatabaseBox;
        private readonly TextBox toolBox;
        private readonly Button toolBrowse;
        private readonly CheckBox compressionBox;
        private readonly CheckBox verifyBox;
        private readonly Label hintLabel;
        private readonly Button runButton;
        private readonly TextBox logBox;
        private readonly ToolStripStatusLabel statusLabel;
        private bool running;

        public NativeBackupForm(IDatabase database, string databaseName, NativeBackupEndpoint endpoint, string endpointUnavailableReason)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;
            this.endpoint = endpoint;
            provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);
            bool sqlServer = provider == "mssql";

            Text = Localization.Format("NativeBackup.WindowTitle", databaseName);
            Width = 760;
            Height = 620;
            MinimumSize = new Size(620, 520);
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            backupMode = new RadioButton { Text = Localization.T("NativeBackup.ModeBackup"), AutoSize = true, Checked = true };
            restoreMode = new RadioButton { Text = Localization.T("NativeBackup.ModeRestore"), AutoSize = true };
            FlowLayoutPanel modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            modes.Controls.Add(backupMode);
            modes.Controls.Add(restoreMode);
            AddRow(layout, 0, Localization.T("NativeBackup.Mode"), modes, null);

            pathBox = new TextBox { Dock = DockStyle.Fill };
            browseButton = new Button { Text = Localization.T("Common.Browse"), AutoSize = true, Visible = !sqlServer };
            AddRow(layout, 1, Localization.T(sqlServer ? "NativeBackup.ServerPath" : "NativeBackup.File"), pathBox, browseButton);

            newDatabaseBox = new TextBox { Dock = DockStyle.Fill, Text = databaseName + "_restored" };
            AddRow(layout, 2, Localization.T("NativeBackup.NewDatabase"), newDatabaseBox, null);

            toolBox = new TextBox { Dock = DockStyle.Fill };
            toolBrowse = new Button { Text = Localization.T("Common.Browse"), AutoSize = true };
            if (!sqlServer) AddRow(layout, 3, Localization.T("NativeBackup.Tool"), toolBox, toolBrowse);

            compressionBox = new CheckBox { Text = Localization.T("NativeBackup.Compression"), AutoSize = true };
            verifyBox = new CheckBox { Text = Localization.T("NativeBackup.Verify"), AutoSize = true, Checked = true };
            FlowLayoutPanel options = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            options.Controls.Add(compressionBox);
            options.Controls.Add(verifyBox);
            if (sqlServer) AddRow(layout, 4, Localization.T("NativeBackup.Options"), options, null);

            hintLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = Color.FromArgb(102, 112, 133),
                Text = Localization.T(sqlServer ? "NativeBackup.HintSqlServer" : provider == "postgresql" ? "NativeBackup.HintPostgres" : "NativeBackup.HintMongo")
            };
            layout.Controls.Add(hintLabel, 0, 5);
            layout.SetColumnSpan(hintLabel, 3);

            runButton = new Button { Text = Localization.T("NativeBackup.Run"), AutoSize = true };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 4, 8, 4) };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(runButton);

            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f) };
            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(string.Empty) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(logBox);
            Controls.Add(buttons);
            Controls.Add(layout);
            Controls.Add(statusStrip);

            if (sqlServer)
            {
                pathBox.Text = "/var/opt/mssql/data/" + databaseName + "_{yyyyMMdd_HHmmss}.bak";
            }
            else
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                pathBox.Text = Path.Combine(string.IsNullOrEmpty(documents) ? Path.GetTempPath() : documents, "mySQLPunk", "backups",
                    databaseName + "_{yyyyMMdd_HHmmss}" + (provider == "postgresql" ? ".dump" : ".archive.gz"));
                toolBox.Text = NativeBackupService.FindTool(ToolName(), null) ?? string.Empty;
            }

            backupMode.CheckedChanged += (sender, args) => UpdateMode();
            browseButton.Click += (sender, args) => BrowseFile();
            toolBrowse.Click += (sender, args) =>
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Title = ToolName(), Filter = "*.exe|*.exe|*.*|*.*" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) toolBox.Text = dialog.FileName;
                }
            };
            runButton.Click += async (sender, args) => await RunAsync();
            closeButton.Click += (sender, args) => Close();
            FormClosing += (sender, args) => { if (running) args.Cancel = true; };
            UpdateMode();
            if (!sqlServer && endpoint == null)
            {
                runButton.Enabled = false;
                statusLabel.Text = endpointUnavailableReason;
            }
            else if (!sqlServer && string.IsNullOrEmpty(toolBox.Text))
            {
                statusLabel.Text = Localization.Format("NativeBackup.ToolNotFound", ToolName());
            }
            ThemeManager.ApplyTo(this);
        }

        private string ToolName()
        {
            bool backup = backupMode == null || backupMode.Checked;
            if (provider == "postgresql") return backup ? "pg_dump" : "pg_restore";
            return backup ? "mongodump" : "mongorestore";
        }

        private void UpdateMode()
        {
            bool restore = restoreMode.Checked;
            newDatabaseBox.Enabled = restore;
            compressionBox.Enabled = !restore;
            if (provider != "mssql")
            {
                string current = toolBox.Text.Trim();
                bool matches = current.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(current), ToolName(), StringComparison.OrdinalIgnoreCase);
                if (!matches) toolBox.Text = SiblingTool(current) ?? NativeBackupService.FindTool(ToolName(), null) ?? string.Empty;
            }
        }

        /// <summary>pg_dump 與 pg_restore（mongodump 與 mongorestore）通常在同一個目錄。</summary>
        private string SiblingTool(string current)
        {
            if (string.IsNullOrEmpty(current) || !File.Exists(current)) return null;
            string candidate = Path.Combine(Path.GetDirectoryName(current), ToolName() + Path.GetExtension(current));
            return File.Exists(candidate) ? candidate : null;
        }

        private void BrowseFile()
        {
            if (backupMode.Checked)
            {
                using (SaveFileDialog dialog = new SaveFileDialog { FileName = Path.GetFileName(pathBox.Text), Filter = "*.*|*.*" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.FileName;
                }
            }
            else
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "*.*|*.*" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.FileName;
                }
            }
        }

        private async Task RunAsync()
        {
            if (running) return;
            bool restore = restoreMode.Checked;
            string path = ExpandPath(pathBox.Text.Trim());
            string newDatabase = newDatabaseBox.Text.Trim();
            string tool = toolBox.Text.Trim();
            bool compression = compressionBox.Checked;
            bool verify = verifyBox.Checked;
            if (restore)
            {
                try
                {
                    NativeBackupService.ValidateNewDatabaseName(newDatabase);
                }
                catch (InvalidOperationException ex)
                {
                    statusLabel.Text = ex.Message;
                    return;
                }
                if (MessageBox.Show(this, Localization.Format("NativeBackup.ConfirmRestore", path, newDatabase), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            }
            else if (provider != "mssql")
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }

            running = true;
            runButton.Enabled = false;
            statusLabel.Text = Localization.T("NativeBackup.Running");
            logBox.Clear();
            try
            {
                NativeBackupResult result = await Task.Run(() => Execute(restore, path, newDatabase, tool, compression, verify));
                logBox.Text = (result.Log ?? string.Empty).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                statusLabel.Text = result.Message + (result.Bytes > 0 ? " (" + (result.Bytes / 1024.0 / 1024.0).ToString("0.##") + " MB)" : string.Empty);
                statusLabel.ForeColor = result.Succeeded ? Color.FromArgb(6, 118, 71) : Color.FromArgb(180, 35, 24);
            }
            finally
            {
                running = false;
                runButton.Enabled = true;
            }
        }

        /// <summary>執行一次備份或還原；也供測試直接呼叫。</summary>
        public NativeBackupResult Execute(bool restore, string path, string newDatabase, string tool, bool compression, bool verify)
        {
            switch (provider)
            {
                case "mssql":
                    return restore
                        ? NativeBackupService.RestoreSqlServer(database, path, newDatabase)
                        : NativeBackupService.BackupSqlServer(database, databaseName, path, compression, verify);
                case "postgresql":
                    return restore
                        ? NativeBackupService.RestorePostgreSql(tool, database, endpoint, newDatabase, path)
                        : NativeBackupService.BackupPostgreSql(tool, endpoint, path);
                default:
                    return restore
                        ? NativeBackupService.RestoreMongo(tool, database, endpoint, newDatabase, path)
                        : NativeBackupService.BackupMongo(tool, endpoint, path);
            }
        }

        private static string ExpandPath(string value)
        {
            DateTime now = DateTime.Now;
            return value.Replace("{yyyyMMdd_HHmmss}", now.ToString("yyyyMMdd_HHmmss")).Replace("{yyyyMMdd}", now.ToString("yyyyMMdd"));
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control, Control extra)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 5);
            layout.Controls.Add(control, 1, row);
            if (extra != null)
            {
                extra.Margin = new Padding(6, 3, 0, 5);
                layout.Controls.Add(extra, 2, row);
            }
        }
    }
}

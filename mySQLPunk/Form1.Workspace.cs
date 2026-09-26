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
    /// <summary>協同合作：把工作區（連線、SQL 片段、AI 動作、自動執行作業、資料產生器字典）匯出成可放進 Git 的資料夾，或從資料夾匯入。</summary>
    public partial class Form1
    {
        private ToolStripMenuItem exportWorkspaceMenuItem;
        private ToolStripMenuItem importWorkspaceMenuItem;

        private void EnsureWorkspaceMenu()
        {
            if (exportWorkspaceMenuItem != null) return;
            exportWorkspaceMenuItem = new ToolStripMenuItem();
            importWorkspaceMenuItem = new ToolStripMenuItem();
            exportWorkspaceMenuItem.Click += (sender, args) => ExportWorkspaceWithDialog();
            importWorkspaceMenuItem.Click += (sender, args) => ImportWorkspaceWithDialog();
            int index = 檔案ToolStripMenuItem.DropDownItems.IndexOf(importConnectionsToolStripMenuItem);
            檔案ToolStripMenuItem.DropDownItems.Insert(index + 1, exportWorkspaceMenuItem);
            檔案ToolStripMenuItem.DropDownItems.Insert(index + 2, importWorkspaceMenuItem);
            ApplyWorkspaceMenuText();
        }

        private void ApplyWorkspaceMenuText()
        {
            if (exportWorkspaceMenuItem == null) return;
            exportWorkspaceMenuItem.Text = Localization.T("Workspace.ExportMenu");
            importWorkspaceMenuItem.Text = Localization.T("Workspace.ImportMenu");
        }

        private WorkspaceSources BuildWorkspaceSources(bool includeConnections)
        {
            string connectionsJson = null;
            if (includeConnections)
            {
                string temporary = Path.Combine(Path.GetTempPath(), "mysqlpunk-workspace-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    myN.exportConnections(temporary);
                    connectionsJson = File.ReadAllText(temporary, Encoding.UTF8);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return new WorkspaceSources
            {
                ConnectionsJson = connectionsJson,
                SnippetsPath = Path.Combine(Application.UserAppDataPath, "sql-snippets.json"),
                AiActionsPath = Path.Combine(Application.UserAppDataPath, "query-ai-actions.json"),
                JobStore = new ScheduledJobStore(),
                DictionaryDirectory = DataGeneratorDictionaryStore.DefaultDirectory
            };
        }

        private void ExportWorkspaceWithDialog()
        {
            using (WorkspaceExportDialog options = new WorkspaceExportDialog())
            {
                if (options.ShowDialog(this) != DialogResult.OK) return;
                using (FolderBrowserDialog folder = new FolderBrowserDialog { Description = Localization.T("Workspace.ChooseExportFolder"), ShowNewFolderButton = true })
                {
                    if (folder.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        Dictionary<string, int> counts = WorkspaceBundleService.Export(folder.SelectedPath, BuildWorkspaceSources(true), options.Options);
                        string message = Localization.Format("Workspace.Exported", folder.SelectedPath,
                            Count(counts, "connections"), Count(counts, "snippets"), Count(counts, "aiActions"), Count(counts, "jobs"), Count(counts, "dictionaries"));
                        UpdateMainStatus(message);
                        MessageBox.Show(this, message, Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ExceptionMessageService.GetReason(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private static int Count(Dictionary<string, int> counts, string key)
        {
            int value;
            return counts.TryGetValue(key, out value) ? value : 0;
        }

        private void ImportWorkspaceWithDialog()
        {
            using (FolderBrowserDialog folder = new FolderBrowserDialog { Description = Localization.T("Workspace.ChooseImportFolder"), ShowNewFolderButton = false })
            {
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    WorkspaceSources sources = BuildWorkspaceSources(false);
                    WorkspaceImportPlan plan = WorkspaceBundleService.Plan(folder.SelectedPath, sources);
                    int applied = 0;
                    List<string> errors = new List<string>();
                    if (plan.Items.Count > 0)
                    {
                        using (WorkspaceImportDialog dialog = new WorkspaceImportDialog(plan))
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) return;
                            applied = WorkspaceBundleService.Apply(dialog.SelectedItems, errors);
                        }
                    }
                    if (plan.ConnectionsPath != null) ImportConnectionsWithPreview(plan.ConnectionsPath, true);
                    string message = Localization.Format("Workspace.Imported", applied) +
                        (errors.Count + plan.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings.Concat(errors)) : string.Empty);
                    UpdateMainStatus(message);
                    MessageBox.Show(this, message, Localization.T("Common.Success"), MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ExceptionMessageService.GetReason(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    /// <summary>工作區匯出選項：是否包含登入帳號與 Webhook 網址（預設都不含）。</summary>
    public sealed class WorkspaceExportDialog : Form
    {
        private readonly CheckBox userNames;
        private readonly CheckBox webhooks;

        public WorkspaceExportDialog()
        {
            Text = Localization.T("Workspace.ExportMenu");
            Width = 520;
            Height = 290;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Label hint = new Label { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12, 12, 12, 0), Text = Localization.T("Workspace.ExportHint") };
            userNames = new CheckBox { Text = Localization.T("Workspace.IncludeUserNames"), AutoSize = true };
            webhooks = new CheckBox { Text = Localization.T("Workspace.IncludeWebhooks"), AutoSize = true };
            FlowLayoutPanel choices = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 4, 12, 0) };
            choices.Controls.Add(userNames);
            choices.Controls.Add(webhooks);
            Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
            Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(choices);
            Controls.Add(hint);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            ThemeManager.ApplyTo(this);
        }

        public WorkspaceExportOptions Options
        {
            get { return new WorkspaceExportOptions { IncludeUserNames = userNames.Checked, IncludeWebhooks = webhooks.Checked }; }
        }
    }

    /// <summary>工作區匯入預覽：列出每個項目是新增、變更或相同，勾選要套用的項目（相同的項目不需套用）。</summary>
    public sealed class WorkspaceImportDialog : Form
    {
        private readonly WorkspaceImportPlan plan;
        private readonly DataGridView grid;

        public WorkspaceImportDialog(WorkspaceImportPlan plan)
        {
            this.plan = plan;
            Text = Localization.T("Workspace.ImportMenu");
            Width = 760;
            Height = 560;
            MinimumSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterParent;
            Label hint = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 10, 10, 0), Text = Localization.T("Workspace.ImportHint") };
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Apply", HeaderText = string.Empty, FillWeight = 8 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = Localization.T("Workspace.Column.Category"), ReadOnly = true, FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = Localization.T("Workspace.Column.Name"), ReadOnly = true, FillWeight = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = Localization.T("Workspace.Column.State"), ReadOnly = true, FillWeight = 20 });
            foreach (WorkspaceImportItem item in plan.Items)
            {
                int index = grid.Rows.Add(item.State != WorkspaceItemState.Same, item.Category, item.Name, Localization.T("Workspace.State." + item.State));
                DataGridViewRow row = grid.Rows[index];
                row.Tag = item;
                if (item.State == WorkspaceItemState.Same) row.ReadOnly = true;
                if (item.State == WorkspaceItemState.Changed) row.Cells["State"].Style.ForeColor = ThemeManager.WarningColor;
            }
            Label connections = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                Padding = new Padding(10, 8, 10, 0),
                ForeColor = ThemeManager.MutedTextColor,
                Text = plan.ConnectionsPath == null ? Localization.T("Workspace.NoConnections") : Localization.T("Workspace.ConnectionsNext")
            };
            Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
            Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(grid);
            Controls.Add(hint);
            Controls.Add(connections);
            Controls.Add(buttons);
            CancelButton = cancel;
            ThemeManager.ApplyTo(this);
        }

        public List<WorkspaceImportItem> SelectedItems
        {
            get
            {
                grid.EndEdit();
                return grid.Rows.Cast<DataGridViewRow>()
                    .Where(row => row.Cells["Apply"].Value is bool && (bool)row.Cells["Apply"].Value)
                    .Select(row => (WorkspaceImportItem)row.Tag)
                    .Where(item => item.State != WorkspaceItemState.Same)
                    .ToList();
            }
        }
    }
}

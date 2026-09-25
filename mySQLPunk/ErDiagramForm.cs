using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ErDiagramForm : Form, IDockableForm
    {
        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly ErDiagramCanvas canvas;
        private readonly ToolStrip toolStrip;
        private readonly ToolStripButton refreshButton;
        private readonly ToolStripButton zoomOutButton;
        private readonly ToolStripButton zoomInButton;
        private readonly ToolStripButton fitButton;
        private readonly ToolStripButton exportButton;
        private readonly ToolStripButton exportSvgButton;
        private readonly ToolStripComboBox diagramBox;
        private readonly ToolStripButton floatButton;
        private readonly ToolStripButton dockButton;
        private readonly ToolStripLabel zoomLabel;
        private readonly ToolStripStatusLabel statusLabel;
        private Form1 mainHost;
        private bool loaded;
        private bool switchingDiagram;
        private SchemaModelSnapshot snapshot;
        private ErModelDocument document;
        private ErModelDiagram diagram;
        private string modelPath;
        private bool dirty;

        public ErDiagramForm(IDatabase database, string databaseName)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName ?? string.Empty;

            Text = Localization.Format("ErDiagram.Title", this.databaseName);
            Width = 1180;
            Height = 780;
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;

            toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            refreshButton = new ToolStripButton(Localization.T("ErDiagram.Refresh"));
            zoomOutButton = new ToolStripButton("−");
            zoomLabel = new ToolStripLabel("100%");
            zoomInButton = new ToolStripButton("+");
            fitButton = new ToolStripButton(Localization.T("ErDiagram.Fit"));
            exportButton = new ToolStripButton(Localization.T("ErDiagram.ExportPng"));
            exportSvgButton = new ToolStripButton(Localization.T("ErModel.ExportSvg"));
            diagramBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            ToolStripButton newDiagramButton = new ToolStripButton(Localization.T("ErModel.NewDiagram"));
            ToolStripButton deleteDiagramButton = new ToolStripButton(Localization.T("ErModel.DeleteDiagram"));
            ToolStripButton tablesButton = new ToolStripButton(Localization.T("ErModel.Tables"));
            ToolStripButton arrangeButton = new ToolStripButton(Localization.T("ErModel.AutoArrange"));
            ToolStripButton groupsButton = new ToolStripButton(Localization.T("ErModel.Groups"));
            ToolStripButton openButton = new ToolStripButton(Localization.T("ErModel.Open"));
            ToolStripButton saveButton = new ToolStripButton(Localization.T("ErModel.Save"));
            floatButton = new ToolStripButton(Localization.T("Query.Float"));
            dockButton = new ToolStripButton(Localization.T("Query.Dock")) { Visible = false };

            toolStrip.Items.AddRange(new ToolStripItem[]
            {
                refreshButton,
                new ToolStripSeparator(),
                new ToolStripLabel(Localization.T("ErModel.Diagram")),
                diagramBox,
                newDiagramButton,
                deleteDiagramButton,
                tablesButton,
                arrangeButton,
                groupsButton,
                new ToolStripSeparator(),
                openButton,
                saveButton,
                new ToolStripSeparator(),
                zoomOutButton,
                zoomLabel,
                zoomInButton,
                fitButton,
                new ToolStripSeparator(),
                exportButton,
                exportSvgButton,
                new ToolStripSeparator(),
                floatButton,
                dockButton
            });

            canvas = new ErDiagramCanvas { Dock = DockStyle.Fill };
            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("ErDiagram.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(canvas);
            Controls.Add(statusStrip);
            Controls.Add(toolStrip);

            refreshButton.Click += (sender, args) => RefreshDiagram();
            zoomOutButton.Click += (sender, args) => canvas.ZoomBy(-0.1f);
            zoomInButton.Click += (sender, args) => canvas.ZoomBy(0.1f);
            fitButton.Click += (sender, args) => canvas.FitToWindow();
            exportButton.Click += (sender, args) => ExportPng();
            exportSvgButton.Click += (sender, args) => ExportSvg();
            newDiagramButton.Click += (sender, args) => NewDiagram();
            deleteDiagramButton.Click += (sender, args) => DeleteDiagram();
            tablesButton.Click += (sender, args) => EditDiagramTables();
            arrangeButton.Click += (sender, args) => AutoArrange();
            groupsButton.Click += (sender, args) => EditGroups();
            openButton.Click += (sender, args) => OpenModel();
            saveButton.Click += (sender, args) => SaveModel();
            diagramBox.SelectedIndexChanged += (sender, args) =>
            {
                if (switchingDiagram || document == null || diagramBox.SelectedIndex < 0) return;
                diagram = document.Diagrams[diagramBox.SelectedIndex];
                canvas.SetModel(snapshot, document, diagram);
                FitWhenReady();
            };
            floatButton.Click += (sender, args) => { if (mainHost != null) mainHost.FloatDockableForm(this); };
            dockButton.Click += (sender, args) => { if (mainHost != null) mainHost.DockDockableForm(this); };
            canvas.ZoomChanged += (sender, args) => zoomLabel.Text = Math.Round(canvas.Zoom * 100f) + "%";
            canvas.LayoutChanged += (sender, args) => MarkDirty();
            canvas.LockedTableDragAttempted += table => statusLabel.Text = Localization.Format("ErModel.Locked", table);
            canvas.TableContextRequested += ShowTableMenu;

            Shown += (sender, args) =>
            {
                if (loaded) return;
                loaded = true;
                RefreshDiagram();
            };

            ThemeManager.ApplyTo(this);
            canvas.ApplyTheme();
        }

        public ErModelDocument Model { get { return document; } }

        public ErModelDiagram CurrentDiagram { get { return diagram; } }

        public ErDiagramCanvas Canvas { get { return canvas; } }

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
            return dirty;
        }

        public bool UsesDatabase(IDatabase targetDatabase)
        {
            return targetDatabase != null && ReferenceEquals(database, targetDatabase);
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

        /// <summary>重新讀取資料庫結構；已開啟的模型保留版面。也供測試直接呼叫。</summary>
        public void RefreshDiagram()
        {
            Cursor previousCursor = Cursor;
            refreshButton.Enabled = false;
            statusLabel.Text = Localization.T("ErDiagram.Loading");
            Cursor = Cursors.WaitCursor;
            try
            {
                snapshot = SchemaModelService.Load(database, databaseName);
                if (document == null)
                {
                    document = ErModelService.CreateDefault(snapshot, Localization.T("ErModel.DefaultDiagram"));
                    diagram = document.Diagrams[0];
                }
                ShowDocument();
                int missing = document.Diagrams.SelectMany(item => item.Tables)
                    .Count(item => !snapshot.Tables.Any(table => string.Equals(table.Name, item.Table, StringComparison.OrdinalIgnoreCase)));
                statusLabel.Text = Localization.Format(
                    snapshot.Warnings.Count == 0 ? "ErDiagram.Status" : "ErDiagram.StatusWithWarnings",
                    snapshot.Tables.Count,
                    snapshot.Relationships.Count,
                    snapshot.Warnings.Count) + (missing > 0 ? " " + Localization.Format("ErModel.MissingTables", missing) : string.Empty);
                FitWhenReady();
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("ErDiagram.LoadFailed", ExceptionMessageService.GetReason(ex));
                MessageBox.Show(statusLabel.Text, Localization.T("View.ERDiagram"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                refreshButton.Enabled = true;
            }
        }

        /// <summary>依外鍵重新分層排列目前圖表；也供測試直接呼叫。</summary>
        public void AutoArrange()
        {
            if (snapshot == null || diagram == null) return;
            ErModelService.ApplyLayeredLayout(diagram, snapshot);
            canvas.ReloadLayout();
            MarkDirty();
        }

        /// <summary>把資料表指定到群組（null 為不屬於任何群組）；也供測試直接呼叫。</summary>
        public void AssignGroup(string table, string group)
        {
            ErModelTablePlacement placement = diagram == null ? null : diagram.Find(table);
            if (placement == null) return;
            placement.Group = group;
            canvas.ReloadLayout();
            MarkDirty();
        }

        /// <summary>載入模型檔；也供測試直接呼叫。</summary>
        public void LoadModel(string path)
        {
            document = ErModelService.Load(path);
            diagram = document.Diagrams[0];
            modelPath = path;
            if (snapshot != null) ShowDocument();
            dirty = false;
            UpdateTitle();
        }

        /// <summary>儲存模型檔；也供測試直接呼叫。</summary>
        public void SaveModelTo(string path)
        {
            document.Database = databaseName;
            ErModelService.Save(document, path);
            modelPath = path;
            dirty = false;
            UpdateTitle();
            statusLabel.Text = Localization.Format("ErModel.Saved", path);
        }

        private void ShowDocument()
        {
            switchingDiagram = true;
            try
            {
                diagramBox.Items.Clear();
                foreach (ErModelDiagram item in document.Diagrams) diagramBox.Items.Add(item.Name);
                int index = Math.Max(0, document.Diagrams.IndexOf(diagram));
                diagram = document.Diagrams[index];
                diagramBox.SelectedIndex = index;
            }
            finally
            {
                switchingDiagram = false;
            }
            canvas.SetModel(snapshot, document, diagram);
        }

        /// <summary>視窗顯示後再縮放到適合大小；尚未建立視窗（例如測試直接呼叫）時立即縮放。</summary>
        private void FitWhenReady()
        {
            if (IsHandleCreated) BeginInvoke(new Action(canvas.FitToWindow));
            else canvas.FitToWindow();
        }

        private void MarkDirty()
        {
            if (dirty) return;
            dirty = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = modelPath == null ? string.Empty : " · " + Path.GetFileNameWithoutExtension(modelPath);
            Text = Localization.Format("ErDiagram.Title", databaseName) + name + (dirty ? " *" : string.Empty);
        }

        private void ShowTableMenu(string table, Point screen)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem groupMenu = new ToolStripMenuItem(Localization.T("ErModel.MoveToGroup"));
            ErModelTablePlacement placement = diagram.Find(table);
            ToolStripMenuItem none = new ToolStripMenuItem(Localization.T("ErModel.NoGroup")) { Checked = placement != null && placement.Group == null };
            none.Click += (sender, args) => AssignGroup(table, null);
            groupMenu.DropDownItems.Add(none);
            foreach (ErModelGroup group in document.Groups)
            {
                ErModelGroup current = group;
                ToolStripMenuItem item = new ToolStripMenuItem(group.Name) { Checked = placement != null && string.Equals(placement.Group, group.Name, StringComparison.OrdinalIgnoreCase) };
                item.Click += (sender, args) => AssignGroup(table, current.Name);
                groupMenu.DropDownItems.Add(item);
            }
            groupMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem newGroup = new ToolStripMenuItem(Localization.T("ErModel.NewGroup"));
            newGroup.Click += (sender, args) =>
            {
                string name = Prompt(Localization.T("ErModel.NewGroup"), Localization.T("ErModel.GroupNamePrompt"), string.Empty);
                if (string.IsNullOrWhiteSpace(name)) return;
                if (document.FindGroup(name) == null) document.Groups.Add(new ErModelGroup { Name = name.Trim(), Color = NextColor() });
                AssignGroup(table, name.Trim());
            };
            groupMenu.DropDownItems.Add(newGroup);
            menu.Items.Add(groupMenu);
            ToolStripMenuItem remove = new ToolStripMenuItem(Localization.T("ErModel.RemoveFromDiagram"));
            remove.Click += (sender, args) =>
            {
                diagram.Tables.RemoveAll(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
                canvas.ReloadLayout();
                MarkDirty();
            };
            menu.Items.Add(remove);
            menu.Closed += (sender, args) => BeginInvoke(new Action(menu.Dispose));
            ThemeManager.ApplyTo(menu);
            menu.Show(screen);
        }

        private string NextColor()
        {
            string[] palette = { "#2563eb", "#16a34a", "#d97706", "#dc2626", "#7c3aed", "#0891b2", "#db2777", "#4b5563" };
            return palette[document.Groups.Count % palette.Length];
        }

        private void NewDiagram()
        {
            if (document == null) return;
            string name = Prompt(Localization.T("ErModel.NewDiagram"), Localization.T("ErModel.DiagramNamePrompt"),
                Localization.Format("ErModel.DiagramDefaultName", document.Diagrams.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            if (document.Diagrams.Any(item => string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                statusLabel.Text = Localization.T("ErModel.Error.DuplicateDiagram");
                return;
            }
            diagram = new ErModelDiagram { Name = name.Trim() };
            document.Diagrams.Add(diagram);
            ShowDocument();
            MarkDirty();
            EditDiagramTables();
        }

        private void DeleteDiagram()
        {
            if (document == null || document.Diagrams.Count <= 1)
            {
                statusLabel.Text = Localization.T("ErModel.KeepOneDiagram");
                return;
            }
            if (MessageBox.Show(this, Localization.Format("ErModel.ConfirmDeleteDiagram", diagram.Name), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            document.Diagrams.Remove(diagram);
            diagram = document.Diagrams[0];
            ShowDocument();
            MarkDirty();
        }

        private void EditDiagramTables()
        {
            if (snapshot == null || diagram == null) return;
            using (Form dialog = new Form { Text = Localization.Format("ErModel.TablesTitle", diagram.Name), Width = 420, Height = 540, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false })
            {
                CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
                foreach (SchemaTableModel table in snapshot.Tables) list.Items.Add(table.Name, diagram.Find(table.Name) != null);
                Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dialog.Controls.Add(list);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetDiagramTables(list.CheckedItems.Cast<string>().ToList());
            }
        }

        /// <summary>設定目前圖表包含的資料表；新加入的表放在最右側一欄。也供測試直接呼叫。</summary>
        public void SetDiagramTables(IList<string> tables)
        {
            HashSet<string> wanted = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            diagram.Tables.RemoveAll(item => !wanted.Contains(item.Table));
            int x = diagram.Tables.Count == 0 ? ErModelService.Margin : diagram.Tables.Max(item => item.X) + ErModelService.CardWidth + ErModelService.HorizontalGap;
            int y = ErModelService.Margin;
            foreach (string name in tables.Where(name => diagram.Find(name) == null))
            {
                diagram.Tables.Add(new ErModelTablePlacement { Table = name, X = x, Y = y });
                SchemaTableModel table = snapshot.Tables.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                y += ErModelService.CardHeight(table == null ? 0 : table.Columns.Count) + ErModelService.VerticalGap;
            }
            canvas.ReloadLayout();
            MarkDirty();
        }

        private void EditGroups()
        {
            if (document == null) return;
            using (Form dialog = new Form { Text = Localization.T("ErModel.Groups"), Width = 560, Height = 420, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false })
            {
                DataGridView grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    AllowUserToAddRows = true,
                    AllowUserToDeleteRows = true,
                    RowHeadersVisible = true,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    BackgroundColor = SystemColors.Window
                };
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = Localization.T("ErModel.GroupName"), FillWeight = 40 });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", HeaderText = Localization.T("ErModel.GroupColor"), FillWeight = 20 });
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Visible", HeaderText = Localization.T("ErModel.GroupVisible"), FillWeight = 20 });
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Locked", HeaderText = Localization.T("ErModel.GroupLocked"), FillWeight = 20 });
                foreach (ErModelGroup group in document.Groups) grid.Rows.Add(group.Name, group.Color, group.Visible, group.Locked);
                Label error = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.FromArgb(180, 35, 24) };
                Button ok = new Button { Text = Localization.T("Common.OK"), AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dialog.Controls.Add(grid);
                dialog.Controls.Add(error);
                dialog.Controls.Add(buttons);
                dialog.CancelButton = cancel;
                ok.Click += (sender, args) =>
                {
                    List<ErModelGroup> groups = new List<ErModelGroup>();
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        if (row.IsNewRow || string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Name"].Value))) continue;
                        groups.Add(new ErModelGroup
                        {
                            Name = Convert.ToString(row.Cells["Name"].Value).Trim(),
                            Color = string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Color"].Value)) ? "#2563eb" : Convert.ToString(row.Cells["Color"].Value).Trim(),
                            Visible = !(row.Cells["Visible"].Value is bool) || (bool)row.Cells["Visible"].Value,
                            Locked = row.Cells["Locked"].Value is bool && (bool)row.Cells["Locked"].Value
                        });
                    }
                    try
                    {
                        ApplyGroups(groups);
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    catch (InvalidOperationException ex)
                    {
                        error.Text = ex.Message;
                    }
                };
                ThemeManager.ApplyTo(dialog);
                dialog.ShowDialog(this);
            }
        }

        /// <summary>取代群組清單（驗證名稱與顏色；刪除的群組其成員改為不屬於任何群組）。也供測試直接呼叫。</summary>
        public void ApplyGroups(List<ErModelGroup> groups)
        {
            ErModelDocument candidate = new ErModelDocument { Database = document.Database, Groups = groups, Diagrams = document.Diagrams };
            ErModelService.Validate(candidate);
            document.Groups = candidate.Groups;
            canvas.ReloadLayout();
            MarkDirty();
        }

        private void OpenModel()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = Localization.T("ErModel.FileFilter"), Title = Localization.T("ErModel.Open") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    LoadModel(dialog.FileName);
                    statusLabel.Text = Localization.Format("ErModel.Opened", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ExceptionMessageService.GetReason(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveModel()
        {
            if (document == null) return;
            string path = modelPath;
            if (path == null)
            {
                using (SaveFileDialog dialog = new SaveFileDialog
                {
                    Filter = Localization.T("ErModel.FileFilter"),
                    DefaultExt = "punkmodel",
                    AddExtension = true,
                    FileName = MakeSafeFileName(databaseName) + ".punkmodel",
                    Title = Localization.T("ErModel.Save")
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.FileName;
                }
            }
            try
            {
                SaveModelTo(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ExceptionMessageService.GetReason(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportSvg()
        {
            if (!canvas.HasDiagram)
            {
                MessageBox.Show(Localization.T("ErDiagram.NothingToExport"), Localization.T("View.ERDiagram"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "SVG|*.svg",
                DefaultExt = "svg",
                AddExtension = true,
                FileName = MakeSafeFileName(databaseName) + "_" + MakeSafeFileName(diagram.Name) + ".svg",
                Title = Localization.T("ErModel.ExportSvg")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, ErModelService.BuildSvg(snapshot, document, diagram), new System.Text.UTF8Encoding(false));
                    statusLabel.Text = Localization.Format("ErDiagram.Exported", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("ErDiagram.ExportFailed", ExceptionMessageService.GetReason(ex)),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string Prompt(string title, string message, string initial)
        {
            using (Form dialog = new Form { Text = title, Width = 420, Height = 170, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent })
            {
                Label label = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(10, 8, 10, 0), Text = message };
                TextBox input = new TextBox { Dock = DockStyle.Top, Text = initial };
                Panel inputPanel = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 2, 10, 2) };
                inputPanel.Controls.Add(input);
                Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dialog.Controls.Add(inputPanel);
                dialog.Controls.Add(label);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        private void ExportPng()
        {
            if (!canvas.HasDiagram)
            {
                MessageBox.Show(Localization.T("ErDiagram.NothingToExport"), Localization.T("View.ERDiagram"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PNG|*.png",
                DefaultExt = "png",
                AddExtension = true,
                FileName = MakeSafeFileName(databaseName) + "_er_diagram.png",
                Title = Localization.T("ErDiagram.ExportPng")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (Bitmap bitmap = canvas.RenderDiagramToBitmap())
                    {
                        bitmap.Save(dialog.FileName, ImageFormat.Png);
                    }
                    statusLabel.Text = Localization.Format("ErDiagram.Exported", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("ErDiagram.ExportFailed", ExceptionMessageService.GetReason(ex)),
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

    public sealed class ErDiagramCanvas : ScrollableControl
    {
        private readonly List<TableCard> cards = new List<TableCard>();
        private SchemaModelSnapshot snapshot;
        private ErModelDocument document;
        private ErModelDiagram diagram;
        private Size logicalSize = new Size(1, 1);
        private float zoom = 1f;
        private bool panning;
        private Point panStart;
        private Point scrollStart;
        private TableCard dragging;
        private Point dragOffset;

        public ErDiagramCanvas()
        {
            AutoScroll = true;
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        public event EventHandler ZoomChanged;

        /// <summary>拖曳移動資料表後觸發（模型已更新）。</summary>
        public event EventHandler LayoutChanged;

        /// <summary>在資料表上按右鍵；參數為表名與螢幕座標。</summary>
        public event Action<string, Point> TableContextRequested;

        /// <summary>嘗試拖曳鎖定群組中的資料表時觸發。</summary>
        public event Action<string> LockedTableDragAttempted;

        public float Zoom
        {
            get { return zoom; }
        }

        public bool HasDiagram
        {
            get { return snapshot != null && cards.Count > 0; }
        }

        public Size LogicalDiagramSize
        {
            get { return logicalSize; }
        }

        public void ApplyTheme()
        {
            BackColor = ThemeManager.WindowBackColor;
            ForeColor = ThemeManager.TextColor;
            Invalidate();
        }

        /// <summary>以全部資料表的預設模型顯示（依外鍵分層排列）。</summary>
        public void SetSnapshot(SchemaModelSnapshot value)
        {
            ErModelDocument created = ErModelService.CreateDefault(value, Localization.T("ErModel.DefaultDiagram"));
            SetModel(value, created, created.Diagrams[0]);
        }

        public void SetModel(SchemaModelSnapshot value, ErModelDocument model, ErModelDiagram current)
        {
            snapshot = value;
            document = model;
            diagram = current;
            BuildLayout();
            AutoScrollPosition = Point.Empty;
            Invalidate();
        }

        /// <summary>模型改變後（群組、顯示、位置）重新整理畫面。</summary>
        public void ReloadLayout()
        {
            BuildLayout();
            Invalidate();
        }

        public void ZoomBy(float delta)
        {
            SetZoom(zoom + delta);
        }

        public void FitToWindow()
        {
            if (!HasDiagram || logicalSize.Width <= 0 || logicalSize.Height <= 0) return;
            float availableWidth = Math.Max(1, ClientSize.Width - 28);
            float availableHeight = Math.Max(1, ClientSize.Height - 28);
            float fit = Math.Min(availableWidth / logicalSize.Width, availableHeight / logicalSize.Height);
            SetZoom(Math.Min(1f, fit));
            AutoScrollPosition = Point.Empty;
        }

        public Bitmap RenderDiagramToBitmap()
        {
            if (!HasDiagram) throw new InvalidOperationException("Diagram is empty.");
            const float maximumDimension = 8000f;
            const float maximumPixels = 48000000f;
            float dimensionScale = Math.Min(maximumDimension / logicalSize.Width, maximumDimension / logicalSize.Height);
            float pixelScale = (float)Math.Sqrt(maximumPixels / Math.Max(1d, logicalSize.Width * (double)logicalSize.Height));
            float exportScale = Math.Min(1f, Math.Min(dimensionScale, pixelScale));
            int width = Math.Max(1, (int)Math.Ceiling(logicalSize.Width * exportScale));
            int height = Math.Max(1, (int)Math.Ceiling(logicalSize.Height * exportScale));
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(ThemeManager.WindowBackColor);
                graphics.ScaleTransform(exportScale, exportScale);
                DrawDiagram(graphics);
            }
            return bitmap;
        }

        /// <summary>把資料表移到邏輯座標；也供測試模擬拖曳。鎖定群組的表不會移動並回傳 false。</summary>
        public bool MoveTable(string table, int x, int y)
        {
            TableCard card = cards.FirstOrDefault(item => string.Equals(item.Table.Name, table, StringComparison.OrdinalIgnoreCase));
            if (card == null) return false;
            if (ErModelService.IsLocked(document, card.Placement))
            {
                Action<string> locked = LockedTableDragAttempted;
                if (locked != null) locked(card.Table.Name);
                return false;
            }
            card.Placement.X = Math.Max(0, Math.Min(ErModelService.MaximumCoordinate, x));
            card.Placement.Y = Math.Max(0, Math.Min(ErModelService.MaximumCoordinate, y));
            card.UpdateBounds();
            UpdateLogicalSize();
            Invalidate();
            EventHandler changed = LayoutChanged;
            if (changed != null) changed(this, EventArgs.Empty);
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (!HasDiagram)
            {
                using (Brush brush = new SolidBrush(ThemeManager.MutedTextColor))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    e.Graphics.DrawString(Localization.T("ErDiagram.Empty"), Font, brush, ClientRectangle, format);
                }
                return;
            }

            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            e.Graphics.ScaleTransform(zoom, zoom);
            DrawDiagram(e.Graphics);
            e.Graphics.Restore(state);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ZoomBy(e.Delta > 0 ? 0.1f : -0.1f);
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle)
            {
                panning = true;
                panStart = e.Location;
                scrollStart = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
                Cursor = Cursors.Hand;
                Capture = true;
                return;
            }

            Point logical = ToLogical(e.Location);
            TableCard hit = cards.LastOrDefault(card => card.Bounds.Contains(logical));
            if (hit == null) return;
            if (e.Button == MouseButtons.Right)
            {
                Action<string, Point> handler = TableContextRequested;
                if (handler != null) handler(hit.Table.Name, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (ErModelService.IsLocked(document, hit.Placement))
            {
                Action<string> locked = LockedTableDragAttempted;
                if (locked != null) locked(hit.Table.Name);
                return;
            }
            dragging = hit;
            dragOffset = new Point(logical.X - hit.Bounds.X, logical.Y - hit.Bounds.Y);
            cards.Remove(hit);
            cards.Add(hit);
            Cursor = Cursors.SizeAll;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning)
            {
                AutoScrollPosition = new Point(
                    Math.Max(0, scrollStart.X - (e.X - panStart.X)),
                    Math.Max(0, scrollStart.Y - (e.Y - panStart.Y)));
                return;
            }
            if (dragging == null) return;
            Point logical = ToLogical(e.Location);
            dragging.Placement.X = Math.Max(0, logical.X - dragOffset.X);
            dragging.Placement.Y = Math.Max(0, logical.Y - dragOffset.Y);
            dragging.UpdateBounds();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (panning && e.Button == MouseButtons.Middle)
            {
                panning = false;
                Capture = false;
                Cursor = Cursors.Default;
                return;
            }
            if (dragging == null || e.Button != MouseButtons.Left) return;
            dragging = null;
            Capture = false;
            Cursor = Cursors.Default;
            UpdateLogicalSize();
            EventHandler changed = LayoutChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private Point ToLogical(Point client)
        {
            return new Point(
                (int)Math.Round((client.X - AutoScrollPosition.X) / zoom),
                (int)Math.Round((client.Y - AutoScrollPosition.Y) / zoom));
        }

        private void SetZoom(float value)
        {
            float next = Math.Max(0.1f, Math.Min(2f, value));
            if (Math.Abs(next - zoom) < 0.001f) return;
            zoom = next;
            UpdateScrollSize();
            Invalidate();
            EventHandler handler = ZoomChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void BuildLayout()
        {
            cards.Clear();
            if (snapshot != null && diagram != null)
            {
                Dictionary<string, SchemaTableModel> tables = snapshot.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
                foreach (ErModelTablePlacement placement in diagram.Tables)
                {
                    SchemaTableModel table;
                    if (!tables.TryGetValue(placement.Table, out table) || !ErModelService.IsVisible(document, placement)) continue;
                    cards.Add(new TableCard(table, placement));
                }
            }
            UpdateLogicalSize();
        }

        private void UpdateLogicalSize()
        {
            logicalSize = cards.Count == 0
                ? new Size(1, 1)
                : new Size(cards.Max(card => card.Bounds.Right) + ErModelService.Margin, cards.Max(card => card.Bounds.Bottom) + ErModelService.Margin);
            UpdateScrollSize();
        }

        private void UpdateScrollSize()
        {
            AutoScrollMinSize = new Size(
                Math.Max(1, (int)Math.Ceiling(logicalSize.Width * zoom)),
                Math.Max(1, (int)Math.Ceiling(logicalSize.Height * zoom)));
        }

        private void DrawDiagram(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawRelationships(graphics);
            foreach (TableCard card in cards) DrawTable(graphics, card);
        }

        private void DrawRelationships(Graphics graphics)
        {
            if (snapshot == null) return;
            Dictionary<string, TableCard> byName = cards.ToDictionary(card => card.Table.Name, StringComparer.OrdinalIgnoreCase);
            using (Pen pen = new Pen(ThemeManager.AccentColor, 1.6f))
            using (AdjustableArrowCap arrow = new AdjustableArrowCap(3.5f, 5f, true))
            {
                pen.CustomEndCap = arrow;
                foreach (SchemaRelationshipModel relationship in snapshot.Relationships)
                {
                    TableCard from;
                    TableCard to;
                    if (!byName.TryGetValue(relationship.FromTable, out from) || !byName.TryGetValue(relationship.ToTable, out to)) continue;

                    PointF start = GetColumnAnchor(from, relationship.FromColumn, true);
                    PointF end = GetColumnAnchor(to, relationship.ToColumn, false);
                    if (ReferenceEquals(from, to))
                    {
                        float loopX = from.Bounds.Right + 34;
                        graphics.DrawLines(pen, new[]
                        {
                            start,
                            new PointF(loopX, start.Y),
                            new PointF(loopX, end.Y + 18),
                            new PointF(end.X, end.Y + 18),
                            end
                        });
                        continue;
                    }

                    bool leftToRight = from.Bounds.Left <= to.Bounds.Left;
                    start.X = leftToRight ? from.Bounds.Right : from.Bounds.Left;
                    end.X = leftToRight ? to.Bounds.Left : to.Bounds.Right;
                    float middleX = (start.X + end.X) / 2f;
                    graphics.DrawLines(pen, new[]
                    {
                        start,
                        new PointF(middleX, start.Y),
                        new PointF(middleX, end.Y),
                        end
                    });
                }
            }
        }

        private static PointF GetColumnAnchor(TableCard card, string columnName, bool right)
        {
            int index = card.Table.Columns.FindIndex(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 0;
            index = Math.Min(index, ErModelService.MaximumVisibleColumns);
            float y = card.Bounds.Top + ErModelService.HeaderHeight + index * ErModelService.RowHeight + ErModelService.RowHeight / 2f;
            return new PointF(right ? card.Bounds.Right : card.Bounds.Left, y);
        }

        private void DrawTable(Graphics graphics, TableCard card)
        {
            Rectangle bounds = card.Bounds;
            ErModelGroup group = document == null ? null : document.FindGroup(card.Placement.Group);
            Color header = group == null ? ThemeManager.AccentSoftColor : ColorTranslator.FromHtml(ErModelService.Tint(group.Color));
            using (Brush cardBrush = new SolidBrush(ThemeManager.ElevatedColor))
            using (Brush headerBrush = new SolidBrush(header))
            using (Pen borderPen = new Pen(group == null ? ThemeManager.BorderStrongColor : ColorTranslator.FromHtml(group.Color), group == null ? 1f : 1.6f))
            using (Pen rowPen = new Pen(ThemeManager.GridColor))
            using (Brush textBrush = new SolidBrush(group == null ? ThemeManager.TextColor : Color.FromArgb(17, 24, 39)))
            using (Brush bodyTextBrush = new SolidBrush(ThemeManager.TextColor))
            using (Brush mutedBrush = new SolidBrush(ThemeManager.MutedTextColor))
            using (Brush keyBrush = new SolidBrush(ThemeManager.AccentColor))
            using (Font headerFont = new Font(Font, FontStyle.Bold))
            using (Font keyFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f), FontStyle.Bold))
            using (StringFormat headerFormat = EllipsisFormat(StringAlignment.Near))
            using (StringFormat nameFormat = EllipsisFormat(StringAlignment.Near))
            using (StringFormat typeFormat = EllipsisFormat(StringAlignment.Far))
            {
                graphics.FillRectangle(cardBrush, bounds);
                graphics.FillRectangle(headerBrush, new Rectangle(bounds.Left, bounds.Top, bounds.Width, ErModelService.HeaderHeight));
                graphics.DrawRectangle(borderPen, bounds);
                string title = card.Table.Name + (ErModelService.IsLocked(document, card.Placement) ? Localization.T("ErModel.LockedSuffix") : string.Empty);
                graphics.DrawString(title, headerFont, textBrush,
                    new RectangleF(bounds.Left + 12, bounds.Top + 9, bounds.Width - 24, ErModelService.HeaderHeight - 12), headerFormat);

                int visible = Math.Min(ErModelService.MaximumVisibleColumns, card.Table.Columns.Count);
                if (visible == 0)
                {
                    graphics.DrawString(Localization.T("ErDiagram.NoColumns"), Font, mutedBrush,
                        new RectangleF(bounds.Left + 12, bounds.Top + ErModelService.HeaderHeight + 5, bounds.Width - 24, ErModelService.RowHeight), nameFormat);
                }
                for (int index = 0; index < visible; index++)
                {
                    SchemaColumnModel column = card.Table.Columns[index];
                    int rowTop = bounds.Top + ErModelService.HeaderHeight + index * ErModelService.RowHeight;
                    graphics.DrawLine(rowPen, bounds.Left, rowTop, bounds.Right, rowTop);
                    if (column.IsPrimaryKey)
                    {
                        graphics.DrawString("PK", keyFont, keyBrush,
                            new RectangleF(bounds.Left + 9, rowTop + 5, 24, ErModelService.RowHeight - 6), nameFormat);
                    }
                    graphics.DrawString(column.Name, Font, bodyTextBrush,
                        new RectangleF(bounds.Left + 36, rowTop + 4, 142, ErModelService.RowHeight - 6), nameFormat);
                    string typeText = (column.DataType ?? string.Empty) + (column.IsNullable ? " ?" : string.Empty);
                    graphics.DrawString(typeText.Trim(), Font, mutedBrush,
                        new RectangleF(bounds.Left + 174, rowTop + 4, bounds.Width - 184, ErModelService.RowHeight - 6), typeFormat);
                }

                if (card.Table.Columns.Count > ErModelService.MaximumVisibleColumns)
                {
                    int rowTop = bounds.Top + ErModelService.HeaderHeight + visible * ErModelService.RowHeight;
                    graphics.DrawLine(rowPen, bounds.Left, rowTop, bounds.Right, rowTop);
                    graphics.DrawString(Localization.Format("ErDiagram.MoreColumns", card.Table.Columns.Count - ErModelService.MaximumVisibleColumns),
                        Font, mutedBrush, new RectangleF(bounds.Left + 12, rowTop + 4, bounds.Width - 24, ErModelService.RowHeight - 6), nameFormat);
                }
            }
        }

        private static StringFormat EllipsisFormat(StringAlignment alignment)
        {
            return new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        private sealed class TableCard
        {
            public TableCard(SchemaTableModel table, ErModelTablePlacement placement)
            {
                Table = table;
                Placement = placement;
                UpdateBounds();
            }

            public SchemaTableModel Table { get; private set; }
            public ErModelTablePlacement Placement { get; private set; }
            public Rectangle Bounds { get; private set; }

            public void UpdateBounds()
            {
                Bounds = new Rectangle(Placement.X, Placement.Y, ErModelService.CardWidth, ErModelService.CardHeight(Table.Columns.Count));
            }
        }
    }
}

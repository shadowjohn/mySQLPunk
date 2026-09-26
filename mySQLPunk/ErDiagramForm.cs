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
        private readonly ToolStripMenuItem addModelTableItem;
        private readonly ToolStripMenuItem syncModelItem;
        private readonly ToolStripMenuItem detachModelItem;
        private readonly ToolStripMenuItem routinesItem;
        private readonly ToolStripMenuItem dataVaultItem;
        private readonly ToolStripLabel zoomLabel;
        private readonly ToolStripStatusLabel statusLabel;
        private Form1 mainHost;
        private bool loaded;
        private bool switchingDiagram;
        private SchemaModelSnapshot snapshot;
        private SchemaModelSnapshot liveSnapshot;
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
            ToolStripDropDownButton modelMenu = new ToolStripDropDownButton(Localization.T("ErModel.SchemaMenu"));
            ToolStripMenuItem captureItem = new ToolStripMenuItem(Localization.T("ErModel.CaptureSchema"));
            addModelTableItem = new ToolStripMenuItem(Localization.T("ErModel.AddTable"));
            syncModelItem = new ToolStripMenuItem(Localization.T("ErModel.SyncToDatabase"));
            detachModelItem = new ToolStripMenuItem(Localization.T("ErModel.DetachSchema"));
            routinesItem = new ToolStripMenuItem(Localization.T("ErModel.Routines"));
            ToolStripMenuItem suggestRolesItem = new ToolStripMenuItem(Localization.T("ErModel.SuggestRoles"));
            dataVaultItem = new ToolStripMenuItem(Localization.T("ErModel.DataVault"));
            suggestRolesItem.Click += (sender, args) => GuardModel(() => SuggestRoles());
            dataVaultItem.Click += (sender, args) => GuardModel(ShowDataVaultDialog);
            modelMenu.DropDownItems.AddRange(new ToolStripItem[] { captureItem, addModelTableItem, routinesItem, syncModelItem, new ToolStripSeparator(), suggestRolesItem, dataVaultItem, new ToolStripSeparator(), detachModelItem });
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
                modelMenu,
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
            captureItem.Click += (sender, args) => GuardModel(() => CaptureFromDatabase(true));
            addModelTableItem.Click += (sender, args) => EditModelTable(null);
            syncModelItem.Click += (sender, args) => GuardModel(ShowModelSync);
            detachModelItem.Click += (sender, args) => GuardModel(DetachSchema);
            routinesItem.Click += (sender, args) => GuardModel(EditRoutines);
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
            canvas.RouteContextRequested += (key, screen) =>
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                ToolStripMenuItem reset = new ToolStripMenuItem(Localization.T("ErModel.ResetRoute")) { Enabled = diagram != null && diagram.FindRoute(key) != null };
                reset.Click += (sender, args) => canvas.ResetRoute(key);
                menu.Items.Add(reset);
                menu.Closed += (sender, args) => BeginInvoke(new Action(menu.Dispose));
                ThemeManager.ApplyTo(menu);
                menu.Show(screen);
            };

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
                liveSnapshot = SchemaModelService.Load(database, databaseName);
                if (document == null)
                {
                    document = ErModelService.CreateDefault(liveSnapshot, Localization.T("ErModel.DefaultDiagram"));
                    diagram = document.Diagrams[0];
                }
                snapshot = CurrentSnapshot();
                ShowDocument();
                int missing = document.Diagrams.SelectMany(item => item.Tables)
                    .Count(item => !snapshot.Tables.Any(table => string.Equals(table.Name, item.Table, StringComparison.OrdinalIgnoreCase)));
                statusLabel.Text = Localization.Format(
                    snapshot.Warnings.Count == 0 ? "ErDiagram.Status" : "ErDiagram.StatusWithWarnings",
                    snapshot.Tables.Count,
                    snapshot.Relationships.Count,
                    snapshot.Warnings.Count) + (missing > 0 ? " " + Localization.Format("ErModel.MissingTables", missing) : string.Empty) + ModeSuffix();
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
            snapshot = CurrentSnapshot();
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
            Text = Localization.Format("ErDiagram.Title", databaseName) + name + (IsModelFirst ? " · " + Localization.T("ErModel.ModelMode") : string.Empty) + (dirty ? " *" : string.Empty);
            addModelTableItem.Enabled = IsModelFirst;
            syncModelItem.Enabled = IsModelFirst;
            detachModelItem.Enabled = IsModelFirst;
            routinesItem.Enabled = IsModelFirst;
            dataVaultItem.Enabled = IsModelFirst;
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
            ToolStripMenuItem roleMenu = new ToolStripMenuItem(Localization.T("ErModel.Role"));
            string currentRole = ErModelPatternService.RoleOf(document, table);
            ToolStripMenuItem noRole = new ToolStripMenuItem(Localization.T("ErModel.Role.None")) { Checked = currentRole == null };
            noRole.Click += (sender, args) => SetTableRole(table, null);
            roleMenu.DropDownItems.Add(noRole);
            foreach (string role in ErTableRoles.All)
            {
                string current = role;
                ToolStripMenuItem item = new ToolStripMenuItem(Localization.T("ErModel.Role." + role)) { Checked = currentRole == role };
                item.Click += (sender, args) => SetTableRole(table, current);
                roleMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(roleMenu);
            ToolStripMenuItem remove = new ToolStripMenuItem(Localization.T("ErModel.RemoveFromDiagram"));
            remove.Click += (sender, args) =>
            {
                diagram.Tables.RemoveAll(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
                canvas.ReloadLayout();
                MarkDirty();
            };
            menu.Items.Add(remove);
            if (IsModelFirst)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem editTable = new ToolStripMenuItem(Localization.T("ErModel.EditTable"));
                editTable.Click += (sender, args) => EditModelTable(table);
                menu.Items.Add(editTable);
                ToolStripMenuItem dropTable = new ToolStripMenuItem(Localization.T("ErModel.DropTable"));
                dropTable.Click += (sender, args) =>
                {
                    if (MessageBox.Show(this, Localization.Format("ErModel.ConfirmDropTable", table), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    GuardModel(() => DropModelTable(table));
                };
                menu.Items.Add(dropTable);
            }
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

        /// <summary>模型內有結構時（模型優先），圖表畫的是模型而不是資料庫。</summary>
        public bool IsModelFirst { get { return document != null && document.Schema != null; } }

        private SchemaModelSnapshot CurrentSnapshot()
        {
            if (IsModelFirst) return ErModelService.ToSnapshot(document.Schema, databaseName);
            return liveSnapshot;
        }

        private string ModeSuffix()
        {
            return IsModelFirst ? " " + Localization.T("ErModel.ModelModeStatus") : string.Empty;
        }

        private void RefreshModelView()
        {
            snapshot = CurrentSnapshot();
            if (snapshot == null) return;
            canvas.SetModel(snapshot, document, diagram);
            UpdateTitle();
        }

        private void GuardModel(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
                MessageBox.Show(this, statusLabel.Text, Localization.T("ErModel.SchemaMenu"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 資料庫 → 模型：以資料庫目前結構取代模型結構。模型已有結構時先列出差異並確認（confirm 為 false 時直接套用，供測試）。
        /// 資料庫新出現的資料表會加到目前圖表。回傳差異（第一次擷取時為 null）。
        /// </summary>
        public SchemaComparisonResult CaptureFromDatabase(bool confirm)
        {
            SchemaModelSnapshot live = SchemaModelService.Load(database, databaseName);
            List<ErModelRoutine> liveRoutines = LoadRoutines();
            SchemaComparisonResult differences = null;
            if (IsModelFirst)
            {
                differences = SchemaComparisonService.Compare(live, ErModelService.ToSnapshot(document.Schema, databaseName));
                // 以資料庫為目標比較：模型沒有、資料庫有的函式在這裡是「新增到模型」。
                List<RoutineChange> routineChanges = RoutineModelService.Compare(live.ProviderName, liveRoutines, document.Schema.Routines);
                int count = differences.Differences.Count(item => item.Kind != SchemaDifferenceKind.MetadataWarning) + routineChanges.Count;
                if (count == 0)
                {
                    statusLabel.Text = Localization.T("ErModel.CaptureNoChanges");
                    return differences;
                }
                string described = DescribeDifferences(differences);
                if (routineChanges.Count > 0) described += Environment.NewLine + Localization.Format("ErModel.RoutineDifferences", routineChanges.Count);
                if (confirm && MessageBox.Show(this, Localization.Format("ErModel.ConfirmCapture", count, described), Text,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    return differences;
                }
            }
            ErModelSchema captured = ErModelService.CaptureSchema(live);
            captured.Routines = liveRoutines;
            ErModelService.ValidateSchema(captured);
            if (document == null)
            {
                document = ErModelService.CreateDefault(live, Localization.T("ErModel.DefaultDiagram"));
                diagram = document.Diagrams[0];
            }
            // 第一次擷取時圖表本來就依資料庫排好（使用者移除的表不再加回）；之後只把模型沒有的新表加到目前圖表。
            HashSet<string> known = new HashSet<string>(IsModelFirst ? document.Schema.Tables.Select(table => table.Name) : captured.Tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);
            document.Schema = captured;
            liveSnapshot = live;
            List<string> added = captured.Tables.Select(table => table.Name).Where(name => !known.Contains(name)).ToList();
            snapshot = CurrentSnapshot();
            if (added.Count > 0) SetDiagramTables(diagram.Tables.Select(item => item.Table).Concat(added).ToList());
            ShowDocument();
            MarkDirty();
            UpdateTitle();
            statusLabel.Text = Localization.Format("ErModel.Captured", captured.Tables.Count, captured.Relationships.Count) + " " +
                Localization.Format("ErModel.RoutinesCaptured", captured.Routines.Count) + ModeSuffix();
            return differences;
        }

        /// <summary>模型 → 資料庫：以模型為來源、資料庫為目標比較結構。</summary>
        public SchemaComparisonResult CompareModelToDatabase()
        {
            if (!IsModelFirst) throw new InvalidOperationException(Localization.T("ErModel.Error.NoSchema"));
            liveSnapshot = SchemaModelService.Load(database, databaseName);
            return SchemaComparisonService.Compare(ErModelService.ToSnapshot(document.Schema, databaseName), liveSnapshot);
        }

        /// <summary>模型與資料庫的函式／程序差異（讓資料庫跟上模型）。</summary>
        public List<RoutineChange> CompareRoutinesToDatabase()
        {
            if (!IsModelFirst) throw new InvalidOperationException(Localization.T("ErModel.Error.NoSchema"));
            return RoutineModelService.Compare(database.ProviderName, document.Schema.Routines, LoadRoutines());
        }

        private List<ErModelRoutine> LoadRoutines()
        {
            try
            {
                return RoutineModelService.Load(database, databaseName);
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("ErModel.RoutinesUnavailable", ExceptionMessageService.GetReason(ex));
                return new List<ErModelRoutine>();
            }
        }

        /// <summary>產生讓資料庫跟上模型的同步視窗（逐句審核、破壞性變更需確認後才執行）；沒有差異時回傳 null。</summary>
        public SchemaSyncScriptForm CreateModelSyncForm()
        {
            SchemaComparisonResult result = CompareModelToDatabase();
            List<RoutineChange> routineChanges = CompareRoutinesToDatabase();
            string reason;
            bool tableChanges = SchemaSyncScriptService.CanGenerate(result, out reason);
            if (!tableChanges && (routineChanges.Count == 0 || !RoutineModelService.SupportsSync(database.ProviderName)))
            {
                statusLabel.Text = reason;
                return null;
            }
            SchemaSyncScript script = RoutineModelService.Merge(database.ProviderName, tableChanges ? SchemaSyncScriptService.Generate(result) : null, routineChanges);
            statusLabel.Text = script.Summary;
            return new SchemaSyncScriptForm(script, Localization.Format("ErModel.SyncTarget", databaseName, database.ProviderName), databaseName,
                statements => SchemaSyncExecutionService.Execute(database, databaseName, statements));
        }

        private void ShowModelSync()
        {
            using (SchemaSyncScriptForm form = CreateModelSyncForm())
            {
                if (form == null)
                {
                    MessageBox.Show(this, statusLabel.Text, Localization.T("ErModel.SyncToDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                form.ShowDialog(this);
                if (form.ExecutedOnTarget)
                {
                    SchemaComparisonResult after = CompareModelToDatabase();
                    int remaining = after.Differences.Count(item => item.Kind != SchemaDifferenceKind.MetadataWarning) + CompareRoutinesToDatabase().Count;
                    statusLabel.Text = remaining == 0 ? Localization.T("ErModel.InSync") : Localization.Format("ErModel.RemainingDifferences", remaining);
                }
            }
        }

        /// <summary>設定資料表角色（null 為清除）；也供測試直接呼叫。</summary>
        public void SetTableRole(string table, string role)
        {
            if (document == null) return;
            ErModelPatternService.SetRole(document, table, role);
            canvas.ReloadLayout();
            MarkDirty();
        }

        /// <summary>依名稱與結構推測事實／維度或 Data Vault 角色，只補上還沒有角色的資料表；回傳新增的數量。</summary>
        public int SuggestRoles()
        {
            if (snapshot == null) return 0;
            int added = 0;
            foreach (KeyValuePair<string, string> pair in ErModelPatternService.SuggestRoles(snapshot))
            {
                if (ErModelPatternService.RoleOf(document, pair.Key) != null) continue;
                ErModelPatternService.SetRole(document, pair.Key, pair.Value);
                added++;
            }
            if (added > 0)
            {
                canvas.ReloadLayout();
                MarkDirty();
            }
            statusLabel.Text = Localization.Format("ErModel.RolesSuggested", added);
            return added;
        }

        /// <summary>把資料表轉成 Data Vault 2.0 結構並開啟新的圖表；也供測試直接呼叫。</summary>
        public DataVaultResult GenerateDataVault(IList<string> tables)
        {
            DataVaultResult result = ErModelPatternService.AddDataVault(document, tables, "Data Vault");
            if (result.Hubs.Count + result.Links.Count + result.Satellites.Count > 0)
            {
                diagram = document.Diagrams.Last();
                snapshot = CurrentSnapshot();
                ShowDocument();
                MarkDirty();
                FitWhenReady();
            }
            statusLabel.Text = Localization.Format("ErModel.DataVault.Done", result.Hubs.Count, result.Links.Count, result.Satellites.Count) +
                (result.Skipped.Count > 0 ? " " + string.Join(" ", result.Skipped) : string.Empty);
            return result;
        }

        private void ShowDataVaultDialog()
        {
            if (!IsModelFirst) return;
            using (Form dialog = new Form { Text = Localization.T("ErModel.DataVault"), Width = 460, Height = 560, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false })
            {
                Label hint = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 8, 8, 0), Text = Localization.T("ErModel.DataVault.Hint") };
                CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
                foreach (ErModelTable table in document.Schema.Tables.Where(item => ErModelPatternService.RoleOf(document, item.Name) == null))
                {
                    list.Items.Add(table.Name, table.Columns.Any(column => column.PrimaryKey));
                }
                Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                dialog.Controls.Add(list);
                dialog.Controls.Add(hint);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                GenerateDataVault(list.CheckedItems.Cast<string>().ToList());
            }
        }

        private void EditRoutines()
        {
            if (!IsModelFirst) return;
            using (ErRoutinesForm form = new ErRoutinesForm(document.Schema.Routines, database.ProviderName))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                ApplyRoutines(form.Result);
            }
        }

        /// <summary>取代模型中的函式／程序清單；也供測試直接呼叫。</summary>
        public void ApplyRoutines(IList<ErModelRoutine> routines)
        {
            List<ErModelRoutine> copy = routines.Select(item => new ErModelRoutine { Name = item.Name, Kind = item.Kind, ReturnType = item.ReturnType, Definition = item.Definition }).ToList();
            RoutineModelService.Validate(copy);
            document.Schema.Routines = copy;
            MarkDirty();
            statusLabel.Text = Localization.Format("ErModel.RoutinesUpdated", copy.Count);
        }

        private void DetachSchema()
        {
            if (!IsModelFirst) return;
            if (MessageBox.Show(this, Localization.T("ErModel.ConfirmDetach"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            document.Schema = null;
            if (liveSnapshot == null) liveSnapshot = SchemaModelService.Load(database, databaseName);
            RefreshModelView();
            MarkDirty();
            statusLabel.Text = Localization.T("ErModel.Detached");
        }

        private static string DescribeDifferences(SchemaComparisonResult result)
        {
            List<string> lines = result.Differences
                .Where(item => item.Kind != SchemaDifferenceKind.MetadataWarning)
                .Take(15)
                .Select(item => "• " + SchemaComparisonService.GetKindDisplayName(item.Kind) + ": " + item.ObjectName +
                                (string.IsNullOrEmpty(item.DetailName) ? string.Empty : "." + item.DetailName))
                .ToList();
            int more = result.Differences.Count(item => item.Kind != SchemaDifferenceKind.MetadataWarning) - lines.Count;
            if (more > 0) lines.Add(Localization.Format("ErModel.MoreDifferences", more));
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>新增（original 為 null）或修改模型中的資料表；也供測試直接呼叫。回傳被移除的外鍵數。</summary>
        public int ApplyModelTable(string original, ErModelTable table, IList<ErModelRelationship> outgoing)
        {
            int dropped = ErModelService.ReplaceTable(document, original, table, outgoing);
            if (original == null && diagram.Find(table.Name) == null)
            {
                snapshot = CurrentSnapshot();
                SetDiagramTables(diagram.Tables.Select(item => item.Table).Concat(new[] { table.Name }).ToList());
            }
            RefreshModelView();
            MarkDirty();
            statusLabel.Text = Localization.Format(original == null ? "ErModel.TableAdded" : "ErModel.TableUpdated", table.Name) +
                (dropped > 0 ? " " + Localization.Format("ErModel.RelationshipsDropped", dropped) : string.Empty);
            return dropped;
        }

        public void DropModelTable(string table)
        {
            ErModelService.DropTable(document, table);
            RefreshModelView();
            MarkDirty();
            statusLabel.Text = Localization.Format("ErModel.TableDropped", table);
        }

        private void EditModelTable(string tableName)
        {
            if (!IsModelFirst) return;
            ErModelTable existing = tableName == null ? null : document.Schema.Find(tableName);
            if (tableName != null && existing == null) return;
            List<ErModelRelationship> outgoing = existing == null
                ? new List<ErModelRelationship>()
                : document.Schema.Relationships.Where(item => string.Equals(item.FromTable, existing.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            using (ErModelTableEditorForm editor = new ErModelTableEditorForm(existing, outgoing, document.Schema.Tables.Select(item => item.Name).ToList(),
                       (table, relationships) => ApplyModelTable(existing == null ? null : existing.Name, table, relationships)))
            {
                editor.ShowDialog(this);
            }
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
        private readonly List<RouteSegment> segments = new List<RouteSegment>();
        private string routeDragging;

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

        /// <summary>在連接線上按右鍵；參數為連接線鍵值與螢幕座標。</summary>
        public event Action<string, Point> RouteContextRequested;

        /// <summary>目前畫出的連接線鍵值（測試用）。</summary>
        public IList<string> RouteKeys { get { return segments.Select(item => item.Key).ToList(); } }

        /// <summary>以邏輯座標找出可拖曳的連接線垂直段落。</summary>
        public string RouteHitTest(Point logical)
        {
            float tolerance = 5f / Math.Max(0.1f, zoom);
            RouteSegment hit = segments.LastOrDefault(item => Math.Abs(logical.X - item.X) <= tolerance && logical.Y >= item.Top - tolerance && logical.Y <= item.Bottom + tolerance);
            return hit == null ? null : hit.Key;
        }

        /// <summary>把連接線的垂直段落移到指定 X；也供測試直接呼叫。</summary>
        public bool MoveRoute(string key, int x)
        {
            if (diagram == null || string.IsNullOrEmpty(key)) return false;
            ErModelRoute route = diagram.FindRoute(key);
            if (route == null)
            {
                route = new ErModelRoute { Key = key };
                diagram.Routes.Add(route);
            }
            route.X = Math.Max(0, Math.Min(ErModelService.MaximumCoordinate, x));
            Invalidate();
            EventHandler changed = LayoutChanged;
            if (changed != null) changed(this, EventArgs.Empty);
            return true;
        }

        /// <summary>取消手動調整，改回自動路徑。</summary>
        public bool ResetRoute(string key)
        {
            if (diagram == null || diagram.Routes.RemoveAll(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Invalidate();
            EventHandler changed = LayoutChanged;
            if (changed != null) changed(this, EventArgs.Empty);
            return true;
        }

        /// <summary>重新計算連接線（不繪製），供測試在視窗尚未顯示時取得段落。</summary>
        public void MeasureRoutes()
        {
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                DrawRelationships(graphics);
            }
        }

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
            if (hit == null)
            {
                string routeKey = RouteHitTest(logical);
                if (routeKey == null) return;
                if (e.Button == MouseButtons.Right)
                {
                    Action<string, Point> routeHandler = RouteContextRequested;
                    if (routeHandler != null) routeHandler(routeKey, PointToScreen(e.Location));
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                routeDragging = routeKey;
                Capture = true;
                Cursor = Cursors.SizeWE;
                return;
            }
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
            if (routeDragging != null)
            {
                ErModelRoute route = diagram.FindRoute(routeDragging);
                if (route == null)
                {
                    route = new ErModelRoute { Key = routeDragging };
                    diagram.Routes.Add(route);
                }
                route.X = Math.Max(0, Math.Min(ErModelService.MaximumCoordinate, ToLogical(e.Location).X));
                Invalidate();
                return;
            }
            if (dragging == null)
            {
                Cursor = e.Button == MouseButtons.None && !cards.Any(card => card.Bounds.Contains(ToLogical(e.Location))) && RouteHitTest(ToLogical(e.Location)) != null
                    ? Cursors.SizeWE
                    : Cursors.Default;
                return;
            }
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
            if (routeDragging != null && e.Button == MouseButtons.Left)
            {
                routeDragging = null;
                Capture = false;
                Cursor = Cursors.Default;
                EventHandler routed = LayoutChanged;
                if (routed != null) routed(this, EventArgs.Empty);
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
            segments.Clear();
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
                    string key = ErModelService.RouteKey(relationship);
                    ErModelRoute route = diagram == null ? null : diagram.FindRoute(key);
                    if (ReferenceEquals(from, to))
                    {
                        float loopX = route != null ? route.X : from.Bounds.Right + 34;
                        segments.Add(new RouteSegment { Key = key, X = loopX, Top = Math.Min(start.Y, end.Y + 18), Bottom = Math.Max(start.Y, end.Y + 18) });
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
                    float middleX = route != null ? route.X : (start.X + end.X) / 2f;
                    segments.Add(new RouteSegment { Key = key, X = middleX, Top = Math.Min(start.Y, end.Y), Bottom = Math.Max(start.Y, end.Y) });
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
            string role = ErModelPatternService.RoleOf(document, card.Table.Name);
            string roleColor = ErTableRoles.Color(role);
            Color header = group != null ? ColorTranslator.FromHtml(ErModelService.Tint(group.Color))
                : roleColor != null ? ColorTranslator.FromHtml(ErModelService.Tint(roleColor))
                : ThemeManager.AccentSoftColor;
            using (Brush cardBrush = new SolidBrush(ThemeManager.ElevatedColor))
            using (Brush headerBrush = new SolidBrush(header))
            using (Pen borderPen = new Pen(group == null ? ThemeManager.BorderStrongColor : ColorTranslator.FromHtml(group.Color), group == null ? 1f : 1.6f))
            using (Pen rowPen = new Pen(ThemeManager.GridColor))
            using (Brush textBrush = new SolidBrush(group == null && roleColor == null ? ThemeManager.TextColor : Color.FromArgb(17, 24, 39)))
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
                string badge = ErTableRoles.Badge(role);
                int badgeWidth = 0;
                if (badge != null)
                {
                    badgeWidth = 40;
                    using (Brush badgeBrush = new SolidBrush(ColorTranslator.FromHtml(roleColor)))
                    using (Brush badgeText = new SolidBrush(Color.White))
                    using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        RectangleF badgeBounds = new RectangleF(bounds.Right - 48, bounds.Top + 10, 38, 18);
                        graphics.FillRectangle(badgeBrush, badgeBounds);
                        graphics.DrawString(badge, keyFont, badgeText, badgeBounds, centered);
                    }
                }
                graphics.DrawString(title, headerFont, textBrush,
                    new RectangleF(bounds.Left + 12, bounds.Top + 9, bounds.Width - 24 - badgeWidth, ErModelService.HeaderHeight - 12), headerFormat);

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

        private sealed class RouteSegment
        {
            public string Key;
            public float X;
            public float Top;
            public float Bottom;
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

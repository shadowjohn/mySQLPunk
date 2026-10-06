using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ReportExportWizardForm : Form
    {
        private readonly Dictionary<string, object> captured;
        private readonly string databaseName;
        private readonly HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
        private List<SchemaReportTableInfo> tables = new List<SchemaReportTableInfo>();
        private readonly Panel[] pages = new Panel[3];
        private readonly Label stepLabel = new Label(), selectionLabel = new Label(), summary = new Label(), status = new Label();
        private readonly TextBox search = new TextBox(), title = new TextBox(), author = new TextBox(), output = new TextBox();
        private readonly CheckedListBox tableList = new CheckedListBox();
        private readonly ComboBox format = new ComboBox();
        private readonly FlowLayoutPanel cards = new FlowLayoutPanel();
        private readonly Button back = new Button(), next = new Button(), cancel = new Button(), openFile = new Button(), openFolder = new Button();
        private readonly Button toggleSound = new Button();
        private TableLayoutPanel confirmationFields;
        private readonly ProgressBar progress = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private CancellationTokenSource cancellation;
        private Task worker;
        private bool busy, closeRequested, rebuilding, loaded, succeeded;
        private string completedOutputPath;
        private int step;
        private string templateId;
        private string lastStage = "";
        private readonly Dictionary<ReportExportFormat, string> templateChoices = new Dictionary<ReportExportFormat, string>();
        private readonly List<Image> thumbnails = new List<Image>();
        private static string T(string zh, string en) { return ReportText.Get(zh, en); }
        public ReportExportWizardForm(Dictionary<string, object> connection, string databaseName)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            this.databaseName = databaseName;
            captured = new Dictionary<string, object>();
            // Settings are scalar values; never retain live providers or runtime/session objects.
            foreach (var item in connection)
                if (item.Key != "pdo" && (item.Value == null || item.Value is string || item.Value.GetType().IsValueType)) captured[item.Key] = item.Value;
            captured.Remove("isConnect"); captured.Remove("connString");
            captured["initial_database"] = databaseName;
            Name = "reportExportWizard"; Text = T("匯出資料庫結構報表", "Export database schema report");
            Font = UiKit.Body; AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1040, 780); MinimumSize = new Size(800, 640);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);
            stepLabel.Dock = DockStyle.Fill; stepLabel.Font = UiKit.Subtitle; root.Controls.Add(stepLabel, 0, 0);
            var body = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(body, 0, 1);
            for (int i = 0; i < 3; i++) { pages[i] = new Panel { Dock = DockStyle.Fill, Name = "reportStep" + (i + 1) }; body.Controls.Add(pages[i]); }
            BuildTables(); BuildTemplates(); BuildConfirmation();
            var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            state.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); state.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            root.Controls.Add(state, 0, 2);
            status.Name = "reportStatus"; status.Dock = DockStyle.Fill; status.AutoEllipsis = true; state.Controls.Add(status, 0, 0);
            progress.Name = "reportProgress"; progress.Dock = DockStyle.Fill; state.Controls.Add(progress, 0, 1);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            root.Controls.Add(footer, 0, 3);
            Configure(cancel, "reportCancel", T("取消", "Cancel"), (s, e) => { if (busy) RequestCancel(); else Close(); });
            Configure(next, "reportNext", T("下一步 →", "Next →"), async (s, e) => { if (!loaded || tables.Count == 0) await LoadTablesAsync(); else if (step < 2) { ResetResult(); step++; ShowStep(); } else await ExportAsync(); });
            Configure(back, "reportBack", T("← 上一步", "← Back"), (s, e) => { ResetResult(); step--; ShowStep(); });
            Configure(openFile, "reportOpenFile", T("開啟檔案", "Open file"), (s, e) => OpenPath(completedOutputPath));
            Configure(openFolder, "reportOpenFolder", T("開啟資料夾", "Open folder"), (s, e) => OpenPath(Path.GetDirectoryName(completedOutputPath)));
            footer.Controls.AddRange(new Control[] { cancel, next, back, openFolder, openFile });
            openFile.Visible = openFolder.Visible = false; AcceptButton = next; CancelButton = cancel;
            Shown += (s, e) => {
                Rectangle working = Screen.FromControl(Owner ?? this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, working.Width), Math.Min(MinimumSize.Height, working.Height));
                Size = new Size(Math.Min(Width, working.Width), Math.Min(Height, working.Height));
                Location = new Point(Math.Max(working.Left, Math.Min(Left, working.Right - Width)), Math.Max(working.Top, Math.Min(Top, working.Bottom - Height)));
            };
            Shown += async (s, e) => await LoadTablesAsync();
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; closeRequested = true; RequestCancel(); } };
            ThemeManager.ApplyTo(this); ShowStep();
        }
        private static void Configure(Button button, string name, string text, EventHandler click)
        { button.Name = name; button.Text = text; button.AccessibleName = text; button.AutoSize = true; button.MinimumSize = new Size(100, 34); button.Click += click; }
        private void BuildTables()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pages[0].Controls.Add(layout);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
            search.Name = "reportTableSearch"; search.Width = 260; search.AccessibleName = T("搜尋資料表", "Search tables");
            tools.Controls.Add(new Label { Text = T("搜尋資料表", "Search tables"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); tools.Controls.Add(search);
            var all = new Button(); var none = new Button();
            Configure(all, "reportSelectAll", T("全選（全部資料表）", "Select all tables"), (s, e) => { foreach (var table in tables) selected.Add(table.Name); ResetResult(); RefreshTables(); });
            Configure(none, "reportSelectNone", T("全部取消", "Clear all tables"), (s, e) => { selected.Clear(); ResetResult(); RefreshTables(); });
            tools.Controls.Add(all); tools.Controls.Add(none); layout.Controls.Add(tools, 0, 0);
            tableList.Name = "reportTableList"; tableList.Dock = DockStyle.Fill; tableList.CheckOnClick = true; tableList.IntegralHeight = false; tableList.HorizontalScrollbar = true; tableList.AccessibleName = T("選取資料表", "Select tables");
            tableList.ItemCheck += (s, e) => { if (rebuilding) return; string name = ((SchemaReportTableInfo)tableList.Items[e.Index]).Name; if (e.NewValue == CheckState.Checked) selected.Add(name); else selected.Remove(name); ResetResult(); UpdateSelection(); };
            search.TextChanged += (s, e) => RefreshTables(); layout.Controls.Add(tableList, 0, 1);
            selectionLabel.Name = "reportSelectionSummary"; selectionLabel.Dock = DockStyle.Fill; layout.Controls.Add(selectionLabel, 0, 2);
        }
        private void RefreshTables()
        {
            rebuilding = true; tableList.BeginUpdate();
            try { tableList.Items.Clear(); foreach (var table in tables) if (table.Name.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0 || (table.Comment ?? "").IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0) tableList.Items.Add(table, selected.Contains(table.Name)); }
            finally { tableList.EndUpdate(); rebuilding = false; }
            UpdateSelection();
        }
        private void UpdateSelection()
        { selectionLabel.Text = T("已選 ", "Selected ") + selected.Count + " / " + tables.Count + (selected.Count == 0 ? T("；請至少選擇一張資料表", "; select at least one table") : ""); UpdateButtons(); }
        private ReportExportFormat CurrentFormat { get { return (ReportExportFormat)Math.Max(0, format.SelectedIndex); } }
        private string Extension { get { return CurrentFormat.ToString().ToLowerInvariant(); } }
        private void BuildTemplates()
        {
            format.Name = "reportFormat"; format.DropDownStyle = ComboBoxStyle.DropDownList; format.Items.AddRange(new object[] { "DOCX", "Excel (.xlsx)", "PDF" }); format.AccessibleName = T("報表格式", "Report format");
            format.Dock = DockStyle.Top; pages[1].Controls.Add(cards); pages[1].Controls.Add(format);
            cards.Dock = DockStyle.Fill; cards.AutoScroll = true; cards.WrapContents = true; cards.Padding = new Padding(0, 12, 0, 0); cards.Name = "reportTemplateCards";
            format.SelectedIndexChanged += (s, e) => { BuildCards(); if (!string.IsNullOrWhiteSpace(output.Text)) output.Text = Path.ChangeExtension(output.Text, Extension); ResetResult(); UpdateButtons(); };
            format.SelectedIndex = 0;
        }
        private void BuildCards()
        {
            foreach (Control child in cards.Controls.Cast<Control>().ToArray()) child.Dispose(); cards.Controls.Clear();
            foreach (Image image in thumbnails) image.Dispose(); thumbnails.Clear();
            var templates = ReportTemplateCatalog.ForFormat(CurrentFormat).ToList();
            string previous; templateId = templateChoices.TryGetValue(CurrentFormat, out previous) ? previous : templates[0].Id;
            foreach (ReportTemplate template in templates)
            {
                var card = new TableLayoutPanel { Name = "template_" + template.Id, Width = 390, Height = 310, ColumnCount = 1, RowCount = 4, Padding = new Padding(10), Margin = new Padding(8), BorderStyle = BorderStyle.FixedSingle };
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                var choice = new RadioButton { Name = "select_" + template.Id, Text = template.Name, Checked = template.Id == templateId, Dock = DockStyle.Fill, AutoSize = true };
                choice.CheckedChanged += (s, e) => { if (!choice.Checked) return; templateId = template.Id; templateChoices[CurrentFormat] = templateId; ResetResult(); foreach (Control other in cards.Controls) foreach (RadioButton radio in other.Controls.OfType<RadioButton>()) if (radio != choice) radio.Checked = false; HighlightCards(); };
                card.Controls.Add(choice, 0, 0);
                var picture = new PictureBox { Name = "preview_" + template.Id, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand, AccessibleName = T("放大模板預覽：", "Enlarge template: ") + template.Name, TabStop = true };
                string imagePath = Path.Combine(AssetRoot, template.SampleStem + ".png");
                if (File.Exists(imagePath)) { using (Image original = Image.FromFile(imagePath)) picture.Image = new Bitmap(original); thumbnails.Add(picture.Image); }
                picture.Click += (s, e) => Enlarge(template); picture.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Enlarge(template); };
                card.Controls.Add(picture, 0, 1); card.Controls.Add(new Label { Text = template.Description, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
                var sample = new Button(); Configure(sample, "sample_" + template.Id, T("開啟範例", "Open sample"), (s, e) => OpenPath(Path.Combine(AssetRoot, template.SampleStem + "." + Extension))); card.Controls.Add(sample, 0, 3);
                cards.Controls.Add(card);
            }
            ThemeManager.ApplyTo(cards); HighlightCards();
        }
        private void HighlightCards() { foreach (Control card in cards.Controls) card.BackColor = card.Controls.OfType<RadioButton>().Any(r => r.Checked) ? ThemeManager.AccentSoftColor : ThemeManager.ElevatedColor; }
        private static string AssetRoot { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "report-templates"); } }
        private void Enlarge(ReportTemplate template)
        {
            string previewPath = Path.Combine(AssetRoot, template.SampleStem + "-preview.png");
            if (!File.Exists(previewPath)) previewPath = Path.Combine(AssetRoot, template.SampleStem + ".png");
            if (!File.Exists(previewPath)) return;
            using (var preview = new ReportTemplatePreviewForm(previewPath, template.Name)) preview.ShowDialog(this);
        }
        private void BuildConfirmation()
        {
            var confirmation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            confirmation.RowStyles.Add(new RowStyle(SizeType.AutoSize)); confirmation.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); pages[2].Controls.Add(confirmation);
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 5, Padding = new Padding(4) };
            confirmationFields = layout;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); confirmation.Controls.Add(layout, 0, 0);
            summary.Name = "reportConfirmationSummary"; summary.AutoSize = true; summary.Dock = DockStyle.Fill; summary.Padding = new Padding(0, 0, 0, 20); layout.Controls.Add(summary, 0, 0); layout.SetColumnSpan(summary, 2);
            title.Name = "reportTitle"; title.Text = databaseName + T(" 資料庫結構報表", " Database Schema Report"); author.Name = "reportAuthor"; output.Name = "reportOutputPath"; output.ReadOnly = true;
            AddField(layout, 1, T("文件標題", "Document title"), title); AddField(layout, 2, T("作者", "Author"), author); AddField(layout, 3, T("輸出路徑", "Output path"), output);
            var browse = new Button(); Configure(browse, "reportBrowse", T("選擇儲存位置…", "Choose destination…"), (s, e) => ChooseOutput()); layout.Controls.Add(browse, 1, 4);
            output.TextChanged += (s, e) => { ResetResult(); UpdateButtons(); };
            title.TextChanged += (s, e) => ResetResult(); author.TextChanged += (s, e) => ResetResult();
            BuildCompanion(confirmation);
        }
        private void BuildCompanion(TableLayoutPanel confirmation)
        {
            var space = new Panel { Name = "reportCompanionArea", Dock = DockStyle.Fill, Margin = new Padding(0) };
            var companion = new Panel { Name = "reportCompanion", Margin = new Padding(0) };
            var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            punky.Dock = DockStyle.Fill; companion.Controls.Add(punky);
            Configure(toggleSound, "reportToggleSound", "", (s, e) => { punky.Muted = !punky.Muted; UpdateSoundButton(); });
            toggleSound.AccessibleDescription = T("每次匯出只播放第一輪聲音；取消靜音會在下次匯出生效。", "Audio plays only on the first cycle; unmuting takes effect on the next export.");
            controls.Controls.Add(toggleSound); companion.Controls.Add(controls);
            space.Controls.Add(companion); confirmation.Controls.Add(space, 0, 1);
            space.Resize += (s, e) => {
                int buttonHeight = controls.Height;
                int side = Math.Max(0, Math.Min(384, Math.Min(space.ClientSize.Width, space.ClientSize.Height - buttonHeight)));
                int width = Math.Min(space.ClientSize.Width, Math.Max(side, controls.PreferredSize.Width));
                companion.SetBounds(Math.Max(0, space.ClientSize.Width - width - 12), Math.Max(0, (space.ClientSize.Height - side - buttonHeight) / 2), width, side + buttonHeight);
            };
            UpdateSoundButton();
        }
        private void ResetResult()
        {
            if (!succeeded) return;
            succeeded = false; completedOutputPath = null; progress.Value = 0;
            status.Text = T("僅匯出資料庫結構，不包含資料列。", "Exports schema only; no data rows.");
            UpdateButtons();
        }
        private static void AddField(TableLayoutPanel layout, int row, string text, TextBox input) { layout.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 0, row); input.Dock = DockStyle.Fill; input.AccessibleName = text; layout.Controls.Add(input, 1, row); }
        private void ChooseOutput()
        {
            using (var dialog = new SaveFileDialog { Filter = Extension.ToUpperInvariant() + " (*." + Extension + ")|*." + Extension, DefaultExt = Extension, AddExtension = true, OverwritePrompt = false, FileName = string.IsNullOrEmpty(output.Text) ? SafeName(databaseName) + "." + Extension : Path.GetFileName(output.Text) })
            { if (!string.IsNullOrEmpty(output.Text)) dialog.InitialDirectory = Path.GetDirectoryName(output.Text); if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = Path.ChangeExtension(dialog.FileName, Extension); }
        }
        private static string SafeName(string name) { foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name; }
        private void ShowStep()
        {
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == step;
            pages[step].BringToFront(); stepLabel.Text = databaseName + " — " + T("資料庫結構報表", "Database schema report") + Environment.NewLine + (step + 1) + " / 3  " + new[] { T("選擇資料表", "Select tables"), T("格式與模板", "Format and template"), T("確認輸出", "Confirm output") }[step];
            summary.Text = databaseName + Environment.NewLine + T("資料表：", "Tables: ") + selected.Count + "   " + format.Text + "   " + ReportTemplateCatalog.Find(templateId).Name;
            UpdateButtons();
        }
        private void UpdateButtons()
        {
            back.Enabled = !busy && step > 0; next.Enabled = !busy && (((!loaded || tables.Count == 0) && step == 0) || (loaded && selected.Count > 0 && (step != 2 || !string.IsNullOrWhiteSpace(output.Text))));
            next.Text = !loaded || tables.Count == 0 ? T("重新讀取資料表", "Retry loading tables") : step == 2 ? T("開始匯出", "Start export") : T("下一步 →", "Next →");
            cancel.Text = busy && cancellation != null && cancellation.IsCancellationRequested ? T("正在取消…", "Cancelling…") : succeeded ? T("完成", "Done") : T("取消", "Cancel");
            cancel.Enabled = !busy || cancellation == null || !cancellation.IsCancellationRequested;
            pages[0].Enabled = pages[1].Enabled = !busy;
            if (confirmationFields != null) confirmationFields.Enabled = !busy;
            openFile.Visible = openFolder.Visible = succeeded && !busy;
        }
        private Func<IDatabase> Factory { get { return () => ConnectionOpenService.Open(captured, false).Database; } }
        private void UpdateSoundButton()
        {
            toggleSound.Text = !punky.AudioAvailable ? T("無音效", "No audio") : punky.Muted ? T("取消靜音", "Unmute") : T("靜音", "Mute");
            toggleSound.AccessibleName = toggleSound.Text; toggleSound.Enabled = punky.AudioAvailable;
        }
        private static string StageText(string stage)
        {
            switch (stage)
            {
                case "collect": return T("收集結構資料", "Collect schema");
                case "render": return T("排版產生文件", "Render document");
                case "validate": return T("寫入並驗證輸出檔", "Write and validate output");
                default: return stage;
            }
        }
        private void SetBusy(bool value) { busy = value; if (!value) punky.EndExport(); UpdateSoundButton(); UpdateButtons(); }
        private void RequestCancel() { if (cancellation == null) return; cancellation.Cancel(); punky.EndExport(); UpdateSoundButton(); status.Text = T("正在取消；等待目前的資料庫查詢或寫入完成…", "Cancelling; waiting for the current query or write to finish…"); UpdateButtons(); }
        private async Task LoadTablesAsync()
        {
            cancellation = new CancellationTokenSource(); SetBusy(true); progress.Style = ProgressBarStyle.Marquee; status.Text = T("正在讀取資料表…", "Loading tables…");
            try { worker = Task.Run(() => SchemaReportExportService.ListTableDetails(Factory, databaseName, cancellation.Token)); tables = await (Task<List<SchemaReportTableInfo>>)worker; selected.Clear(); foreach (var table in tables) selected.Add(table.Name); loaded = true; RefreshTables(); status.Text = tables.Count == 0 ? T("目前沒有可匯出的資料表，可重新讀取。", "No exportable tables found; retry loading if needed.") : T("僅匯出資料庫結構，不包含資料列。", "Exports schema only; no data rows."); }
            catch (OperationCanceledException) { status.Text = T("已取消讀取。可按重新讀取再試。", "Loading cancelled. Click Retry to load again."); }
            catch (Exception ex) { status.Text = T("無法讀取資料表：", "Unable to load tables: ") + ex.Message; }
            finally { FinishWork(); }
        }
        private async Task ExportAsync()
        {
            if (busy) return;
            bool overwrite = File.Exists(output.Text);
            if (overwrite && MessageBox.Show(this, T("檔案已存在，是否覆寫？", "The file already exists. Overwrite?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var request = new ReportExportRequest { DatabaseName = databaseName, EngineName = captured.ContainsKey("db_kind") ? Convert.ToString(captured["db_kind"]) : "", Title = title.Text, Author = author.Text, TemplateId = templateId, OutputPath = output.Text, Tables = tables.Where(table => selected.Contains(table.Name)).Select(table => table.Name).ToList(), Overwrite = overwrite };
            cancellation = new CancellationTokenSource(); lastStage = T("開始匯出", "Starting export"); succeeded = false; completedOutputPath = null; SetBusy(true); punky.BeginExport(); UpdateSoundButton(); progress.Value = 0; progress.Style = ProgressBarStyle.Marquee; status.Text = T("正在開始匯出…", "Starting export…");
            var operationCancellation = cancellation;
            var reporter = new Progress<ReportExportProgress>(p => { if (!busy || IsDisposed || cancellation != operationCancellation || operationCancellation.IsCancellationRequested) return; lastStage = StageText(p.Stage); status.Text = lastStage + ": " + p.Message; progress.Style = p.IsIndeterminate || p.Total <= 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; if (progress.Style == ProgressBarStyle.Continuous) progress.Value = Math.Min(99, Math.Max(0, (int)(100L * p.Completed / p.Total))); });
            try { worker = Task.Run(() => SchemaReportExportService.Run(Factory, request, cancellation.Token, reporter)); await worker; completedOutputPath = request.OutputPath; succeeded = true; progress.Style = ProgressBarStyle.Continuous; progress.Value = 100; status.Text = T("匯出完成，輸出檔已驗證。", "Export complete; output verified."); }
            catch (OperationCanceledException) { status.Text = T("已取消匯出。", "Export cancelled."); }
            catch (Exception ex) { status.Text = T("匯出失敗（", "Export failed (") + lastStage + "): " + ex.Message; }
            finally { FinishWork(); }
        }
        private void FinishWork() { progress.Style = ProgressBarStyle.Continuous; cancellation.Dispose(); cancellation = null; worker = null; SetBusy(false); if (closeRequested) Close(); }
        private void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        protected override void Dispose(bool disposing) { if (disposing) { foreach (Image image in thumbnails) image.Dispose(); thumbnails.Clear(); } base.Dispose(disposing); }
    }
}

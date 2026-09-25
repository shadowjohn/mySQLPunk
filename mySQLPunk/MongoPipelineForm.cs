using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// MongoDB Aggregation Pipeline 設計器：以範本新增 stage、調整順序與啟用、逐 stage 編輯 JSON，並預覽「到此 stage 為止」
    /// 的輸出（只取前 N 筆）。只允許唯讀 stage；可複製 mongosh 語法或送到查詢視窗執行。
    /// </summary>
    public sealed class MongoPipelineForm : Form
    {
        private static readonly int[] PreviewSizes = { 20, 100, 500 };

        private readonly my_mongodb database;
        private readonly string databaseName;
        private readonly string collectionName;
        private readonly List<MongoPipelineStage> stages = new List<MongoPipelineStage>();
        private readonly CheckedListBox stageList;
        private readonly ComboBox templateBox;
        private readonly TextBox stageEditor;
        private readonly Label stageError;
        private readonly TextBox previewBox;
        private readonly ComboBox previewSize;
        private readonly ToolStripStatusLabel statusLabel;
        private bool loading;
        private bool busy;

        public MongoPipelineForm(my_mongodb database, string databaseName, string collectionName, Action<string> openInQuery)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;
            this.collectionName = collectionName;

            Text = Localization.Format("MongoPipeline.WindowTitle", databaseName, collectionName);
            Width = 1220;
            Height = 760;
            MinimumSize = new Size(880, 520);
            StartPosition = FormStartPosition.CenterParent;

            stageList = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = false };
            templateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            templateBox.Items.AddRange(MongoPipelineService.Templates.Select(item => (object)item.Key).ToArray());
            templateBox.SelectedIndex = 0;
            Button addStage = new Button { Text = Localization.T("MongoPipeline.AddStage"), AutoSize = true };
            Button moveUp = new Button { Text = Localization.T("MongoPipeline.MoveUp"), AutoSize = true };
            Button moveDown = new Button { Text = Localization.T("MongoPipeline.MoveDown"), AutoSize = true };
            Button removeStage = new Button { Text = Localization.T("MongoPipeline.RemoveStage"), AutoSize = true };
            FlowLayoutPanel stageButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(2) };
            stageButtons.Controls.AddRange(new Control[] { templateBox, addStage, moveUp, moveDown, removeStage });
            Panel left = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(8) };
            left.Controls.Add(stageList);
            left.Controls.Add(new Label { Text = Localization.T("MongoPipeline.Stages"), Dock = DockStyle.Top, Height = 20 });
            left.Controls.Add(stageButtons);

            stageEditor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 10f)
            };
            stageError = new Label { Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.FromArgb(180, 35, 24), AutoEllipsis = true };
            Panel editorPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 4, 4) };
            editorPanel.Controls.Add(stageEditor);
            editorPanel.Controls.Add(new Label { Text = Localization.T("MongoPipeline.StageBody"), Dock = DockStyle.Top, Height = 20 });
            editorPanel.Controls.Add(stageError);

            previewBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };
            previewSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            previewSize.Items.AddRange(PreviewSizes.Select(size => (object)size.ToString(CultureInfo.InvariantCulture)).ToArray());
            previewSize.SelectedIndex = 0;
            Button previewStage = new Button { Text = Localization.T("MongoPipeline.PreviewStage"), AutoSize = true };
            Button runAll = new Button { Text = Localization.T("MongoPipeline.RunAll"), AutoSize = true };
            FlowLayoutPanel previewButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
            previewButtons.Controls.AddRange(new Control[]
            {
                new Label { Text = Localization.T("MongoPipeline.PreviewSize"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
                previewSize, previewStage, runAll
            });
            Panel previewPanel = new Panel { Dock = DockStyle.Right, Width = 480, Padding = new Padding(4, 8, 8, 4) };
            previewPanel.Controls.Add(previewBox);
            previewPanel.Controls.Add(previewButtons);

            Button importButton = new Button { Text = Localization.T("MongoPipeline.Import"), AutoSize = true };
            Button copyShell = new Button { Text = Localization.T("MongoPipeline.CopyShell"), AutoSize = true };
            Button openQuery = new Button { Text = Localization.T("QueryBuilder.OpenInQuery"), AutoSize = true, Enabled = openInQuery != null };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
            bottom.Controls.AddRange(new Control[] { closeButton, openQuery, copyShell, importButton });

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("MongoPipeline.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(editorPanel);
            Controls.Add(previewPanel);
            Controls.Add(left);
            Controls.Add(bottom);
            Controls.Add(statusStrip);

            addStage.Click += (sender, args) => AddStage(Convert.ToString(templateBox.SelectedItem), null);
            moveUp.Click += (sender, args) => MoveSelected(-1);
            moveDown.Click += (sender, args) => MoveSelected(1);
            removeStage.Click += (sender, args) => RemoveSelected();
            stageList.SelectedIndexChanged += (sender, args) => ShowSelected();
            stageList.ItemCheck += (sender, args) =>
            {
                if (loading || args.Index < 0 || args.Index >= stages.Count) return;
                stages[args.Index].Enabled = args.NewValue == CheckState.Checked;
            };
            stageEditor.TextChanged += (sender, args) => SaveEditor();
            previewStage.Click += (sender, args) => RunGuarded(() => Preview(stageList.SelectedIndex < 0 ? stages.Count - 1 : stageList.SelectedIndex, SelectedPreviewSize()));
            runAll.Click += (sender, args) => RunGuarded(() => Preview(stages.Count - 1, SelectedPreviewSize()));
            importButton.Click += (sender, args) => ImportFromClipboardDialog();
            copyShell.Click += (sender, args) => RunGuarded(() =>
            {
                Clipboard.SetText(MongoPipelineService.ToShellText(collectionName, stages));
                statusLabel.Text = Localization.T("MongoPipeline.Copied");
            });
            openQuery.Click += (sender, args) => RunGuarded(() =>
            {
                if (openInQuery == null) return;
                openInQuery(MongoPipelineService.ToQueryJson(collectionName, stages, SelectedPreviewSize()));
                Close();
            });
            closeButton.Click += (sender, args) => Close();
            FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
            ThemeManager.ApplyTo(this);

            AddStage("$match", null);
        }

        public IList<MongoPipelineStage> Stages { get { return stages; } }

        /// <summary>新增 stage（body 為 null 時用範本）；也供測試直接呼叫。</summary>
        public void AddStage(string operatorName, string body)
        {
            int insertAt = stageList.SelectedIndex < 0 ? stages.Count : stageList.SelectedIndex + 1;
            stages.Insert(insertAt, new MongoPipelineStage(operatorName, body ?? MongoPipelineService.TemplateFor(operatorName)));
            RefreshList(insertAt);
        }

        /// <summary>預覽到指定 stage 為止的輸出；回傳文件。也供測試直接呼叫。</summary>
        public List<BsonDocument> Preview(int upToIndex, int limit)
        {
            List<BsonDocument> pipeline = MongoPipelineService.Build(stages, upToIndex);
            Stopwatch watch = Stopwatch.StartNew();
            List<BsonDocument> documents = database.RunPipeline(databaseName, collectionName, pipeline, limit);
            watch.Stop();
            JsonWriterSettings settings = new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson };
            previewBox.Text = string.Join(Environment.NewLine, documents.Select(document => document.ToJson(settings)))
                .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            int shownStage = Math.Min(upToIndex, stages.Count - 1) + 1;
            statusLabel.Text = Localization.Format("MongoPipeline.Previewed", shownStage, documents.Count, watch.ElapsedMilliseconds) +
                               (documents.Count >= limit ? " " + Localization.Format("MongoPipeline.PreviewCapped", limit) : string.Empty);
            return documents;
        }

        /// <summary>匯入 pipeline JSON 取代目前的 stage；也供測試直接呼叫。</summary>
        public void ImportPipeline(string json)
        {
            List<MongoPipelineStage> imported = MongoPipelineService.Import(json);
            stages.Clear();
            stages.AddRange(imported);
            RefreshList(stages.Count > 0 ? 0 : -1);
            statusLabel.Text = Localization.Format("MongoPipeline.Imported", imported.Count);
        }

        private void ImportFromClipboardDialog()
        {
            using (Form dialog = new Form { Text = Localization.T("MongoPipeline.Import"), Width = 640, Height = 420, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false })
            {
                TextBox input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9.5f), AcceptsReturn = true };
                if (Clipboard.ContainsText()) input.Text = Clipboard.GetText();
                Button ok = new Button { Text = Localization.T("MongoPipeline.Import"), DialogResult = DialogResult.OK, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dialog.Controls.Add(input);
                dialog.Controls.Add(new Label { Text = Localization.T("MongoPipeline.ImportHint"), Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 6, 8, 0) });
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                RunGuarded(() => ImportPipeline(input.Text));
            }
        }

        private void MoveSelected(int direction)
        {
            int index = stageList.SelectedIndex;
            int target = index + direction;
            if (index < 0 || target < 0 || target >= stages.Count) return;
            MongoPipelineStage stage = stages[index];
            stages.RemoveAt(index);
            stages.Insert(target, stage);
            RefreshList(target);
        }

        private void RemoveSelected()
        {
            int index = stageList.SelectedIndex;
            if (index < 0) return;
            stages.RemoveAt(index);
            RefreshList(Math.Min(index, stages.Count - 1));
        }

        private void RefreshList(int select)
        {
            loading = true;
            try
            {
                stageList.Items.Clear();
                for (int index = 0; index < stages.Count; index++)
                {
                    stageList.Items.Add(StageLabel(index), stages[index].Enabled);
                }
                if (select >= 0 && select < stageList.Items.Count) stageList.SelectedIndex = select;
            }
            finally
            {
                loading = false;
            }
            ShowSelected();
        }

        private string StageLabel(int index)
        {
            string body = (stages[index].BodyJson ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            while (body.Contains("  ")) body = body.Replace("  ", " ");
            if (body.Length > 40) body = body.Substring(0, 40) + "…";
            return (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + stages[index].Operator + "  " + body;
        }

        private void ShowSelected()
        {
            if (loading) return;
            int index = stageList.SelectedIndex;
            loading = true;
            try
            {
                stageEditor.Enabled = index >= 0;
                stageEditor.Text = index >= 0 ? stages[index].BodyJson.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) : string.Empty;
            }
            finally
            {
                loading = false;
            }
            Validate(index);
        }

        private void SaveEditor()
        {
            if (loading) return;
            int index = stageList.SelectedIndex;
            if (index < 0) return;
            stages[index].BodyJson = stageEditor.Text;
            loading = true;
            try
            {
                stageList.Items[index] = StageLabel(index);
            }
            finally
            {
                loading = false;
            }
            Validate(index);
        }

        private void Validate(int index)
        {
            if (index < 0)
            {
                stageError.Text = string.Empty;
                return;
            }
            try
            {
                MongoPipelineService.ParseStage(stages[index], index + 1);
                stageError.Text = string.Empty;
            }
            catch (FormatException exception)
            {
                stageError.Text = exception.Message;
            }
        }

        private int SelectedPreviewSize()
        {
            return PreviewSizes[Math.Max(0, previewSize.SelectedIndex)];
        }

        private void RunGuarded(Action action)
        {
            if (busy) return;
            busy = true;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("MongoPipeline.Failed", ExceptionMessageService.GetReason(ex));
            }
            finally
            {
                Cursor = previous;
                busy = false;
            }
        }
    }
}

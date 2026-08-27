using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class MongoDocumentSavedEventArgs : EventArgs
    {
        public string SavedDocumentJson = string.Empty;
    }

    /// <summary>
    /// MongoDB 文件檢視器：左側為可展開的文件樹，右側為 Canonical Extended JSON 編輯區。
    /// 儲存一律經由 my_mongodb.ReplaceDocumentChecked 的並行比對；view 或缺 _id 的文件為唯讀。
    /// </summary>
    public sealed class MongoDocumentViewerForm : Form
    {
        private readonly my_mongodb _db;
        private readonly string _databaseName;
        private readonly string _collectionName;
        private readonly bool _readOnly;
        private readonly TreeView _tree;
        private readonly TextBox _jsonBox;
        private readonly Label _statusLabel;
        private readonly Button _validateButton;
        private readonly Button _reloadButton;
        private readonly Button _saveButton;
        private string _originalJson;
        private bool _insertMode;

        public event EventHandler<MongoDocumentSavedEventArgs> DocumentSaved;

        public bool IsReadOnly { get { return _readOnly; } }
        public bool IsInsertMode { get { return _insertMode; } }
        public string DocumentJson { get { return _jsonBox.Text; } }

        /// <summary>開啟「新增文件」模式：從空白文件開始，儲存改走 InsertDocumentChecked。</summary>
        public static MongoDocumentViewerForm CreateForInsert(my_mongodb db, string databaseName, string collectionName)
        {
            return new MongoDocumentViewerForm(db, databaseName, collectionName, "{ }", false, string.Empty, true);
        }

        public MongoDocumentViewerForm(my_mongodb db, string databaseName, string collectionName, string documentJson, bool readOnly, string readOnlyReason)
            : this(db, databaseName, collectionName, documentJson, readOnly, readOnlyReason, false)
        {
        }

        private MongoDocumentViewerForm(my_mongodb db, string databaseName, string collectionName, string documentJson, bool readOnly, string readOnlyReason, bool insertMode)
        {
            _insertMode = insertMode;
            if (db == null) throw new ArgumentNullException("db");
            _db = db;
            _databaseName = databaseName ?? string.Empty;
            _collectionName = collectionName ?? string.Empty;
            _readOnly = readOnly;
            _originalJson = documentJson ?? string.Empty;

            Text = _insertMode
                ? Localization.Format("MongoDB.DocumentViewerInsertTitle", _databaseName, _collectionName)
                : Localization.Format("MongoDB.DocumentViewerTitle", _databaseName, _collectionName);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(720, 480);
            Size = new Size(960, 640);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 320
            };

            TableLayoutPanel leftPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("MongoDB.DocumentTreeLabel"),
                Margin = new Padding(0, 0, 0, 5)
            }, 0, 0);
            _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            leftPanel.Controls.Add(_tree, 0, 1);
            split.Panel1.Controls.Add(leftPanel);

            TableLayoutPanel rightPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            rightPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("MongoDB.CanonicalJsonHint"),
                ForeColor = Color.Gray,
                Margin = new Padding(0, 0, 0, 5)
            }, 0, 0);
            _jsonBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                AcceptsTab = true,
                Font = new Font("Consolas", 10f),
                ReadOnly = _readOnly
            };
            rightPanel.Controls.Add(_jsonBox, 0, 1);
            split.Panel2.Controls.Add(rightPanel);
            root.Controls.Add(split, 0, 0);

            _statusLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
                Text = _readOnly ? (readOnlyReason ?? string.Empty) : string.Empty
            };
            root.Controls.Add(_statusLabel, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 0)
            };
            _validateButton = MakeButton(Localization.T("MongoDB.Validate"), (s, e) => ValidateOnly());
            _reloadButton = MakeButton(Localization.T("MongoDB.Reload"), async (s, e) => await ReloadDocumentAsync());
            _saveButton = MakeButton(Localization.T("MongoDB.SaveDocument"), async (s, e) => await SaveDocumentAsync());
            Button closeButton = MakeButton(Localization.T("MongoDB.CloseViewer"), (s, e) => Close());
            if (_readOnly)
            {
                _validateButton.Visible = false;
                _saveButton.Visible = false;
            }
            if (_insertMode) _reloadButton.Visible = false;
            actions.Controls.AddRange(new Control[] { _validateButton, _reloadButton, _saveButton, closeButton });
            root.Controls.Add(actions, 0, 2);

            Controls.Add(root);
            ThemeManager.ApplyTo(this);
            ShowDocument(_originalJson);
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            Button button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            button.Click += onClick;
            return button;
        }

        private void ShowDocument(string documentJson)
        {
            string formatted;
            try { formatted = MongoDocumentEditService.FormatDocumentJson(documentJson); }
            catch (Exception) { formatted = documentJson ?? string.Empty; }
            _jsonBox.Text = formatted;
            RebuildTree(formatted);
        }

        private void RebuildTree(string documentJson)
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                MongoDocumentTreeNode rootNode;
                try { rootNode = MongoDocumentEditService.BuildTree(documentJson); }
                catch (Exception) { return; }
                TreeNode uiRoot = ToTreeNode(rootNode);
                _tree.Nodes.Add(uiRoot);
                uiRoot.Expand();
                foreach (TreeNode child in uiRoot.Nodes) child.Expand();
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private static TreeNode ToTreeNode(MongoDocumentTreeNode source)
        {
            string label = source.Children.Count > 0
                ? source.Name + " " + source.DisplayValue + "  (" + source.BsonType + ")"
                : source.Name + " : " + source.DisplayValue + "  (" + source.BsonType + ")";
            TreeNode node = new TreeNode(label);
            foreach (MongoDocumentTreeNode child in source.Children)
            {
                node.Nodes.Add(ToTreeNode(child));
            }
            return node;
        }

        private void ValidateOnly()
        {
            MongoDocumentEditValidation validation = _insertMode
                ? MongoDocumentEditService.ValidateInsert(_jsonBox.Text)
                : MongoDocumentEditService.ValidateEdit(_originalJson, _jsonBox.Text);
            if (!validation.Success)
            {
                _statusLabel.Text = validation.Error;
                return;
            }
            RebuildTree(validation.NormalizedJson);
            _statusLabel.Text = validation.HasChanges
                ? Localization.T("MongoDB.DocumentValid")
                : Localization.T("MongoDB.DocumentNoChanges");
        }

        private async Task ReloadDocumentAsync()
        {
            if (!_readOnly && !string.Equals(_jsonBox.Text, _originalJson, StringComparison.Ordinal))
            {
                DialogResult confirm = MessageBox.Show(this,
                    Localization.T("MongoDB.DiscardEditsConfirm"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
            }

            string filterJson;
            if (!MongoDocumentEditService.TryGetIdFilterJson(_originalJson, out filterJson))
            {
                _statusLabel.Text = Localization.T("MongoDB.DocumentIdRequired");
                return;
            }

            SetBusy(true);
            try
            {
                _statusLabel.Text = Localization.T("MongoDB.LoadingDocument");
                string latest = await Task.Run(() => _db.FindDocumentJson(_databaseName, _collectionName, filterJson));
                if (latest == null)
                {
                    _statusLabel.Text = Localization.T("MongoDB.DocumentDeleted");
                    _saveButton.Enabled = false;
                    return;
                }
                _originalJson = latest;
                ShowDocument(latest);
                _saveButton.Enabled = !_readOnly;
                _statusLabel.Text = string.Empty;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task SaveDocumentAsync()
        {
            if (_insertMode)
            {
                await InsertDocumentAsync();
                return;
            }

            MongoDocumentEditValidation validation = MongoDocumentEditService.ValidateEdit(_originalJson, _jsonBox.Text);
            if (!validation.Success)
            {
                _statusLabel.Text = validation.Error;
                return;
            }
            if (!validation.HasChanges)
            {
                _statusLabel.Text = Localization.T("MongoDB.DocumentNoChanges");
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                Localization.Format("MongoDB.ConfirmSaveDocument", _databaseName, _collectionName),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            string originalJson = _originalJson;
            string editedJson = validation.NormalizedJson;
            SetBusy(true);
            try
            {
                await Task.Run(() => _db.ReplaceDocumentChecked(_databaseName, _collectionName, originalJson, editedJson));
                _originalJson = editedJson;
                ShowDocument(editedJson);
                _statusLabel.Text = Localization.T("MongoDB.DocumentSaved");
                EventHandler<MongoDocumentSavedEventArgs> handler = DocumentSaved;
                if (handler != null) handler(this, new MongoDocumentSavedEventArgs { SavedDocumentJson = editedJson });
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task InsertDocumentAsync()
        {
            MongoDocumentEditValidation validation = MongoDocumentEditService.ValidateInsert(_jsonBox.Text);
            if (!validation.Success)
            {
                _statusLabel.Text = validation.Error;
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                Localization.Format("MongoDB.ConfirmInsertDocument", _databaseName, _collectionName),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            string documentJson = validation.NormalizedJson;
            SetBusy(true);
            try
            {
                string insertedJson = await Task.Run(() => _db.InsertDocumentChecked(_databaseName, _collectionName, documentJson));
                // 新增成功後轉成一般編輯模式，接著的儲存走安全寫回。
                _insertMode = false;
                _reloadButton.Visible = true;
                _originalJson = insertedJson;
                Text = Localization.Format("MongoDB.DocumentViewerTitle", _databaseName, _collectionName);
                ShowDocument(insertedJson);
                _statusLabel.Text = Localization.T("MongoDB.DocumentInserted");
                EventHandler<MongoDocumentSavedEventArgs> handler = DocumentSaved;
                if (handler != null) handler(this, new MongoDocumentSavedEventArgs { SavedDocumentJson = insertedJson });
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _validateButton.Enabled = !busy;
            _reloadButton.Enabled = !busy;
            _saveButton.Enabled = !busy && !_readOnly;
            _jsonBox.Enabled = !busy;
            UseWaitCursor = busy;
        }
    }
}

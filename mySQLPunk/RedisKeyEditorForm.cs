using System;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class RedisKeySavedEventArgs : EventArgs
    {
        public string Key = string.Empty;
        public string SavedValue = string.Empty;
        public bool Deleted;
        /// <summary>true 代表 string 值被儲存（SavedValue 有意義）；集合項目變更為 false。</summary>
        public bool IsStringValue;
    }

    /// <summary>
    /// Redis key 編輯器：string 值與 hash／list／set／zset 項目都以 WATCH／MULTI／EXEC 樂觀並行寫入，
    /// 另提供 TTL 設定與 key 刪除。值含無法以 UTF-8 呈現的位元組時只允許檢視，避免寫回損毀原始資料。
    /// </summary>
    public sealed class RedisKeyEditorForm : Form
    {
        private const int CollectionLoadLimit = 1000;

        private readonly my_redis _db;
        private readonly string _databaseName;
        private readonly string _key;
        private readonly Label _typeValueLabel;
        private readonly Label _ttlValueLabel;
        private readonly Label _statusLabel;
        private readonly TextBox _valueBox;
        private readonly Panel _stringPanel;
        private readonly TableLayoutPanel _collectionPanel;
        private readonly DataGridView _grid;
        private readonly Label _entryNameLabel;
        private readonly TextBox _entryNameBox;
        private readonly Label _entryValueLabel;
        private readonly TextBox _entryValueBox;
        private readonly Button _addUpdateButton;
        private readonly Button _deleteEntryButton;
        private readonly Button _appendButton;
        private readonly Label _collectionNoteLabel;
        private readonly TextBox _ttlSecondsBox;
        private readonly CheckBox _preserveTtlCheck;
        private readonly Button _saveButton;
        private readonly Button _applyTtlButton;
        private readonly Button _removeTtlButton;
        private readonly Button _deleteButton;
        private string _type = string.Empty;
        private string _originalValue = string.Empty;
        private bool _binaryUnsafe;
        private long _selectedListIndex = -1;

        public event EventHandler<RedisKeySavedEventArgs> KeySaved;

        public RedisKeyEditorForm(my_redis db, string databaseName, string key)
        {
            if (db == null) throw new ArgumentNullException("db");
            _db = db;
            _databaseName = databaseName ?? string.Empty;
            _key = key ?? string.Empty;

            Text = Localization.Format("Redis.KeyEditorTitle", _key);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(620, 480);
            Size = new Size(780, 580);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, AutoSize = true };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.KeyLabel"), Margin = new Padding(0, 0, 6, 4) }, 0, 0);
            header.Controls.Add(new Label { AutoSize = true, Text = _key, Font = new Font("Consolas", 9.5f), Margin = new Padding(0, 0, 0, 4) }, 1, 0);
            header.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.ColumnTypeComment").Split('（')[0] + "：", Margin = new Padding(0, 0, 6, 4) }, 0, 1);
            _typeValueLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            header.Controls.Add(_typeValueLabel, 1, 1);
            header.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.TtlLabel"), Margin = new Padding(0, 0, 6, 4) }, 0, 2);
            _ttlValueLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            header.Controls.Add(_ttlValueLabel, 1, 2);
            root.Controls.Add(header, 0, 0);

            // string 模式：多行文字編輯區
            _stringPanel = new Panel { Dock = DockStyle.Fill };
            _valueBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                Font = new Font("Consolas", 10f)
            };
            _stringPanel.Controls.Add(_valueBox);

            // 集合模式：項目網格＋輸入列
            _collectionPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Visible = false };
            _collectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _collectionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _collectionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            _grid.SelectionChanged += (s, e) => FillEntryInputsFromSelection();
            _collectionPanel.Controls.Add(_grid, 0, 0);

            FlowLayoutPanel entryRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 6, 0, 0),
                WrapContents = false
            };
            _entryNameLabel = new Label { AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            _entryNameBox = new TextBox { Width = 170, Margin = new Padding(0, 3, 10, 0) };
            _entryValueLabel = new Label { AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            _entryValueBox = new TextBox { Width = 220, Margin = new Padding(0, 3, 10, 0) };
            _addUpdateButton = MakeButton(Localization.T("Redis.AddOrUpdateEntry"), async (s, e) => await AddOrUpdateEntryAsync());
            _deleteEntryButton = MakeButton(Localization.T("Redis.DeleteEntry"), async (s, e) => await DeleteEntryAsync());
            _appendButton = MakeButton(Localization.T("Redis.AppendListEntry"), async (s, e) => await AppendListEntryAsync());
            entryRow.Controls.AddRange(new Control[] { _entryNameLabel, _entryNameBox, _entryValueLabel, _entryValueBox, _addUpdateButton, _deleteEntryButton, _appendButton });
            _collectionPanel.Controls.Add(entryRow, 0, 1);
            _collectionNoteLabel = new Label
            {
                AutoSize = true,
                ForeColor = ThemeManager.MutedTextColor,
                Margin = new Padding(0, 4, 0, 0),
                Visible = false,
                Text = Localization.T("Redis.ListEditNote")
            };
            _collectionPanel.Controls.Add(_collectionNoteLabel, 0, 2);

            Panel content = new Panel { Dock = DockStyle.Fill };
            content.Controls.Add(_stringPanel);
            content.Controls.Add(_collectionPanel);
            root.Controls.Add(content, 0, 1);

            FlowLayoutPanel ttlRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 6, 0, 0)
            };
            ttlRow.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.TtlSecondsLabel"), Margin = new Padding(0, 6, 4, 0) });
            _ttlSecondsBox = new TextBox { Width = 90, Margin = new Padding(0, 3, 8, 0) };
            ttlRow.Controls.Add(_ttlSecondsBox);
            _applyTtlButton = MakeButton(Localization.T("Redis.ApplyTtl"), async (s, e) => await ApplyTtlAsync());
            _removeTtlButton = MakeButton(Localization.T("Redis.RemoveTtl"), async (s, e) => await RemoveTtlAsync());
            ttlRow.Controls.Add(_applyTtlButton);
            ttlRow.Controls.Add(_removeTtlButton);
            _preserveTtlCheck = new CheckBox
            {
                AutoSize = true,
                Checked = true,
                Text = Localization.T("Redis.PreserveTtlOnSave"),
                Margin = new Padding(12, 5, 0, 0)
            };
            ttlRow.Controls.Add(_preserveTtlCheck);
            root.Controls.Add(ttlRow, 0, 2);

            _statusLabel = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            root.Controls.Add(_statusLabel, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 0)
            };
            _saveButton = MakeButton(Localization.T("Redis.SaveValue"), async (s, e) => await SaveValueAsync());
            Button reloadButton = MakeButton(Localization.T("Redis.ReloadValue"), async (s, e) => await ReloadAsync());
            _deleteButton = MakeButton(Localization.T("Redis.DeleteKeyAction"), async (s, e) => await DeleteKeyAsync());
            Button closeButton = MakeButton(Localization.T("MongoDB.CloseViewer"), (s, e) => Close());
            actions.Controls.AddRange(new Control[] { _saveButton, reloadButton, _deleteButton, closeButton });
            root.Controls.Add(actions, 0, 4);

            Controls.Add(root);
            ThemeManager.ApplyTo(this);
            Shown += async (s, e) => await ReloadAsync();
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            Button button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            button.Click += onClick;
            return button;
        }

        private bool IsStringMode { get { return string.Equals(_type, "string", StringComparison.OrdinalIgnoreCase); } }

        private void SetBusy(bool busy)
        {
            _saveButton.Enabled = !busy && IsStringMode && !_binaryUnsafe;
            _applyTtlButton.Enabled = !busy;
            _removeTtlButton.Enabled = !busy;
            _deleteButton.Enabled = !busy;
            _addUpdateButton.Enabled = !busy;
            _deleteEntryButton.Enabled = !busy;
            _appendButton.Enabled = !busy;
            _valueBox.ReadOnly = busy || _binaryUnsafe;
        }

        private void ShowTtl(long ttlMs)
        {
            _ttlValueLabel.Text = ttlMs < 0
                ? Localization.T("Redis.NoExpiry")
                : TimeSpan.FromMilliseconds(ttlMs).ToString("g", CultureInfo.InvariantCulture);
        }

        /// <summary>依 key 型別切換 string 或集合模式，並設定輸入列的標籤與可見度。</summary>
        private void ConfigureForType()
        {
            bool stringMode = IsStringMode;
            _typeValueLabel.Text = _type;
            _stringPanel.Visible = stringMode;
            _collectionPanel.Visible = !stringMode;
            _saveButton.Visible = stringMode;
            if (stringMode) return;

            _collectionNoteLabel.Visible = _type == "list";
            _appendButton.Visible = _type == "list";
            switch (_type)
            {
                case "hash":
                    _entryNameLabel.Text = Localization.T("Redis.EntryFieldColon");
                    _entryValueLabel.Text = Localization.T("Redis.EntryValueColon");
                    _entryNameBox.ReadOnly = false;
                    _entryValueBox.Visible = _entryValueLabel.Visible = true;
                    _addUpdateButton.Text = Localization.T("Redis.AddOrUpdateEntry");
                    _addUpdateButton.Visible = _deleteEntryButton.Visible = true;
                    break;
                case "list":
                    _entryNameLabel.Text = Localization.T("Redis.EntryIndexColon");
                    _entryValueLabel.Text = Localization.T("Redis.EntryValueColon");
                    _entryNameBox.ReadOnly = true;
                    _entryValueBox.Visible = _entryValueLabel.Visible = true;
                    _addUpdateButton.Text = Localization.T("Redis.UpdateEntry");
                    _addUpdateButton.Visible = true;
                    _deleteEntryButton.Visible = false;
                    break;
                case "set":
                    _entryNameLabel.Text = Localization.T("Redis.EntryMemberColon");
                    _entryNameBox.ReadOnly = false;
                    _entryValueBox.Visible = _entryValueLabel.Visible = false;
                    _addUpdateButton.Text = Localization.T("Redis.AddEntry");
                    _addUpdateButton.Visible = _deleteEntryButton.Visible = true;
                    break;
                case "zset":
                    _entryNameLabel.Text = Localization.T("Redis.EntryMemberColon");
                    _entryValueLabel.Text = Localization.T("Redis.EntryScoreColon");
                    _entryNameBox.ReadOnly = false;
                    _entryValueBox.Visible = _entryValueLabel.Visible = true;
                    _addUpdateButton.Text = Localization.T("Redis.AddOrUpdateEntry");
                    _addUpdateButton.Visible = _deleteEntryButton.Visible = true;
                    break;
                default:
                    // stream 等其他型別維持唯讀檢視。
                    _entryNameBox.Visible = _entryNameLabel.Visible = false;
                    _entryValueBox.Visible = _entryValueLabel.Visible = false;
                    _addUpdateButton.Visible = _deleteEntryButton.Visible = _appendButton.Visible = false;
                    break;
            }
        }

        private void FillEntryInputsFromSelection()
        {
            _selectedListIndex = -1;
            DataGridViewRow row = _grid.CurrentRow;
            DataRowView view = row == null ? null : row.DataBoundItem as DataRowView;
            if (view == null) return;
            DataTable table = view.Row.Table;
            switch (_type)
            {
                case "hash":
                    if (table.Columns.Contains("field")) _entryNameBox.Text = Convert.ToString(view.Row["field"], CultureInfo.InvariantCulture);
                    if (table.Columns.Contains("value")) _entryValueBox.Text = Convert.ToString(view.Row["value"], CultureInfo.InvariantCulture);
                    break;
                case "list":
                    if (table.Columns.Contains("index"))
                    {
                        _selectedListIndex = Convert.ToInt64(view.Row["index"], CultureInfo.InvariantCulture);
                        _entryNameBox.Text = _selectedListIndex.ToString(CultureInfo.InvariantCulture);
                    }
                    if (table.Columns.Contains("value")) _entryValueBox.Text = Convert.ToString(view.Row["value"], CultureInfo.InvariantCulture);
                    break;
                case "set":
                    if (table.Columns.Contains("member")) _entryNameBox.Text = Convert.ToString(view.Row["member"], CultureInfo.InvariantCulture);
                    break;
                case "zset":
                    if (table.Columns.Contains("member")) _entryNameBox.Text = Convert.ToString(view.Row["member"], CultureInfo.InvariantCulture);
                    if (table.Columns.Contains("score")) _entryValueBox.Text = Convert.ToString(view.Row["score"], CultureInfo.InvariantCulture);
                    break;
            }
        }

        /// <summary>在目前載入的項目中找 hash 欄位／zset 成員的既有值，決定是「更新」還是「新增」。</summary>
        private bool TryFindLoadedEntry(string keyColumn, string valueColumn, string name, out string existingValue)
        {
            existingValue = null;
            DataTable table = _grid.DataSource as DataTable;
            if (table == null || !table.Columns.Contains(keyColumn)) return false;
            foreach (DataRow row in table.Rows)
            {
                if (string.Equals(Convert.ToString(row[keyColumn], CultureInfo.InvariantCulture), name, StringComparison.Ordinal))
                {
                    existingValue = valueColumn != null && table.Columns.Contains(valueColumn)
                        ? Convert.ToString(row[valueColumn], CultureInfo.InvariantCulture)
                        : string.Empty;
                    return true;
                }
            }
            return false;
        }

        private async Task ReloadAsync()
        {
            SetBusy(true);
            try
            {
                string type = await Task.Run(() => _db.GetKeyTypeForEdit(_databaseName, _key));
                _type = type;
                ConfigureForType();
                if (IsStringMode)
                {
                    my_redis.RedisStringEditContext context = await Task.Run(() => _db.GetStringForEdit(_databaseName, _key));
                    _originalValue = context.Value ?? string.Empty;
                    _binaryUnsafe = context.IsBinaryUnsafe;
                    _valueBox.Text = _originalValue;
                    ShowTtl(context.TtlMs);
                    _statusLabel.Text = _binaryUnsafe ? Localization.T("Redis.EditBinaryUnsupported") : string.Empty;
                }
                else
                {
                    DataTable detail = null;
                    long ttlMs = -1;
                    await Task.Run(() =>
                    {
                        detail = _db.GetKeyDetailForEdit(_databaseName, _key, CollectionLoadLimit);
                        ttlMs = _db.GetKeyTtlMs(_databaseName, _key);
                    });
                    _grid.DataSource = detail;
                    ShowTtl(ttlMs);
                    _statusLabel.Text = string.Empty;
                    FillEntryInputsFromSelection();
                }
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

        private async Task SaveValueAsync()
        {
            if (!IsStringMode || _binaryUnsafe) return;
            string newValue = _valueBox.Text;
            bool preserveTtl = _preserveTtlCheck.Checked;
            SetBusy(true);
            try
            {
                string expected = _originalValue;
                await Task.Run(() => _db.SaveStringValue(_databaseName, _key, expected, newValue, preserveTtl));
                _originalValue = newValue;
                _statusLabel.Text = Localization.T("Redis.KeySaved");
                RaiseSaved(newValue, false, true);
                await RefreshTtlOnlyAsync();
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

        private async Task AddOrUpdateEntryAsync()
        {
            string name = _entryNameBox.Text;
            string value = _entryValueBox.Text;
            SetBusy(true);
            try
            {
                switch (_type)
                {
                    case "hash":
                        {
                            string existing;
                            bool exists = TryFindLoadedEntry("field", "value", name, out existing);
                            await Task.Run(() => _db.SaveHashField(_databaseName, _key, name, existing, exists, value));
                            break;
                        }
                    case "list":
                        {
                            if (_selectedListIndex < 0) { _statusLabel.Text = Localization.T("Redis.EntryNotSelected"); return; }
                            long index = _selectedListIndex;
                            string expected;
                            TryFindLoadedListElement(index, out expected);
                            await Task.Run(() => _db.SaveListElement(_databaseName, _key, index, expected, value));
                            break;
                        }
                    case "set":
                        await Task.Run(() => _db.AddSetMember(_databaseName, _key, name));
                        break;
                    case "zset":
                        {
                            string existingScore;
                            bool exists = TryFindLoadedEntry("member", "score", name, out existingScore);
                            await Task.Run(() => _db.SaveZSetMember(_databaseName, _key, name, existingScore, exists, value));
                            break;
                        }
                    default:
                        return;
                }
                _statusLabel.Text = Localization.T("Redis.EntrySaved");
                RaiseSaved(string.Empty, false, false);
                await ReloadCollectionAsync();
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

        private bool TryFindLoadedListElement(long index, out string expected)
        {
            expected = null;
            DataTable table = _grid.DataSource as DataTable;
            if (table == null || !table.Columns.Contains("index")) return false;
            foreach (DataRow row in table.Rows)
            {
                if (Convert.ToInt64(row["index"], CultureInfo.InvariantCulture) == index)
                {
                    expected = Convert.ToString(row["value"], CultureInfo.InvariantCulture);
                    return true;
                }
            }
            return false;
        }

        private async Task DeleteEntryAsync()
        {
            DataGridViewRow row = _grid.CurrentRow;
            DataRowView view = row == null ? null : row.DataBoundItem as DataRowView;
            if (view == null) { _statusLabel.Text = Localization.T("Redis.EntryNotSelected"); return; }
            SetBusy(true);
            try
            {
                switch (_type)
                {
                    case "hash":
                        {
                            string field = Convert.ToString(view.Row["field"], CultureInfo.InvariantCulture);
                            string expected = Convert.ToString(view.Row["value"], CultureInfo.InvariantCulture);
                            await Task.Run(() => _db.DeleteHashField(_databaseName, _key, field, expected));
                            break;
                        }
                    case "set":
                        {
                            string member = Convert.ToString(view.Row["member"], CultureInfo.InvariantCulture);
                            await Task.Run(() => _db.RemoveSetMember(_databaseName, _key, member));
                            break;
                        }
                    case "zset":
                        {
                            string member = Convert.ToString(view.Row["member"], CultureInfo.InvariantCulture);
                            await Task.Run(() => _db.RemoveZSetMember(_databaseName, _key, member));
                            break;
                        }
                    default:
                        return;
                }
                _statusLabel.Text = Localization.T("Redis.EntryDeleted");
                RaiseSaved(string.Empty, false, false);
                await ReloadCollectionAsync();
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

        private async Task AppendListEntryAsync()
        {
            if (_type != "list") return;
            string value = _entryValueBox.Text;
            SetBusy(true);
            try
            {
                await Task.Run(() => _db.AppendListElement(_databaseName, _key, value));
                _statusLabel.Text = Localization.T("Redis.EntrySaved");
                RaiseSaved(string.Empty, false, false);
                await ReloadCollectionAsync();
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

        private async Task ReloadCollectionAsync()
        {
            try
            {
                DataTable detail = await Task.Run(() => _db.GetKeyDetailForEdit(_databaseName, _key, CollectionLoadLimit));
                _grid.DataSource = detail;
            }
            catch (Exception)
            {
                // 清單更新失敗不影響已完成的寫入；使用者可按重新載入。
            }
        }

        private async Task RefreshTtlOnlyAsync()
        {
            try
            {
                long ttlMs = await Task.Run(() => _db.GetKeyTtlMs(_databaseName, _key));
                ShowTtl(ttlMs);
            }
            catch (Exception)
            {
                // TTL 顯示更新失敗不影響已完成的儲存。
            }
        }

        private async Task ApplyTtlAsync()
        {
            long seconds;
            if (!long.TryParse(_ttlSecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
            {
                _statusLabel.Text = Localization.T("Redis.TtlInvalid");
                return;
            }
            SetBusy(true);
            try
            {
                await Task.Run(() => _db.SetKeyTtl(_databaseName, _key, seconds));
                ShowTtl(seconds * 1000L);
                _statusLabel.Text = Localization.T("Redis.TtlApplied");
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

        private async Task RemoveTtlAsync()
        {
            SetBusy(true);
            try
            {
                await Task.Run(() => _db.RemoveKeyTtl(_databaseName, _key));
                ShowTtl(-1);
                _statusLabel.Text = Localization.T("Redis.TtlApplied");
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

        private async Task DeleteKeyAsync()
        {
            DialogResult confirm = MessageBox.Show(
                this,
                Localization.Format("Redis.ConfirmDeleteKey", _key),
                Localization.T("Redis.DeleteKeyAction"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            SetBusy(true);
            try
            {
                await Task.Run(() => _db.DeleteKey(_databaseName, _key));
                RaiseSaved(string.Empty, true, false);
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                SetBusy(false);
            }
        }

        private void RaiseSaved(string savedValue, bool deleted, bool isStringValue)
        {
            EventHandler<RedisKeySavedEventArgs> handler = KeySaved;
            if (handler != null)
                handler(this, new RedisKeySavedEventArgs { Key = _key, SavedValue = savedValue, Deleted = deleted, IsStringValue = isStringValue });
        }
    }
}

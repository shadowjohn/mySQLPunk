using System;
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
    }

    /// <summary>
    /// Redis key 編輯器：string 值以 WATCH／MULTI／EXEC 樂觀並行儲存，另提供 TTL 設定與 key 刪除。
    /// 值含無法以 UTF-8 呈現的位元組時只允許檢視，避免寫回損毀原始資料。
    /// </summary>
    public sealed class RedisKeyEditorForm : Form
    {
        private readonly my_redis _db;
        private readonly string _databaseName;
        private readonly string _key;
        private readonly TextBox _valueBox;
        private readonly Label _ttlValueLabel;
        private readonly Label _statusLabel;
        private readonly TextBox _ttlSecondsBox;
        private readonly CheckBox _preserveTtlCheck;
        private readonly Button _saveButton;
        private readonly Button _applyTtlButton;
        private readonly Button _removeTtlButton;
        private readonly Button _deleteButton;
        private string _originalValue = string.Empty;
        private bool _binaryUnsafe;

        public event EventHandler<RedisKeySavedEventArgs> KeySaved;

        public RedisKeyEditorForm(my_redis db, string databaseName, string key)
        {
            if (db == null) throw new ArgumentNullException("db");
            _db = db;
            _databaseName = databaseName ?? string.Empty;
            _key = key ?? string.Empty;

            Text = Localization.Format("Redis.KeyEditorTitle", _key);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 420);
            Size = new Size(720, 520);
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

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.KeyLabel"), Margin = new Padding(0, 0, 6, 4) }, 0, 0);
            header.Controls.Add(new Label { AutoSize = true, Text = _key, Font = new Font("Consolas", 9.5f), Margin = new Padding(0, 0, 0, 4) }, 1, 0);
            header.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Redis.TtlLabel"), Margin = new Padding(0, 0, 6, 4) }, 0, 1);
            _ttlValueLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            header.Controls.Add(_ttlValueLabel, 1, 1);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel valuePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            valuePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            valuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            valuePanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("Redis.ValueLabel"),
                Margin = new Padding(0, 0, 0, 5)
            }, 0, 0);
            _valueBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                Font = new Font("Consolas", 10f)
            };
            valuePanel.Controls.Add(_valueBox, 0, 1);
            root.Controls.Add(valuePanel, 0, 1);

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

        private void SetBusy(bool busy)
        {
            _saveButton.Enabled = !busy && !_binaryUnsafe;
            _applyTtlButton.Enabled = !busy;
            _removeTtlButton.Enabled = !busy;
            _deleteButton.Enabled = !busy;
            _valueBox.ReadOnly = busy || _binaryUnsafe;
        }

        private void ShowTtl(long ttlMs)
        {
            _ttlValueLabel.Text = ttlMs < 0
                ? Localization.T("Redis.NoExpiry")
                : TimeSpan.FromMilliseconds(ttlMs).ToString("g", CultureInfo.InvariantCulture);
        }

        private async Task ReloadAsync()
        {
            SetBusy(true);
            try
            {
                my_redis.RedisStringEditContext context = await Task.Run(() => _db.GetStringForEdit(_databaseName, _key));
                _originalValue = context.Value ?? string.Empty;
                _binaryUnsafe = context.IsBinaryUnsafe;
                _valueBox.Text = _originalValue;
                ShowTtl(context.TtlMs);
                _statusLabel.Text = _binaryUnsafe ? Localization.T("Redis.EditBinaryUnsupported") : string.Empty;
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
            if (_binaryUnsafe) return;
            string newValue = _valueBox.Text;
            bool preserveTtl = _preserveTtlCheck.Checked;
            SetBusy(true);
            try
            {
                string expected = _originalValue;
                await Task.Run(() => _db.SaveStringValue(_databaseName, _key, expected, newValue, preserveTtl));
                _originalValue = newValue;
                _statusLabel.Text = Localization.T("Redis.KeySaved");
                RaiseSaved(newValue, false);
                await RefreshTtlOnlyAsync();
            }
            catch (RedisEditConflictException ex)
            {
                _statusLabel.Text = ex.Message;
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

        private async Task RefreshTtlOnlyAsync()
        {
            try
            {
                my_redis.RedisStringEditContext context = await Task.Run(() => _db.GetStringForEdit(_databaseName, _key));
                ShowTtl(context.TtlMs);
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
                RaiseSaved(string.Empty, true);
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                SetBusy(false);
            }
        }

        private void RaiseSaved(string savedValue, bool deleted)
        {
            EventHandler<RedisKeySavedEventArgs> handler = KeySaved;
            if (handler != null)
                handler(this, new RedisKeySavedEventArgs { Key = _key, SavedValue = savedValue, Deleted = deleted });
        }
    }
}

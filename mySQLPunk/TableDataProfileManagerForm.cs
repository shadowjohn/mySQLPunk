using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class TableDataProfileManagerForm : Form
    {
        private readonly TableDataProfileStore _store;
        private readonly string _provider;
        private readonly string _database;
        private readonly string _table;
        private readonly List<string> _columns;
        private readonly ListBox _profileList;
        private readonly TextBox _nameBox;
        private readonly TextBox _filterBox;
        private readonly ComboBox _sortColumnBox;
        private readonly CheckBox _descendingBox;
        private readonly CheckedListBox _visibleColumnsList;
        private readonly Button _deleteButton;
        private string _editingId;

        public string SelectedProfileId { get; private set; }

        public TableDataProfileManagerForm(
            TableDataProfileStore store,
            string provider,
            string database,
            string table,
            IEnumerable<string> columns,
            string selectedProfileId)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _provider = provider ?? string.Empty;
            _database = database ?? string.Empty;
            _table = table ?? string.Empty;
            _columns = (columns ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SelectedProfileId = selectedProfileId;

            Text = Localization.Format("TableProfile.ManagerTitle", _table);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(780, 520);
            Size = new Size(940, 650);
            KeyPreview = true;
            KeyDown += OnFormKeyDown;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            TableLayoutPanel left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 10, 0)
            };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("TableProfile.Profiles"),
                Margin = new Padding(0, 0, 0, 6)
            }, 0, 0);
            _profileList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _profileList.SelectedIndexChanged += (sender, args) => LoadSelectedProfile();
            left.Controls.Add(_profileList, 0, 1);

            TableLayoutPanel editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _nameBox = AddTextField(editor, 0, Localization.T("TableProfile.Name"), false);
            _filterBox = AddTextField(editor, 1, Localization.T("TableProfile.Filter"), true);
            _filterBox.Font = new Font("Consolas", 10f);

            AddLabel(editor, 2, Localization.T("TableProfile.SortColumn"));
            _sortColumnBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 0, 5)
            };
            _sortColumnBox.Items.Add(Localization.T("TableProfile.DefaultSort"));
            foreach (string column in _columns) _sortColumnBox.Items.Add(column);
            _sortColumnBox.SelectedIndex = 0;
            Control sortColumnField = UiField.Wrap(_sortColumnBox);
            sortColumnField.Dock = DockStyle.Fill;
            sortColumnField.Margin = new Padding(0, 3, 0, 5);
            editor.Controls.Add(sortColumnField, 1, 2);

            AddLabel(editor, 3, Localization.T("TableProfile.SortDirection"));
            _descendingBox = new CheckBox
            {
                AutoSize = true,
                Text = Localization.T("TableProfile.Descending"),
                Margin = new Padding(0, 7, 0, 7)
            };
            editor.Controls.Add(_descendingBox, 1, 3);

            AddLabel(editor, 4, Localization.T("TableProfile.VisibleColumns"));
            _visibleColumnsList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                Margin = new Padding(0, 3, 0, 5)
            };
            foreach (string column in _columns) _visibleColumnsList.Items.Add(column, true);
            editor.Controls.Add(_visibleColumnsList, 1, 4);

            Label filterHint = new Label
            {
                AutoSize = true,
                Text = Localization.T("TableProfile.FilterHint"),
                ForeColor = Color.Gray,
                Margin = new Padding(0, 4, 0, 8)
            };
            editor.Controls.Add(filterHint, 1, 5);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            Button newButton = MakeButton(Localization.T("TableProfile.New"), (sender, args) => StartNewProfile());
            Button saveButton = MakeButton(Localization.T("TableProfile.Save"), (sender, args) => SaveProfile());
            _deleteButton = MakeButton(Localization.T("TableProfile.Delete"), (sender, args) => DeleteProfile());
            Button closeButton = MakeButton(Localization.T("TableProfile.ApplyAndClose"), (sender, args) => ApplyAndClose());
            actions.Controls.AddRange(new Control[] { newButton, saveButton, _deleteButton, closeButton });
            editor.Controls.Add(actions, 0, 6);
            editor.SetColumnSpan(actions, 2);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(editor, 1, 0);
            Controls.Add(root);
            ThemeManager.ApplyTo(this);
            ReloadProfiles(selectedProfileId);
        }

        private static TextBox AddTextField(TableLayoutPanel panel, int row, string labelText, bool multiline)
        {
            AddLabel(panel, row, labelText);
            TextBox box = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                WordWrap = multiline,
                Margin = new Padding(0, 3, 0, 5)
            };
            Control field = UiField.Wrap(box);
            field.Dock = DockStyle.Fill;
            field.Margin = box.Margin;
            panel.Controls.Add(field, 1, row);
            return box;
        }

        private static void AddLabel(TableLayoutPanel panel, int row, string text)
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = text,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            }, 0, row);
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button button = new Button { AutoSize = true, Text = text, Margin = new Padding(0, 0, 6, 4) };
            button.Click += click;
            return button;
        }

        private void ReloadProfiles(string selectId)
        {
            List<TableDataProfile> profiles = _store.GetProfiles(_provider, _database, _table);
            _profileList.BeginUpdate();
            try
            {
                _profileList.Items.Clear();
                foreach (TableDataProfile profile in profiles)
                {
                    int index = _profileList.Items.Add(new ProfileListItem(profile));
                    if (!string.IsNullOrWhiteSpace(selectId) && string.Equals(profile.Id, selectId, StringComparison.OrdinalIgnoreCase))
                    {
                        _profileList.SelectedIndex = index;
                    }
                }
            }
            finally
            {
                _profileList.EndUpdate();
            }
            if (_profileList.SelectedIndex < 0 && _profileList.Items.Count > 0) _profileList.SelectedIndex = 0;
            if (_profileList.Items.Count == 0) StartNewProfile();
            UpdateActionState();
        }

        private void LoadSelectedProfile()
        {
            ProfileListItem item = _profileList.SelectedItem as ProfileListItem;
            if (item == null)
            {
                UpdateActionState();
                return;
            }

            TableDataProfile profile = item.Profile;
            _editingId = profile.Id;
            SelectedProfileId = profile.Id;
            _nameBox.Text = profile.Name ?? string.Empty;
            _filterBox.Text = profile.FilterExpression ?? string.Empty;
            int sortIndex = 0;
            for (int index = 1; index < _sortColumnBox.Items.Count; index++)
            {
                if (string.Equals(Convert.ToString(_sortColumnBox.Items[index]), profile.SortColumn, StringComparison.OrdinalIgnoreCase))
                {
                    sortIndex = index;
                    break;
                }
            }
            _sortColumnBox.SelectedIndex = sortIndex;
            _descendingBox.Checked = profile.SortDescending;
            HashSet<string> visible = new HashSet<string>(profile.VisibleColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            bool showAll = visible.Count == 0;
            for (int index = 0; index < _visibleColumnsList.Items.Count; index++)
            {
                _visibleColumnsList.SetItemChecked(index, showAll || visible.Contains(Convert.ToString(_visibleColumnsList.Items[index])));
            }
            UpdateActionState();
        }

        private void StartNewProfile()
        {
            _profileList.ClearSelected();
            _editingId = null;
            _nameBox.Clear();
            _filterBox.Clear();
            _sortColumnBox.SelectedIndex = 0;
            _descendingBox.Checked = false;
            for (int index = 0; index < _visibleColumnsList.Items.Count; index++) _visibleColumnsList.SetItemChecked(index, true);
            _nameBox.Focus();
            UpdateActionState();
        }

        private void SaveProfile()
        {
            try
            {
                List<string> visibleColumns = _visibleColumnsList.CheckedItems.Cast<object>()
                    .Select(Convert.ToString)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                string sortColumn = _sortColumnBox.SelectedIndex <= 0 ? string.Empty : Convert.ToString(_sortColumnBox.SelectedItem);
                TableDataProfile saved = _store.Save(
                    _provider,
                    _database,
                    _table,
                    new TableDataProfile
                    {
                        Id = _editingId,
                        Name = _nameBox.Text,
                        FilterExpression = _filterBox.Text,
                        SortColumn = sortColumn,
                        SortDescending = _descendingBox.Checked,
                        VisibleColumns = visibleColumns
                    },
                    _columns);
                SelectedProfileId = saved.Id;
                ReloadProfiles(saved.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.T("TableProfile.ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DeleteProfile()
        {
            if (string.IsNullOrWhiteSpace(_editingId)) return;
            if (MessageBox.Show(this, Localization.T("TableProfile.DeleteConfirm"), Localization.T("TableProfile.Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _store.Delete(_provider, _database, _table, _editingId);
            SelectedProfileId = null;
            _editingId = null;
            ReloadProfiles(null);
        }

        private void ApplyAndClose()
        {
            SelectedProfileId = _editingId;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateActionState()
        {
            _deleteButton.Enabled = !string.IsNullOrWhiteSpace(_editingId);
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape) return;
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }

        private sealed class ProfileListItem
        {
            public TableDataProfile Profile { get; private set; }

            public ProfileListItem(TableDataProfile profile)
            {
                Profile = profile;
            }

            public override string ToString()
            {
                return Profile.Name ?? string.Empty;
            }
        }
    }
}

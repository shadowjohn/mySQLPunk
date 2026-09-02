using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class SqlSnippetManagerForm : Form
    {
        private readonly SqlSnippetService _service;
        private readonly TextBox _searchBox;
        private readonly ListBox _snippetList;
        private readonly TextBox _nameBox;
        private readonly TextBox _shortcutBox;
        private readonly TextBox _descriptionBox;
        private readonly TextBox _sqlBox;
        private readonly Button _saveButton;
        private readonly Button _deleteButton;
        private readonly Button _insertButton;
        private List<SqlCodeSnippet> _allSnippets = new List<SqlCodeSnippet>();
        private string _editingId;
        private bool _editingBuiltIn;

        public SqlCodeSnippet SelectedSnippet { get; private set; }

        public SqlSnippetManagerForm(SqlSnippetService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            _service = service;
            Text = Localization.T("Snippet.Title");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 500);
            Size = new Size(920, 620);
            KeyPreview = true;
            KeyDown += OnFormKeyDown;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            TableLayoutPanel left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0, 0, 10, 0)
            };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("Snippet.Search"),
                Margin = new Padding(0, 0, 0, 5)
            }, 0, 0);
            _searchBox = new TextBox { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            _searchBox.TextChanged += (sender, args) => RefreshList(null);
            Control searchField = UiField.Wrap(_searchBox);
            searchField.Dock = DockStyle.Top;
            searchField.Margin = new Padding(0, 0, 0, 8);
            left.Controls.Add(searchField, 0, 1);
            _snippetList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _snippetList.SelectedIndexChanged += (sender, args) => LoadSelectedSnippet();
            _snippetList.DoubleClick += (sender, args) => InsertSelectedSnippet();
            left.Controls.Add(_snippetList, 0, 2);

            TableLayoutPanel editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _nameBox = AddField(editor, 0, Localization.T("Snippet.Name"), false);
            _shortcutBox = AddField(editor, 1, Localization.T("Snippet.Shortcut"), false);
            _descriptionBox = AddField(editor, 2, Localization.T("Snippet.Description"), false);
            _sqlBox = AddField(editor, 3, Localization.T("Snippet.Sql"), true);
            _sqlBox.AcceptsTab = true;
            _sqlBox.Font = new Font("Consolas", 10f);

            Label hint = new Label
            {
                AutoSize = true,
                Text = Localization.T("Snippet.CursorHint"),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 7, 3, 8)
            };
            editor.Controls.Add(hint, 1, 4);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            Button newButton = MakeButton(Localization.T("Snippet.New"), (sender, args) => StartNewSnippet());
            _saveButton = MakeButton(Localization.T("Snippet.Save"), (sender, args) => SaveSnippet());
            _deleteButton = MakeButton(Localization.T("Snippet.Delete"), (sender, args) => DeleteSnippet());
            Button importButton = MakeButton(Localization.T("Snippet.Import"), (sender, args) => ImportSnippets());
            Button exportButton = MakeButton(Localization.T("Snippet.Export"), (sender, args) => ExportSnippets());
            _insertButton = MakeButton(Localization.T("Snippet.Insert"), (sender, args) => InsertSelectedSnippet());
            Button closeButton = MakeButton(Localization.T("Snippet.Close"), (sender, args) => Close());
            actions.Controls.AddRange(new Control[] { newButton, _saveButton, _deleteButton, importButton, exportButton, _insertButton, closeButton });
            editor.Controls.Add(actions, 0, 5);
            editor.SetColumnSpan(actions, 2);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(editor, 1, 0);
            Controls.Add(root);
            ThemeManager.ApplyTo(this);
            ReloadSnippets(null);
        }

        private static TextBox AddField(TableLayoutPanel panel, int row, string labelText, bool multiline)
        {
            Label label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            };
            TextBox box = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Both : ScrollBars.None,
                WordWrap = !multiline,
                Margin = new Padding(0, 3, 0, 5)
            };
            Control field = UiField.Wrap(box);
            field.Dock = DockStyle.Fill;
            field.Margin = box.Margin;
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(field, 1, row);
            return box;
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button button = new Button { AutoSize = true, Text = text, Margin = new Padding(0, 0, 6, 4) };
            button.Click += click;
            return button;
        }

        private void ReloadSnippets(string selectId)
        {
            _allSnippets = _service.GetAll();
            RefreshList(selectId);
        }

        private void RefreshList(string selectId)
        {
            string filter = (_searchBox.Text ?? string.Empty).Trim();
            _snippetList.BeginUpdate();
            try
            {
                _snippetList.Items.Clear();
                foreach (SqlCodeSnippet snippet in _allSnippets.Where(item => MatchesFilter(item, filter)))
                {
                    SnippetListItem wrapper = new SnippetListItem(snippet);
                    int index = _snippetList.Items.Add(wrapper);
                    if (!string.IsNullOrWhiteSpace(selectId) && string.Equals(snippet.Id, selectId, StringComparison.OrdinalIgnoreCase))
                    {
                        _snippetList.SelectedIndex = index;
                    }
                }
            }
            finally
            {
                _snippetList.EndUpdate();
            }
            if (_snippetList.SelectedIndex < 0 && _snippetList.Items.Count > 0) _snippetList.SelectedIndex = 0;
            UpdateActionState();
        }

        private static bool MatchesFilter(SqlCodeSnippet snippet, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return Contains(snippet.Name, filter) || Contains(snippet.Shortcut, filter) ||
                   Contains(snippet.Description, filter) || Contains(snippet.Sql, filter);
        }

        private static bool Contains(string value, string filter)
        {
            return (value ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadSelectedSnippet()
        {
            SnippetListItem item = _snippetList.SelectedItem as SnippetListItem;
            if (item == null)
            {
                UpdateActionState();
                return;
            }
            SqlCodeSnippet snippet = item.Snippet;
            _editingId = snippet.Id;
            _editingBuiltIn = snippet.IsBuiltIn;
            _nameBox.Text = snippet.Name ?? string.Empty;
            _shortcutBox.Text = snippet.Shortcut ?? string.Empty;
            _descriptionBox.Text = snippet.Description ?? string.Empty;
            _sqlBox.Text = snippet.Sql ?? string.Empty;
            UpdateActionState();
        }

        private void StartNewSnippet()
        {
            _snippetList.ClearSelected();
            _editingId = null;
            _editingBuiltIn = false;
            _nameBox.Clear();
            _shortcutBox.Clear();
            _descriptionBox.Clear();
            _sqlBox.Text = "SELECT " + SqlSnippetService.CursorMarker + ";";
            _nameBox.Focus();
            UpdateActionState();
        }

        private void SaveSnippet()
        {
            try
            {
                SqlCodeSnippet saved = _service.Save(new SqlCodeSnippet
                {
                    Id = _editingBuiltIn ? null : _editingId,
                    Name = _nameBox.Text,
                    Shortcut = _shortcutBox.Text,
                    Description = _descriptionBox.Text,
                    Sql = _sqlBox.Text,
                    IsBuiltIn = _editingBuiltIn
                });
                ReloadSnippets(saved.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.T("Snippet.ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DeleteSnippet()
        {
            if (_editingBuiltIn || string.IsNullOrWhiteSpace(_editingId)) return;
            if (MessageBox.Show(this, Localization.T("Snippet.DeleteConfirm"), Localization.T("Snippet.Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _service.Delete(_editingId);
            _editingId = null;
            ReloadSnippets(null);
        }

        private void ImportSnippets()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = Localization.T("Snippet.FileFilter"), CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    int count = _service.Import(dialog.FileName);
                    ReloadSnippets(null);
                    MessageBox.Show(this, Localization.Format("Snippet.Imported", count), Localization.T("Snippet.Import"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, Localization.T("Snippet.ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ExportSnippets()
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = Localization.T("Snippet.FileFilter"),
                FileName = "mysqlpunk-sql-snippets.json",
                OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _service.Export(dialog.FileName);
                    MessageBox.Show(this, Localization.Format("Snippet.Exported", dialog.FileName), Localization.T("Snippet.Export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, Localization.T("Snippet.ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void InsertSelectedSnippet()
        {
            SnippetListItem item = _snippetList.SelectedItem as SnippetListItem;
            if (item == null) return;
            SelectedSnippet = item.Snippet.Clone();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateActionState()
        {
            bool selected = _snippetList.SelectedItem != null;
            _deleteButton.Enabled = selected && !_editingBuiltIn && !string.IsNullOrWhiteSpace(_editingId);
            _insertButton.Enabled = selected;
            _saveButton.Text = _editingBuiltIn ? Localization.T("Snippet.SaveCopy") : Localization.T("Snippet.Save");
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private sealed class SnippetListItem
        {
            public SqlCodeSnippet Snippet { get; private set; }

            public SnippetListItem(SqlCodeSnippet snippet)
            {
                Snippet = snippet;
            }

            public override string ToString()
            {
                string kind = Snippet.IsBuiltIn ? Localization.T("Snippet.BuiltIn") : Localization.T("Snippet.Custom");
                return (Snippet.Shortcut ?? string.Empty) + "  —  " + (Snippet.Name ?? string.Empty) + "  [" + kind + "]";
            }
        }
    }
}

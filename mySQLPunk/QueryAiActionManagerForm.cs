using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class QueryAiActionManagerForm : Form
    {
        private readonly QueryAiActionService _service;
        private readonly ListBox _actionList;
        private readonly TextBox _nameBox;
        private readonly TextBox _instructionBox;
        private readonly CheckBox _pinCheckBox;
        private readonly Button _saveButton;
        private readonly Button _deleteButton;
        private readonly Button _useButton;
        private List<QueryAiCustomAction> _actions = new List<QueryAiCustomAction>();
        private string _editingId;

        public QueryAiCustomAction SelectedAction { get; private set; }

        public QueryAiActionManagerForm(QueryAiActionService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            _service = service;
            Text = Localization.T("AiAction.Title");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(720, 480);
            Size = new Size(860, 570);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            KeyDown += OnFormKeyDown;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            TableLayoutPanel left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 12, 0)
            };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Localization.T("AiAction.List"),
                Margin = new Padding(0, 0, 0, 6)
            }, 0, 0);
            _actionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _actionList.SelectedIndexChanged += (sender, args) => LoadSelectedAction();
            _actionList.DoubleClick += (sender, args) => UseCurrentAction();
            left.Controls.Add(_actionList, 0, 1);

            TableLayoutPanel editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _nameBox = AddTextField(editor, 0, Localization.T("AiAction.Name"), false);
            _nameBox.MaxLength = QueryAiActionService.MaxNameLength;
            _nameBox.TextChanged += (sender, args) => UpdateActionState();
            _instructionBox = AddTextField(editor, 1, Localization.T("AiAction.Instruction"), true);
            _instructionBox.AcceptsTab = true;
            _instructionBox.Font = new Font("Consolas", 10f);
            _instructionBox.MaxLength = QueryAiActionService.MaxInstructionLength;
            _instructionBox.TextChanged += (sender, args) => UpdateActionState();

            _pinCheckBox = new CheckBox
            {
                AutoSize = true,
                Text = Localization.T("AiAction.Pin"),
                Margin = new Padding(0, 6, 0, 5)
            };
            editor.Controls.Add(_pinCheckBox, 1, 2);

            Label hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                Text = Localization.T("AiAction.Hint"),
                ForeColor = Color.Gray,
                Margin = new Padding(0, 3, 0, 10)
            };
            editor.Controls.Add(hint, 1, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            Button newButton = MakeButton(Localization.T("AiAction.New"), (sender, args) => StartNewAction());
            _saveButton = MakeButton(Localization.T("AiAction.Save"), (sender, args) => SaveCurrentAction());
            _deleteButton = MakeButton(Localization.T("AiAction.Delete"), (sender, args) => DeleteCurrentAction());
            _useButton = MakeButton(Localization.T("AiAction.Use"), (sender, args) => UseCurrentAction());
            Button closeButton = MakeButton(Localization.T("AiAction.Close"), (sender, args) => Close());
            actions.Controls.AddRange(new Control[] { newButton, _saveButton, _deleteButton, _useButton, closeButton });
            editor.Controls.Add(actions, 0, 4);
            editor.SetColumnSpan(actions, 2);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(editor, 1, 0);
            Controls.Add(root);
            AcceptButton = _useButton;
            ThemeManager.ApplyTo(this);
            ReloadActions(null);
        }

        private static TextBox AddTextField(TableLayoutPanel panel, int row, string labelText, bool multiline)
        {
            Label label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = multiline ? AnchorStyles.Left | AnchorStyles.Top : AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            };
            TextBox box = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                WordWrap = true,
                Margin = new Padding(0, 3, 0, 5)
            };
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(box, 1, row);
            return box;
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button button = new Button { AutoSize = true, Text = text, Margin = new Padding(0, 0, 6, 4) };
            button.Click += click;
            return button;
        }

        private void ReloadActions(string selectId)
        {
            _actions = _service.Load();
            _actionList.BeginUpdate();
            try
            {
                _actionList.Items.Clear();
                foreach (QueryAiCustomAction action in _actions)
                {
                    int index = _actionList.Items.Add(new ActionListItem(action));
                    if (!string.IsNullOrWhiteSpace(selectId) &&
                        string.Equals(action.Id, selectId, StringComparison.OrdinalIgnoreCase))
                        _actionList.SelectedIndex = index;
                }
            }
            finally
            {
                _actionList.EndUpdate();
            }
            if (_actionList.SelectedIndex < 0 && _actionList.Items.Count > 0) _actionList.SelectedIndex = 0;
            else if (_actionList.Items.Count == 0) StartNewAction();
            UpdateActionState();
        }

        private void LoadSelectedAction()
        {
            ActionListItem item = _actionList.SelectedItem as ActionListItem;
            if (item == null)
            {
                UpdateActionState();
                return;
            }
            QueryAiCustomAction action = item.Action;
            _editingId = action.Id;
            _nameBox.Text = action.Name ?? string.Empty;
            _instructionBox.Text = action.Instruction ?? string.Empty;
            _pinCheckBox.Checked = action.Pinned;
            UpdateActionState();
        }

        private void StartNewAction()
        {
            _actionList.ClearSelected();
            _editingId = null;
            _nameBox.Clear();
            _instructionBox.Clear();
            _pinCheckBox.Checked = true;
            _nameBox.Focus();
            UpdateActionState();
        }

        private QueryAiCustomAction SaveCurrentAction()
        {
            try
            {
                QueryAiCustomAction saved = _service.Save(new QueryAiCustomAction
                {
                    Id = _editingId,
                    Name = _nameBox.Text,
                    Instruction = _instructionBox.Text,
                    Pinned = _pinCheckBox.Checked
                });
                _editingId = saved.Id;
                ReloadActions(saved.Id);
                return saved;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.T("AiAction.ErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        private void DeleteCurrentAction()
        {
            if (string.IsNullOrWhiteSpace(_editingId)) return;
            if (MessageBox.Show(this, Localization.T("AiAction.DeleteConfirm"), Localization.T("AiAction.Delete"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _service.Delete(_editingId);
            _editingId = null;
            ReloadActions(null);
            if (_actionList.Items.Count == 0) StartNewAction();
        }

        private void UseCurrentAction()
        {
            QueryAiCustomAction saved = SaveCurrentAction();
            if (saved == null) return;
            SelectedAction = saved.Clone();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateActionState()
        {
            bool complete = !string.IsNullOrWhiteSpace(_nameBox.Text) &&
                !string.IsNullOrWhiteSpace(_instructionBox.Text);
            _deleteButton.Enabled = !string.IsNullOrWhiteSpace(_editingId);
            _saveButton.Enabled = complete;
            _useButton.Enabled = complete;
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                SaveCurrentAction();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private sealed class ActionListItem
        {
            public QueryAiCustomAction Action { get; private set; }

            public ActionListItem(QueryAiCustomAction action)
            {
                Action = action;
            }

            public override string ToString()
            {
                return (Action.Pinned ? "★  " : "    ") + (Action.Name ?? string.Empty);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 編輯模型中的函式與預存程序（只改模型）：新增、刪除、修改名稱／種類／完整 CREATE 定義，
    /// 之後以「同步模型到資料庫」審核並執行。
    /// </summary>
    public sealed class ErRoutinesForm : Form
    {
        private readonly List<ErModelRoutine> routines;
        private readonly ListBox list;
        private readonly TextBox nameBox;
        private readonly ComboBox kindBox;
        private readonly TextBox definitionBox;
        private readonly Label statusLabel;
        private bool loading;

        public ErRoutinesForm(IList<ErModelRoutine> source, string providerName)
        {
            routines = (source ?? new List<ErModelRoutine>())
                .Select(item => new ErModelRoutine { Name = item.Name, Kind = item.Kind, ReturnType = item.ReturnType, Definition = item.Definition })
                .ToList();
            Text = Localization.T("ErModel.Routines");
            Width = 980;
            Height = 640;
            MinimumSize = new Size(700, 460);
            StartPosition = FormStartPosition.CenterParent;

            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            Button addButton = new Button { Text = Localization.T("ErModel.Routine.Add"), AutoSize = true };
            Button removeButton = new Button { Text = Localization.T("ErModel.Routine.Remove"), AutoSize = true };
            FlowLayoutPanel listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            listButtons.Controls.AddRange(new Control[] { addButton, removeButton });
            Panel left = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(8) };
            left.Controls.Add(list);
            left.Controls.Add(listButtons);

            nameBox = new TextBox { Dock = DockStyle.Fill };
            kindBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            kindBox.Items.AddRange(new object[] { Localization.T("ErModel.Routine.Function"), Localization.T("ErModel.Routine.Procedure") });
            definitionBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = UiKit.GetMonoFont(10f)
            };
            TableLayoutPanel editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(4, 8, 8, 8) };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.Controls.Add(new Label { Text = Localization.T("ErModel.Routine.Name"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            editor.Controls.Add(nameBox, 1, 0);
            editor.Controls.Add(new Label { Text = Localization.T("ErModel.Routine.Kind"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            editor.Controls.Add(kindBox, 1, 1);
            editor.Controls.Add(new Label { Text = Localization.T("ErModel.Routine.Definition"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 4, 0, 0) }, 0, 2);
            editor.Controls.Add(definitionBox, 1, 2);
            statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = ThemeManager.MutedTextColor,
                Text = RoutineModelService.SupportsSync(providerName) ? Localization.T("ErModel.Routine.Hint") : Localization.T("ErModel.Routine.HintNoSync")
            };
            editor.Controls.Add(statusLabel, 1, 3);

            Button okButton = new Button { Text = Localization.T("Common.OK"), AutoSize = true };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            Controls.Add(editor);
            Controls.Add(left);
            Controls.Add(buttons);
            CancelButton = cancelButton;

            list.SelectedIndexChanged += (sender, args) => ShowSelected();
            nameBox.TextChanged += (sender, args) => StoreCurrent(true);
            kindBox.SelectedIndexChanged += (sender, args) => StoreCurrent(true);
            definitionBox.TextChanged += (sender, args) => StoreCurrent(false);
            addButton.Click += (sender, args) =>
            {
                routines.Add(new ErModelRoutine { Name = Localization.T("ErModel.Routine.NewName"), Kind = RoutineModelService.FunctionKind, Definition = string.Empty });
                RefreshList(routines.Count - 1);
                nameBox.Focus();
                nameBox.SelectAll();
            };
            removeButton.Click += (sender, args) =>
            {
                int index = list.SelectedIndex;
                if (index < 0) return;
                routines.RemoveAt(index);
                RefreshList(Math.Min(index, routines.Count - 1));
            };
            okButton.Click += (sender, args) =>
            {
                try
                {
                    List<ErModelRoutine> copy = routines.Select(item => new ErModelRoutine { Name = item.Name, Kind = item.Kind, ReturnType = item.ReturnType, Definition = item.Definition }).ToList();
                    RoutineModelService.Validate(copy);
                    Result = copy;
                    DialogResult = DialogResult.OK;
                }
                catch (InvalidOperationException exception)
                {
                    statusLabel.ForeColor = ThemeManager.DangerColor;
                    statusLabel.Text = exception.Message;
                }
            };
            ThemeManager.ApplyTo(this);
            RefreshList(routines.Count > 0 ? 0 : -1);
        }

        public List<ErModelRoutine> Result { get; private set; }

        private void RefreshList(int select)
        {
            loading = true;
            try
            {
                list.Items.Clear();
                foreach (ErModelRoutine routine in routines) list.Items.Add(Describe(routine));
                list.SelectedIndex = select;
            }
            finally
            {
                loading = false;
            }
            ShowSelected();
        }

        private static string Describe(ErModelRoutine routine)
        {
            return (routine.Kind == RoutineModelService.ProcedureKind ? "P  " : "F  ") + routine.Name;
        }

        private void ShowSelected()
        {
            if (loading) return;
            loading = true;
            try
            {
                ErModelRoutine routine = list.SelectedIndex >= 0 && list.SelectedIndex < routines.Count ? routines[list.SelectedIndex] : null;
                nameBox.Enabled = kindBox.Enabled = definitionBox.Enabled = routine != null;
                nameBox.Text = routine == null ? string.Empty : routine.Name;
                kindBox.SelectedIndex = routine == null ? -1 : routine.Kind == RoutineModelService.ProcedureKind ? 1 : 0;
                definitionBox.Text = routine == null ? string.Empty : (routine.Definition ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            finally
            {
                loading = false;
            }
        }

        private void StoreCurrent(bool refreshLabel)
        {
            if (loading || list.SelectedIndex < 0 || list.SelectedIndex >= routines.Count) return;
            ErModelRoutine routine = routines[list.SelectedIndex];
            routine.Name = nameBox.Text;
            routine.Kind = kindBox.SelectedIndex == 1 ? RoutineModelService.ProcedureKind : RoutineModelService.FunctionKind;
            routine.Definition = definitionBox.Text;
            if (!refreshLabel) return;
            loading = true;
            try
            {
                list.Items[list.SelectedIndex] = Describe(routine);
            }
            finally
            {
                loading = false;
            }
        }
    }
}

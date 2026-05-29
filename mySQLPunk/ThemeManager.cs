using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    public static class ThemeManager
    {
        public const string Light = "light";
        public const string Dark = "dark";

        private static string _theme = Light;

        public static string CurrentTheme
        {
            get { return _theme; }
        }

        public static bool IsDark
        {
            get { return _theme == Dark; }
        }

        public static Color WindowBackColor => IsDark ? Color.FromArgb(30, 34, 38) : Color.White;
        public static Color SurfaceColor => IsDark ? Color.FromArgb(38, 43, 48) : Color.FromArgb(245, 245, 245);
        public static Color ElevatedColor => IsDark ? Color.FromArgb(45, 51, 57) : Color.White;
        public static Color TextColor => IsDark ? Color.FromArgb(235, 240, 244) : Color.FromArgb(51, 51, 51);
        public static Color MutedTextColor => IsDark ? Color.FromArgb(170, 181, 189) : Color.FromArgb(105, 105, 105);
        public static Color BorderColor => IsDark ? Color.FromArgb(70, 78, 86) : Color.FromArgb(224, 224, 224);
        public static Color GridColor => IsDark ? Color.FromArgb(58, 65, 72) : Color.FromArgb(240, 240, 240);
        public static Color SelectionColor => IsDark ? Color.FromArgb(35, 95, 135) : Color.FromArgb(220, 235, 255);
        public static Color SelectionTextColor => IsDark ? Color.White : Color.FromArgb(26, 55, 82);
        public static Color AccentColor => IsDark ? Color.FromArgb(80, 170, 220) : Color.FromArgb(0, 120, 212);
        public static Color HoverColor => IsDark ? Color.FromArgb(48, 73, 88) : Color.FromArgb(232, 242, 255);
        public static Color ButtonBackColor => IsDark ? Color.FromArgb(54, 61, 68) : Color.FromArgb(245, 245, 245);
        public static Color TextBoxBackColor => IsDark ? Color.FromArgb(25, 29, 33) : Color.White;
        public static Color DisabledBackColor => IsDark ? Color.FromArgb(36, 40, 45) : Color.FromArgb(240, 240, 240);
        public static Color DisabledTextColor => IsDark ? Color.FromArgb(125, 135, 145) : Color.FromArgb(130, 130, 130);

        public static void Load()
        {
            try
            {
                string path = GetThemeFilePath();
                if (File.Exists(path))
                {
                    SetTheme(File.ReadAllText(path).Trim(), false);
                }
            }
            catch
            {
                _theme = Light;
            }
        }

        public static void SetTheme(string theme, bool save)
        {
            _theme = theme == Dark ? Dark : Light;
            if (!save) return;

            try
            {
                string path = GetThemeFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, _theme);
            }
            catch
            {
            }
        }

        public static void ApplyTo(Control root)
        {
            if (root == null) return;

            root.BackColor = WindowBackColor;
            root.ForeColor = TextColor;

            Form form = root as Form;
            if (form != null)
            {
                form.Opacity = 1.0f;
                form.BackColor = WindowBackColor;
                form.ForeColor = TextColor;
                form.Font = new Font("Microsoft JhengHei UI", 9F, FontStyle.Regular);
            }

            ApplyControl(root);

            foreach (Control child in root.Controls)
            {
                ApplyTo(child);
            }
        }

        private static void ApplyControl(Control control)
        {
            if (control.ContextMenuStrip != null)
            {
                ApplyToolStrip(control.ContextMenuStrip);
            }

            ToolStrip toolStrip = control as ToolStrip;
            if (toolStrip != null)
            {
                ApplyToolStrip(toolStrip);
                return;
            }

            MenuStrip menuStrip = control as MenuStrip;
            if (menuStrip != null)
            {
                ApplyToolStrip(menuStrip);
                return;
            }

            StatusStrip statusStrip = control as StatusStrip;
            if (statusStrip != null)
            {
                ApplyToolStrip(statusStrip);
                return;
            }

            TreeView treeView = control as TreeView;
            if (treeView != null)
            {
                ApplyTreeView(treeView);
                return;
            }

            DataGridView dgv = control as DataGridView;
            if (dgv != null)
            {
                ApplyDataGridView(dgv);
                return;
            }

            UpDownBase upDown = control as UpDownBase;
            if (upDown != null)
            {
                upDown.BackColor = TextBoxBackColor;
                upDown.ForeColor = TextColor;
                upDown.BorderStyle = BorderStyle.FixedSingle;
                upDown.Margin = new Padding(3, 4, 3, 4);
                return;
            }

            DateTimePicker dateTimePicker = control as DateTimePicker;
            if (dateTimePicker != null)
            {
                dateTimePicker.BackColor = TextBoxBackColor;
                dateTimePicker.ForeColor = TextColor;
                dateTimePicker.CalendarMonthBackground = TextBoxBackColor;
                dateTimePicker.CalendarForeColor = TextColor;
                dateTimePicker.CalendarTitleBackColor = SurfaceColor;
                dateTimePicker.CalendarTitleForeColor = TextColor;
                dateTimePicker.CalendarTrailingForeColor = MutedTextColor;
                dateTimePicker.Margin = new Padding(3, 4, 3, 4);
                return;
            }

            MonthCalendar monthCalendar = control as MonthCalendar;
            if (monthCalendar != null)
            {
                monthCalendar.BackColor = TextBoxBackColor;
                monthCalendar.ForeColor = TextColor;
                monthCalendar.TitleBackColor = SurfaceColor;
                monthCalendar.TitleForeColor = TextColor;
                monthCalendar.TrailingForeColor = MutedTextColor;
                return;
            }

            TextBoxBase textBox = control as TextBoxBase;
            if (textBox != null)
            {
                textBox.BackColor = textBox.ReadOnly ? DisabledBackColor : TextBoxBackColor;
                textBox.ForeColor = textBox.Enabled ? TextColor : DisabledTextColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.Margin = new Padding(3, 4, 3, 4);
                return;
            }

            ComboBox comboBox = control as ComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = comboBox.Enabled ? TextBoxBackColor : DisabledBackColor;
                comboBox.ForeColor = comboBox.Enabled ? TextColor : DisabledTextColor;
                comboBox.FlatStyle = FlatStyle.Flat;
                comboBox.Margin = new Padding(3, 4, 3, 4);
                return;
            }

            Button button = control as Button;
            if (button != null)
            {
                button.UseVisualStyleBackColor = false;
                button.BackColor = button.Enabled ? ButtonBackColor : DisabledBackColor;
                button.ForeColor = button.Enabled ? TextColor : DisabledTextColor;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = BorderColor;
                button.FlatAppearance.MouseOverBackColor = HoverColor;
                button.FlatAppearance.MouseDownBackColor = SelectionColor;
                button.Padding = new Padding(8, 2, 8, 2);
                button.MinimumSize = new Size(Math.Max(button.MinimumSize.Width, 0), Math.Max(button.MinimumSize.Height, 28));
                button.Cursor = Cursors.Hand;
                return;
            }

            TabControl tabControl = control as TabControl;
            if (tabControl != null)
            {
                tabControl.BackColor = WindowBackColor;
                tabControl.ForeColor = TextColor;
                tabControl.Padding = new Point(Math.Max(tabControl.Padding.X, 12), Math.Max(tabControl.Padding.Y, 5));
                return;
            }

            TabPage tabPage = control as TabPage;
            if (tabPage != null)
            {
                tabPage.UseVisualStyleBackColor = false;
                tabPage.BackColor = WindowBackColor;
                tabPage.ForeColor = TextColor;
                return;
            }

            Panel panel = control as Panel;
            if (panel != null)
            {
                panel.BackColor = WindowBackColor;
                panel.ForeColor = TextColor;
                return;
            }

            SplitContainer splitContainer = control as SplitContainer;
            if (splitContainer != null)
            {
                splitContainer.BackColor = BorderColor;
                splitContainer.Panel1.BackColor = WindowBackColor;
                splitContainer.Panel2.BackColor = WindowBackColor;
                return;
            }

            CheckedListBox checkedListBox = control as CheckedListBox;
            if (checkedListBox != null)
            {
                checkedListBox.BackColor = ElevatedColor;
                checkedListBox.ForeColor = TextColor;
                checkedListBox.BorderStyle = BorderStyle.None;
                checkedListBox.IntegralHeight = false;
                checkedListBox.ItemHeight = Math.Max(checkedListBox.ItemHeight, 24);
                return;
            }

            ListBox listBox = control as ListBox;
            if (listBox != null)
            {
                listBox.BackColor = ElevatedColor;
                listBox.ForeColor = TextColor;
                listBox.BorderStyle = BorderStyle.None;
                listBox.IntegralHeight = false;
                listBox.ItemHeight = Math.Max(listBox.ItemHeight, 24);
                return;
            }

            GroupBox groupBox = control as GroupBox;
            if (groupBox != null)
            {
                groupBox.BackColor = WindowBackColor;
                groupBox.ForeColor = TextColor;
                return;
            }

            CheckBox checkBox = control as CheckBox;
            if (checkBox != null)
            {
                checkBox.BackColor = Color.Transparent;
                checkBox.ForeColor = TextColor;
                checkBox.FlatStyle = FlatStyle.Standard;
                return;
            }

            RadioButton radioButton = control as RadioButton;
            if (radioButton != null)
            {
                radioButton.BackColor = Color.Transparent;
                radioButton.ForeColor = TextColor;
                radioButton.FlatStyle = FlatStyle.Standard;
                return;
            }

            LinkLabel linkLabel = control as LinkLabel;
            if (linkLabel != null)
            {
                linkLabel.BackColor = Color.Transparent;
                linkLabel.ForeColor = TextColor;
                linkLabel.LinkColor = AccentColor;
                linkLabel.ActiveLinkColor = SelectionTextColor;
                linkLabel.VisitedLinkColor = MutedTextColor;
                return;
            }

            Label label = control as Label;
            if (label != null)
            {
                label.BackColor = Color.Transparent;
                label.ForeColor = label.ForeColor == Color.Gray || label.ForeColor == Color.DarkGray ? MutedTextColor : TextColor;
                return;
            }
        }

        public static void ApplyToolStrip(ToolStrip strip)
        {
            if (strip == null) return;
            strip.BackColor = SurfaceColor;
            strip.ForeColor = TextColor;
            strip.Renderer = new ThemedToolStripRenderer();
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.Padding = new Padding(4, 2, 4, 2);

            foreach (ToolStripItem item in strip.Items)
            {
                ApplyToolStripItem(item);
            }
        }

        private static void ApplyToolStripItem(ToolStripItem item)
        {
            item.BackColor = SurfaceColor;
            item.ForeColor = TextColor;
            item.Margin = new Padding(1, 1, 1, 1);
            item.Padding = new Padding(4, 2, 4, 2);

            ToolStripTextBox textBox = item as ToolStripTextBox;
            if (textBox != null)
            {
                textBox.BackColor = TextBoxBackColor;
                textBox.ForeColor = TextColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }

            ToolStripDropDownItem dropDown = item as ToolStripDropDownItem;
            if (dropDown == null) return;

            dropDown.DropDown.BackColor = SurfaceColor;
            dropDown.DropDown.ForeColor = TextColor;
            dropDown.DropDown.Renderer = new ThemedToolStripRenderer();
            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                ApplyToolStripItem(child);
            }
        }

        private static void ApplyTreeView(TreeView treeView)
        {
            treeView.BackColor = ElevatedColor;
            treeView.ForeColor = TextColor;
            treeView.LineColor = BorderColor;
            treeView.BorderStyle = BorderStyle.None;
            treeView.FullRowSelect = true;
            treeView.ItemHeight = 24;
            treeView.HideSelection = false;
            treeView.DrawNode -= TreeView_DrawNode;
            treeView.DrawMode = TreeViewDrawMode.OwnerDrawText;
            treeView.DrawNode += TreeView_DrawNode;
        }

        private static void TreeView_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            TreeView treeView = sender as TreeView;
            if (treeView == null || e.Node == null)
            {
                e.DrawDefault = true;
                return;
            }

            bool selected = (e.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
            Color backColor = selected ? SelectionColor : treeView.BackColor;
            Color textColor = selected
                ? SelectionTextColor
                : (e.Node.ForeColor.IsEmpty ? treeView.ForeColor : e.Node.ForeColor);

            Rectangle bounds = e.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            using (SolidBrush brush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(brush, bounds);
            }

            TextRenderer.DrawText(
                e.Graphics,
                e.Node.Text,
                treeView.Font,
                bounds,
                textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private static void ApplyDataGridView(DataGridView dgv)
        {
            dgv.BackgroundColor = WindowBackColor;
            dgv.GridColor = GridColor;
            dgv.BorderStyle = BorderStyle.None;
            dgv.EnableHeadersVisualStyles = false;
            dgv.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            dgv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgv.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgv.RowTemplate.Height = Math.Max(dgv.RowTemplate.Height, 28);
            dgv.ColumnHeadersHeight = Math.Max(dgv.ColumnHeadersHeight, 30);
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            dgv.DefaultCellStyle.BackColor = ElevatedColor;
            dgv.DefaultCellStyle.ForeColor = TextColor;
            dgv.DefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.DefaultCellStyle.SelectionForeColor = SelectionTextColor;
            dgv.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);

            dgv.RowsDefaultCellStyle.BackColor = ElevatedColor;
            dgv.RowsDefaultCellStyle.ForeColor = TextColor;
            dgv.RowsDefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.RowsDefaultCellStyle.SelectionForeColor = SelectionTextColor;
            dgv.RowsDefaultCellStyle.Padding = new Padding(6, 2, 6, 2);

            dgv.AlternatingRowsDefaultCellStyle.BackColor = IsDark ? Color.FromArgb(34, 39, 44) : Color.FromArgb(250, 250, 250);
            dgv.AlternatingRowsDefaultCellStyle.ForeColor = TextColor;
            dgv.AlternatingRowsDefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.AlternatingRowsDefaultCellStyle.SelectionForeColor = SelectionTextColor;
            dgv.AlternatingRowsDefaultCellStyle.Padding = new Padding(6, 2, 6, 2);

            dgv.ColumnHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextColor;
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font(dgv.Font, FontStyle.Bold);

            dgv.RowHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.RowHeadersDefaultCellStyle.ForeColor = TextColor;
            dgv.RowHeadersDefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.RowHeadersDefaultCellStyle.SelectionForeColor = SelectionTextColor;
            dgv.RowHeadersDefaultCellStyle.Padding = new Padding(4, 2, 4, 2);

            dgv.TopLeftHeaderCell.Style.BackColor = SurfaceColor;
            dgv.TopLeftHeaderCell.Style.ForeColor = TextColor;
            dgv.TopLeftHeaderCell.Style.SelectionBackColor = SurfaceColor;
            dgv.TopLeftHeaderCell.Style.SelectionForeColor = TextColor;

            ApplyDataGridViewSelectionStyles(dgv);
        }

        private static void ApplyDataGridViewSelectionStyles(DataGridView dgv)
        {
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row == null) continue;
                row.DefaultCellStyle.SelectionBackColor = SelectionColor;
                row.DefaultCellStyle.SelectionForeColor = SelectionTextColor;

                foreach (DataGridViewCell cell in row.Cells)
                {
                    if (cell == null) continue;
                    cell.Style.SelectionBackColor = SelectionColor;
                    cell.Style.SelectionForeColor = SelectionTextColor;
                }
            }
        }

        private static string GetThemeFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "theme.txt");
        }

        private class ThemedColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => SurfaceColor;
            public override Color ToolStripGradientMiddle => SurfaceColor;
            public override Color ToolStripGradientEnd => SurfaceColor;
            public override Color ToolStripBorder => BorderColor;
            public override Color MenuBorder => BorderColor;
            public override Color MenuItemBorder => AccentColor;
            public override Color MenuItemSelected => HoverColor;
            public override Color MenuItemSelectedGradientBegin => HoverColor;
            public override Color MenuItemSelectedGradientEnd => HoverColor;
            public override Color MenuItemPressedGradientBegin => SelectionColor;
            public override Color MenuItemPressedGradientMiddle => SelectionColor;
            public override Color MenuItemPressedGradientEnd => SelectionColor;
            public override Color ImageMarginGradientBegin => SurfaceColor;
            public override Color ImageMarginGradientMiddle => SurfaceColor;
            public override Color ImageMarginGradientEnd => SurfaceColor;
            public override Color SeparatorDark => BorderColor;
            public override Color SeparatorLight => BorderColor;
            public override Color ButtonSelectedGradientBegin => HoverColor;
            public override Color ButtonSelectedGradientEnd => HoverColor;
            public override Color ButtonPressedGradientBegin => SelectionColor;
            public override Color ButtonPressedGradientEnd => SelectionColor;
            public override Color ButtonCheckedGradientBegin => SelectionColor;
            public override Color ButtonCheckedGradientEnd => SelectionColor;
        }

        private class ThemedToolStripRenderer : ToolStripProfessionalRenderer
        {
            public ThemedToolStripRenderer() : base(new ThemedColorTable())
            {
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(SurfaceColor))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (Pen pen = new Pen(BorderColor))
                {
                    Rectangle bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                    e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
                }
            }
        }
    }
}

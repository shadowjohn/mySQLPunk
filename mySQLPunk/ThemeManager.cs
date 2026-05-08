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
        public static Color AccentColor => IsDark ? Color.FromArgb(80, 170, 220) : Color.FromArgb(0, 120, 212);
        public static Color ButtonBackColor => IsDark ? Color.FromArgb(54, 61, 68) : Color.FromArgb(245, 245, 245);
        public static Color TextBoxBackColor => IsDark ? Color.FromArgb(25, 29, 33) : Color.White;

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
            }

            ApplyControl(root);

            foreach (Control child in root.Controls)
            {
                ApplyTo(child);
            }
        }

        private static void ApplyControl(Control control)
        {
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
                treeView.BackColor = ElevatedColor;
                treeView.ForeColor = TextColor;
                treeView.LineColor = BorderColor;
                treeView.BorderStyle = BorderStyle.None;
                treeView.FullRowSelect = true;
                treeView.ItemHeight = 24;
                return;
            }

            DataGridView dgv = control as DataGridView;
            if (dgv != null)
            {
                ApplyDataGridView(dgv);
                return;
            }

            TextBoxBase textBox = control as TextBoxBase;
            if (textBox != null)
            {
                textBox.BackColor = TextBoxBackColor;
                textBox.ForeColor = TextColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                return;
            }

            ComboBox comboBox = control as ComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = TextBoxBackColor;
                comboBox.ForeColor = TextColor;
                return;
            }

            Button button = control as Button;
            if (button != null)
            {
                button.UseVisualStyleBackColor = false;
                button.BackColor = ButtonBackColor;
                button.ForeColor = TextColor;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = BorderColor;
                return;
            }

            TabControl tabControl = control as TabControl;
            if (tabControl != null)
            {
                tabControl.BackColor = WindowBackColor;
                tabControl.ForeColor = TextColor;
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

            foreach (ToolStripItem item in strip.Items)
            {
                ApplyToolStripItem(item);
            }
        }

        private static void ApplyToolStripItem(ToolStripItem item)
        {
            item.BackColor = SurfaceColor;
            item.ForeColor = TextColor;

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

        private static void ApplyDataGridView(DataGridView dgv)
        {
            dgv.BackgroundColor = WindowBackColor;
            dgv.GridColor = GridColor;
            dgv.BorderStyle = BorderStyle.None;
            dgv.EnableHeadersVisualStyles = false;

            dgv.DefaultCellStyle.BackColor = ElevatedColor;
            dgv.DefaultCellStyle.ForeColor = TextColor;
            dgv.DefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.DefaultCellStyle.SelectionForeColor = Color.White;

            dgv.AlternatingRowsDefaultCellStyle.BackColor = IsDark ? Color.FromArgb(34, 39, 44) : Color.FromArgb(250, 250, 250);
            dgv.AlternatingRowsDefaultCellStyle.ForeColor = TextColor;
            dgv.AlternatingRowsDefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;

            dgv.ColumnHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextColor;

            dgv.RowHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.RowHeadersDefaultCellStyle.ForeColor = TextColor;
            dgv.RowHeadersDefaultCellStyle.SelectionBackColor = SelectionColor;
            dgv.RowHeadersDefaultCellStyle.SelectionForeColor = Color.White;
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
            public override Color MenuItemSelected => SelectionColor;
            public override Color MenuItemSelectedGradientBegin => SelectionColor;
            public override Color MenuItemSelectedGradientEnd => SelectionColor;
            public override Color MenuItemPressedGradientBegin => SelectionColor;
            public override Color MenuItemPressedGradientMiddle => SelectionColor;
            public override Color MenuItemPressedGradientEnd => SelectionColor;
            public override Color ImageMarginGradientBegin => SurfaceColor;
            public override Color ImageMarginGradientMiddle => SurfaceColor;
            public override Color ImageMarginGradientEnd => SurfaceColor;
            public override Color SeparatorDark => BorderColor;
            public override Color SeparatorLight => BorderColor;
            public override Color ButtonSelectedGradientBegin => SelectionColor;
            public override Color ButtonSelectedGradientEnd => SelectionColor;
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

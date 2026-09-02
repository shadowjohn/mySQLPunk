using System.Drawing;
using System.Windows.Forms;

namespace mySQLPunk.template
{
    /// <summary>
    /// 連線編輯對話框的共用版面：標題列（引擎色圖示＋名稱）、對齊的欄位格線、底部按鈕列。
    /// 五個 provider 的表單都走這裡，長相才會一致。
    /// </summary>
    internal static class ConnectionDialogUi
    {
        // 與樹狀清單圖示同一組引擎色
        public static readonly Color MySqlColor = Color.FromArgb(0xDD, 0x8A, 0x24);
        public static readonly Color PostgresColor = Color.FromArgb(0x33, 0x67, 0x91);
        public static readonly Color OracleColor = Color.FromArgb(0xC7, 0x46, 0x34);
        public static readonly Color SqliteColor = Color.FromArgb(0x0F, 0x80, 0xCC);
        public static readonly Color SqlServerColor = Color.FromArgb(0x8E, 0x44, 0xAD);

        public const int LabelColumnWidth = 148;
        public const int FieldWide = 320;
        public const int FieldMedium = 200;
        public const int FieldNarrow = 90;

        public sealed class Shell
        {
            public TableLayoutPanel Fields;
            public TableLayoutPanel Footer;
        }

        public static Shell Build(Form form, string providerName, Color engineColor)
        {
            form.AutoScaleMode = AutoScaleMode.None;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.AutoSize = true;
            form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            form.Padding = new Padding(20, 16, 20, 16);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel header = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0)
            };
            PictureBox glyph = new PictureBox
            {
                Size = new Size(22, 22),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = UiKit.RenderGlyph(UiGlyph.Database, 22, engineColor),
                Margin = new Padding(0, 1, 8, 0)
            };
            Label title = new Label
            {
                AutoSize = true,
                Text = providerName,
                Font = UiKit.Subtitle,
                Margin = new Padding(0, 3, 0, 0)
            };
            header.Controls.Add(glyph);
            header.Controls.Add(title);

            UiDivider topDivider = new UiDivider
            {
                Height = 1,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 8, 0, 14)
            };

            TableLayoutPanel fields = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Margin = new Padding(0)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            UiDivider bottomDivider = new UiDivider
            {
                Height = 1,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 14, 0, 12)
            };

            TableLayoutPanel footer = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(topDivider, 0, 1);
            root.Controls.Add(fields, 0, 2);
            root.Controls.Add(bottomDivider, 0, 3);
            root.Controls.Add(footer, 0, 4);
            form.Controls.Add(root);

            return new Shell { Fields = fields, Footer = footer };
        }

        /// <summary>加一列「標籤＋輸入欄位」。width 用 FieldWide / FieldMedium / FieldNarrow，0 表示沿用控制項現有大小。</summary>
        public static void AddField(Shell shell, Label label, Control field, int width)
        {
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, 12, 0);
            Control cell = WrapInput(field);
            StyleField(cell, width);

            int row = NextRow(shell.Fields);
            shell.Fields.Controls.Add(label, 0, row);
            shell.Fields.Controls.Add(cell, 1, row);
        }

        /// <summary>所有可編輯欄位都交給共用外殼，避免文字、選單與數字欄位長相不一致。</summary>
        public static Control WrapInput(Control field)
        {
            return UiField.Wrap(field);
        }

        /// <summary>加一列只有輸入側的控制項（核取方塊、次要按鈕這類）。</summary>
        public static void AddFieldOnly(Shell shell, Control control)
        {
            control.Margin = new Padding(0, 4, 0, 4);
            if (control is CheckBox checkBox) checkBox.AutoSize = true;

            int row = NextRow(shell.Fields);
            shell.Fields.Controls.Add(control, 1, row);
        }

        /// <summary>加一列橫跨兩欄的控制項（Oracle 的 Basic／TNS 切換面板用）。</summary>
        public static void AddSpanRow(Shell shell, Control control)
        {
            control.Margin = new Padding(0);
            int row = NextRow(shell.Fields);
            shell.Fields.Controls.Add(control, 0, row);
            shell.Fields.SetColumnSpan(control, 2);
        }

        public static void StyleField(Control field, int width)
        {
            field.Anchor = AnchorStyles.Left;
            field.Margin = new Padding(0, 4, 0, 4);
            if (width > 0) field.Width = width;
        }

        /// <summary>底部按鈕列：測試連線靠左，確定／取消靠右；同時掛上 Enter／Esc。</summary>
        public static void Finish(Form form, Shell shell, Button test, Button ok, Button cancel)
        {
            // 五個連線視窗統一掛應用程式圖示（看板娘 Punky）
            try { form.Icon = mySQLPunk.lib.AppIconService.AppIcon; } catch { }

            StyleButton(test);
            StyleButton(ok);
            StyleButton(cancel);
            ok.Margin = new Padding(8, 0, 0, 0);
            cancel.Margin = new Padding(8, 0, 0, 0);

            shell.Footer.Controls.Add(test, 0, 0);
            shell.Footer.Controls.Add(ok, 2, 0);
            shell.Footer.Controls.Add(cancel, 3, 0);

            form.AcceptButton = ok;
            form.CancelButton = cancel;
        }

        private static void StyleButton(Button button)
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(UiMetrics.ButtonMinWidth, UiMetrics.ControlHeight);
            button.Padding = new Padding(10, 2, 10, 2);
            button.Margin = new Padding(0);
        }

        private static int NextRow(TableLayoutPanel table)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return row;
        }
    }
}

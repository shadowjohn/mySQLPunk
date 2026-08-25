using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    /// <summary>
    /// 全域佈景主題與樣式套用。這裡是整個介面唯一的顏色來源，
    /// 量值（間距、圓角、字級）則放在 <c>UiMetrics</c> / <c>UiKit</c>。
    /// </summary>
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

        // ── 顏色 token ────────────────────────────────────────────────
        // 淺色：白色內容區 + 淺灰工具列，深色：三階灰做出層次

        /// <summary>內容區底色（資料表、編輯器等主要工作區）。</summary>
        public static Color WindowBackColor => IsDark ? Color.FromArgb(28, 31, 36) : Color.White;

        /// <summary>介面外框底色（功能表、工具列、標題列、狀態列）。</summary>
        public static Color SurfaceColor => IsDark ? Color.FromArgb(35, 39, 45) : Color.FromArgb(246, 247, 249);

        /// <summary>浮起層底色（清單、樹狀、卡片）。</summary>
        public static Color ElevatedColor => IsDark ? Color.FromArgb(40, 45, 51) : Color.White;

        public static Color TextColor => IsDark ? Color.FromArgb(230, 234, 239) : Color.FromArgb(31, 35, 40);

        /// <summary>次要文字，例如說明、單位、欄位標籤。</summary>
        public static Color MutedTextColor => IsDark ? Color.FromArgb(155, 165, 176) : Color.FromArgb(92, 102, 114);

        /// <summary>第三階文字，例如浮水印、空狀態提示。</summary>
        public static Color SubtleTextColor => IsDark ? Color.FromArgb(110, 120, 131) : Color.FromArgb(139, 149, 161);

        public static Color BorderColor => IsDark ? Color.FromArgb(52, 58, 66) : Color.FromArgb(228, 231, 236);

        /// <summary>需要更明確界線時使用，例如輸入框、被選取的分段。</summary>
        public static Color BorderStrongColor => IsDark ? Color.FromArgb(69, 76, 85) : Color.FromArgb(207, 213, 221);

        public static Color GridColor => IsDark ? Color.FromArgb(46, 52, 59) : Color.FromArgb(237, 239, 243);

        public static Color SelectionColor => IsDark ? Color.FromArgb(42, 68, 103) : Color.FromArgb(221, 233, 253);
        public static Color SelectionTextColor => IsDark ? Color.FromArgb(234, 242, 255) : Color.FromArgb(18, 58, 110);

        public static Color AccentColor => IsDark ? Color.FromArgb(106, 166, 255) : Color.FromArgb(37, 99, 235);
        public static Color AccentHoverColor => IsDark ? Color.FromArgb(140, 187, 255) : Color.FromArgb(29, 78, 216);

        /// <summary>強調色的淡底，用在被選取的工具列項目、分頁底色。</summary>
        public static Color AccentSoftColor => IsDark ? Color.FromArgb(35, 50, 74) : Color.FromArgb(238, 243, 254);

        public static Color HoverColor => IsDark ? Color.FromArgb(44, 50, 58) : Color.FromArgb(239, 241, 245);

        public static Color ButtonBackColor => IsDark ? Color.FromArgb(43, 49, 56) : Color.White;
        public static Color TextBoxBackColor => IsDark ? Color.FromArgb(26, 29, 34) : Color.White;
        public static Color DisabledBackColor => IsDark ? Color.FromArgb(36, 40, 46) : Color.FromArgb(242, 244, 247);
        public static Color DisabledTextColor => IsDark ? Color.FromArgb(106, 115, 125) : Color.FromArgb(160, 168, 178);

        public static Color SuccessColor => IsDark ? Color.FromArgb(74, 194, 130) : Color.FromArgb(22, 143, 84);
        public static Color WarningColor => IsDark ? Color.FromArgb(232, 170, 74) : Color.FromArgb(191, 122, 12);
        public static Color DangerColor => IsDark ? Color.FromArgb(240, 116, 116) : Color.FromArgb(203, 47, 47);

        // ── 主題存取 ──────────────────────────────────────────────────

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

        private static string GetThemeFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "theme.txt");
        }

        // ── 樣式標記 ──────────────────────────────────────────────────
        // 用附掛屬性的方式記住控制項要用哪種變體，避免污染呼叫端的 Tag。

        private enum ToolStripVariant
        {
            Compact,
            Ribbon,
            Menu,
            Status
        }

        private static readonly Dictionary<ToolStrip, ToolStripVariant> StripVariants = new Dictionary<ToolStrip, ToolStripVariant>();
        private static readonly Dictionary<Button, ButtonVisualState> ButtonStates = new Dictionary<Button, ButtonVisualState>();
        private static readonly HashSet<SplitContainer> StyledSplitters = new HashSet<SplitContainer>();
        private static readonly HashSet<TabControl> StyledTabs = new HashSet<TabControl>();
        private class GlyphSpec
        {
            public UiGlyph Glyph;
            public Color Tint;
            public bool HasTint;
        }

        private static readonly Dictionary<ToolStripItem, GlyphSpec> ItemGlyphs = new Dictionary<ToolStripItem, GlyphSpec>();

        // 記錄哪些 Image 是 SetGlyph 自己建立的透明佔位圖。
        // Properties.Resources 每次回傳同一個共用實例，Dispose 它會讓其他按鈕畫到已釋放的圖。
        private static readonly HashSet<Image> OwnedPlaceholderImages = new HashSet<Image>();
        private static readonly HashSet<ListBox> NavigationLists = new HashSet<ListBox>();

        /// <summary>
        /// 指定工具列項目要用哪個向量圖示。圖示會在繪製時即時上色，
        /// 因此切換主題或選取狀態都不需要重新產生點陣圖。
        /// </summary>
        /// <param name="reserveSize">為了讓版面配置預留圖示空間而放的透明佔位圖尺寸。</param>
        public static void SetGlyph(ToolStripItem item, UiGlyph glyph, int reserveSize)
        {
            SetGlyph(item, glyph, reserveSize, Color.Empty);
        }

        /// <summary>同上，但指定固定色，用於「套用」「取消」這類帶語意的動作。</summary>
        public static void SetGlyph(ToolStripItem item, UiGlyph glyph, int reserveSize, Color tint)
        {
            if (item == null) return;

            if (glyph == UiGlyph.None)
            {
                ItemGlyphs.Remove(item);
                Image cleared = item.Image;
                if (cleared != null && OwnedPlaceholderImages.Remove(cleared))
                {
                    item.Image = null;
                    cleared.Dispose();
                }
                return;
            }

            ItemGlyphs[item] = new GlyphSpec { Glyph = glyph, Tint = tint, HasTint = !tint.IsEmpty };

            // ToolStrip 只有在 Image 不為 null 時才會保留圖示區並呼叫 OnRenderItemImage
            Image previous = item.Image;
            Bitmap placeholder = new Bitmap(Math.Max(1, reserveSize), Math.Max(1, reserveSize));
            OwnedPlaceholderImages.Add(placeholder);
            item.Image = placeholder;
            item.ImageScaling = ToolStripItemImageScaling.None;
            if (previous != null && OwnedPlaceholderImages.Remove(previous)) previous.Dispose();

            item.Disposed -= ToolStripItem_Disposed;
            item.Disposed += ToolStripItem_Disposed;
        }

        private static void ToolStripItem_Disposed(object sender, EventArgs e)
        {
            ToolStripItem item = sender as ToolStripItem;
            if (item == null) return;
            ItemGlyphs.Remove(item);
            try
            {
                Image current = item.Image;
                if (current != null && OwnedPlaceholderImages.Remove(current))
                {
                    current.Dispose();
                }
            }
            catch { }
        }

        private static bool TryGetGlyph(ToolStripItem item, out GlyphSpec spec)
        {
            spec = null;
            return item != null && ItemGlyphs.TryGetValue(item, out spec);
        }

        /// <summary>把工具列標記成「主功能列」，選取項目會有強調底色與底線。</summary>
        public static void MarkAsRibbon(ToolStrip strip)
        {
            if (strip == null) return;
            bool alreadyKnown = StripVariants.ContainsKey(strip);
            StripVariants[strip] = ToolStripVariant.Ribbon;
            // 語言/主題切換會重複呼叫，Disposed 只能掛一次
            if (!alreadyKnown) strip.Disposed += (s, e) => StripVariants.Remove(strip);
        }

        private static ToolStripVariant GetVariant(ToolStrip strip)
        {
            ToolStripVariant variant;
            if (StripVariants.TryGetValue(strip, out variant)) return variant;
            if (strip is MenuStrip) return ToolStripVariant.Menu;
            if (strip is StatusStrip) return ToolStripVariant.Status;
            return ToolStripVariant.Compact;
        }

        /// <summary>把按鈕標記成主要動作（實心強調色）。</summary>
        public static void MarkAsPrimary(Button button)
        {
            if (button == null) return;
            ButtonVisualState state = EnsureButtonState(button);
            state.IsPrimary = true;
            button.Invalidate();
        }

        /// <summary>把按鈕標記成危險動作（刪除、清空等）。</summary>
        public static void MarkAsDanger(Button button)
        {
            if (button == null) return;
            ButtonVisualState state = EnsureButtonState(button);
            state.IsDanger = true;
            button.Invalidate();
        }

        /// <summary>
        /// 把 ListBox 變成側邊導覽樣式：整條藍色反白改成圓角淡底，
        /// 項目也拉開留白，看起來才像設定視窗的分頁而不是原始清單。
        /// </summary>
        public static void StyleNavigationList(ListBox list)
        {
            if (list == null) return;

            list.BorderStyle = BorderStyle.None;
            list.BackColor = SurfaceColor;
            list.ForeColor = TextColor;
            list.IntegralHeight = false;
            list.ItemHeight = 32;
            list.Font = UiKit.Body;
            list.DrawMode = DrawMode.OwnerDrawFixed;

            if (NavigationLists.Contains(list)) return;
            NavigationLists.Add(list);
            list.DrawItem += NavigationList_DrawItem;
            list.Disposed += (s, e) => NavigationLists.Remove(list);
        }

        private static void NavigationList_DrawItem(object sender, DrawItemEventArgs e)
        {
            ListBox list = sender as ListBox;
            if (list == null || e.Index < 0 || e.Index >= list.Items.Count) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush brush = new SolidBrush(list.BackColor))
            {
                g.FillRectangle(brush, e.Bounds);
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Rectangle pill = new Rectangle(
                e.Bounds.X + UiMetrics.Space2,
                e.Bounds.Y + 2,
                Math.Max(1, e.Bounds.Width - UiMetrics.Space2 * 2),
                Math.Max(1, e.Bounds.Height - 4));

            if (selected)
            {
                UiKit.FillRounded(g, pill, UiMetrics.RadiusMd, AccentSoftColor);
            }

            Rectangle textBounds = new Rectangle(
                pill.X + UiMetrics.Space3,
                pill.Y,
                Math.Max(1, pill.Width - UiMetrics.Space3 * 2),
                pill.Height);

            object listItem = list.Items[e.Index];
            UiKit.DrawText(g,
                listItem == null ? string.Empty : listItem.ToString(),
                selected ? UiKit.BodyBold : UiKit.Body,
                textBounds,
                selected ? AccentColor : MutedTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        /// <summary>讓按鈕以向量圖示取代文字，顏色跟著按鈕狀態走。</summary>
        public static void SetGlyph(Button button, UiGlyph glyph)
        {
            if (button == null) return;
            ButtonVisualState state = EnsureButtonState(button);
            state.Glyph = glyph;
            button.Invalidate();
        }

        /// <summary>切換按鈕的「已選取」外觀，用於檢視模式這類雙態切換。</summary>
        public static void SetToggled(Button button, bool toggled)
        {
            if (button == null) return;
            ButtonVisualState state = EnsureButtonState(button);
            if (state.IsToggled == toggled) return;
            state.IsToggled = toggled;
            button.Invalidate();
        }

        // ── 套用入口 ──────────────────────────────────────────────────

        public static void ApplyTo(Control root)
        {
            if (root == null) return;

            Form form = root as Form;
            if (form != null)
            {
                form.Opacity = 1.0f;
                form.BackColor = WindowBackColor;
                form.ForeColor = TextColor;
                form.Font = UiKit.Body;
                // 所有視窗統一掛看板娘 Punky 的應用程式圖示（標題列與工作列都會用到）
                try { form.Icon = mySQLPunk.lib.AppIconService.AppIcon; } catch { }
            }
            else
            {
                root.BackColor = WindowBackColor;
                root.ForeColor = TextColor;
            }

            ApplyControl(root);

            // ToolStrip 的 Controls 是 ToolStripControlHost 內嵌的原生控制項，
            // 直接改它們的尺寸會反推回 item 大小、把整條工具列撐高
            if (!(root is ToolStrip))
            {
                foreach (Control child in root.Controls)
                {
                    ApplyTo(child);
                }
            }

            // 對話框的預設按鈕自動升級為主要動作，讓每個視窗都有明確的主行動點
            if (form != null && form.AcceptButton is Button)
            {
                MarkAsPrimary((Button)form.AcceptButton);
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

            RichTextBox richTextBox = control as RichTextBox;
            if (richTextBox != null)
            {
                richTextBox.BackColor = richTextBox.ReadOnly ? SurfaceColor : TextBoxBackColor;
                richTextBox.ForeColor = TextColor;
                richTextBox.BorderStyle = BorderStyle.None;
                return;
            }

            TextBoxBase textBox = control as TextBoxBase;
            if (textBox != null)
            {
                // UiInputShell 內的文字框：外殼負責邊框與底色，這裡別把 BorderStyle 改回方框
                UiInputShell hostShell = textBox.Parent as UiInputShell;
                if (hostShell != null)
                {
                    hostShell.SyncColors();
                    hostShell.Invalidate();
                    WatchInteractiveState(textBox);
                    return;
                }

                textBox.BackColor = textBox.ReadOnly ? DisabledBackColor : TextBoxBackColor;
                textBox.ForeColor = textBox.Enabled ? TextColor : DisabledTextColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.Margin = new Padding(3, 4, 3, 4);
                if (!textBox.Multiline && textBox.Height < 24) textBox.Height = 24;
                WatchInteractiveState(textBox);
                return;
            }

            ComboBox comboBox = control as ComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = comboBox.Enabled ? TextBoxBackColor : DisabledBackColor;
                comboBox.ForeColor = comboBox.Enabled ? TextColor : DisabledTextColor;
                // 淺色主題交給系統畫，才有邊框與正常的下拉箭頭；
                // 深色主題只能用 Flat，否則系統會畫出淺色外框。
                comboBox.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                comboBox.Margin = new Padding(3, 4, 3, 4);
                WatchInteractiveState(comboBox);
                return;
            }

            Button button = control as Button;
            if (button != null)
            {
                ApplyButton(button);
                return;
            }

            TabControl tabControl = control as TabControl;
            if (tabControl != null)
            {
                ApplyTabControl(tabControl);
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

            UiSectionHeader sectionHeader = control as UiSectionHeader;
            if (sectionHeader != null)
            {
                sectionHeader.BackColor = SurfaceColor;
                sectionHeader.ForeColor = TextColor;
                return;
            }

            UiEmptyState emptyState = control as UiEmptyState;
            if (emptyState != null)
            {
                emptyState.BackColor = WindowBackColor;
                emptyState.ForeColor = MutedTextColor;
                return;
            }

            UiSegmentedControl segmented = control as UiSegmentedControl;
            if (segmented != null)
            {
                segmented.BackColor = SurfaceColor;
                segmented.ForeColor = TextColor;
                return;
            }

            UiInputShell inputShell = control as UiInputShell;
            if (inputShell != null)
            {
                inputShell.SyncColors();
                inputShell.Invalidate();
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
                ApplySplitContainer(splitContainer);
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

            ListView listView = control as ListView;
            if (listView != null)
            {
                listView.BackColor = ElevatedColor;
                listView.ForeColor = TextColor;
                listView.BorderStyle = BorderStyle.None;
                return;
            }

            GroupBox groupBox = control as GroupBox;
            if (groupBox != null)
            {
                groupBox.BackColor = Color.Transparent;
                groupBox.ForeColor = MutedTextColor;
                groupBox.Font = UiKit.BodyBold;
                return;
            }

            CheckBox checkBox = control as CheckBox;
            if (checkBox != null)
            {
                checkBox.BackColor = Color.Transparent;
                checkBox.ForeColor = checkBox.Enabled ? TextColor : DisabledTextColor;
                checkBox.FlatStyle = FlatStyle.Standard;
                WatchInteractiveState(checkBox);
                return;
            }

            RadioButton radioButton = control as RadioButton;
            if (radioButton != null)
            {
                radioButton.BackColor = Color.Transparent;
                radioButton.ForeColor = radioButton.Enabled ? TextColor : DisabledTextColor;
                radioButton.FlatStyle = FlatStyle.Standard;
                WatchInteractiveState(radioButton);
                return;
            }

            LinkLabel linkLabel = control as LinkLabel;
            if (linkLabel != null)
            {
                linkLabel.BackColor = Color.Transparent;
                linkLabel.ForeColor = TextColor;
                linkLabel.LinkColor = AccentColor;
                linkLabel.ActiveLinkColor = AccentHoverColor;
                linkLabel.VisitedLinkColor = MutedTextColor;
                linkLabel.LinkBehavior = LinkBehavior.HoverUnderline;
                return;
            }

            Label label = control as Label;
            if (label != null)
            {
                label.BackColor = Color.Transparent;
                // 「次要文字」只能在第一次套用時判定；套完 ForeColor 已被蓋掉，
                // 再從當下顏色反推會把 muted 樣式全部弄丟
                bool wasMuted;
                if (!MutedLabels.TryGetValue(label, out wasMuted))
                {
                    wasMuted = label.ForeColor == Color.Gray || label.ForeColor == Color.DarkGray || label.ForeColor == Color.Silver;
                    MutedLabels[label] = wasMuted;
                    label.Disposed += (s, e) => MutedLabels.Remove((Label)s);
                }
                label.ForeColor = wasMuted ? MutedTextColor : TextColor;
                return;
            }

            ProgressBar progressBar = control as ProgressBar;
            if (progressBar != null)
            {
                progressBar.BackColor = SurfaceColor;
                progressBar.ForeColor = AccentColor;
                return;
            }
        }

        private static readonly Dictionary<Label, bool> MutedLabels = new Dictionary<Label, bool>();
        private static readonly HashSet<Control> StateWatchedControls = new HashSet<Control>();

        /// <summary>Enabled/ReadOnly 之後才改變的話要重套顏色，否則停用色會固化在套版當下。</summary>
        private static void WatchInteractiveState(Control control)
        {
            if (!StateWatchedControls.Add(control)) return;
            EventHandler reapply = (s, e) => ApplyControl((Control)s);
            control.EnabledChanged += reapply;
            TextBoxBase watchedTextBox = control as TextBoxBase;
            if (watchedTextBox != null) watchedTextBox.ReadOnlyChanged += reapply;
            control.Disposed += (s, e) => StateWatchedControls.Remove((Control)s);
        }

        // ── 按鈕 ──────────────────────────────────────────────────────

        private class ButtonVisualState
        {
            public bool IsPrimary;
            public bool IsDanger;
            public bool IsToggled;
            public bool Hovered;
            public bool Pressed;
            public UiGlyph Glyph;
        }

        private static ButtonVisualState EnsureButtonState(Button button)
        {
            ButtonVisualState state;
            if (ButtonStates.TryGetValue(button, out state)) return state;

            state = new ButtonVisualState();
            ButtonStates[button] = state;

            button.MouseEnter += (s, e) => { state.Hovered = true; button.Invalidate(); };
            button.MouseLeave += (s, e) => { state.Hovered = false; state.Pressed = false; button.Invalidate(); };
            button.MouseDown += (s, e) => { state.Pressed = true; button.Invalidate(); };
            button.MouseUp += (s, e) => { state.Pressed = false; button.Invalidate(); };
            button.Paint += Button_Paint;
            button.Disposed += (s, e) => ButtonStates.Remove(button);
            return state;
        }

        private static void ApplyButton(Button button)
        {
            ButtonVisualState state = EnsureButtonState(button);

            // 讓系統只畫出與底色相同的方形，圓角外觀交給 Paint 事件補上
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            // 預設按鈕（AcceptButton）即使 BorderSize=0，WinForms 仍會在 Paint 事件之後
            // 補畫一圈方形邊框，圓角外的四個角落會露出 L 型痕跡；把邊框色設成背景色讓它隱形
            button.FlatAppearance.BorderColor = GetEffectiveParentBackColor(button);
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.BackColor = GetEffectiveParentBackColor(button);
            button.ForeColor = TextColor;
            button.Padding = new Padding(UiMetrics.Space2, 0, UiMetrics.Space2, 0);
            button.Cursor = Cursors.Hand;
            button.Font = UiKit.Body;

            if (button.Height < UiMetrics.ControlHeight && !button.AutoSize)
            {
                button.Height = UiMetrics.ControlHeight;
            }

            if (state.IsPrimary || state.IsDanger)
            {
                button.Font = UiKit.BodyBold;
            }
        }

        private static Color GetEffectiveParentBackColor(Control control)
        {
            Control parent = control.Parent;
            while (parent != null)
            {
                if (parent.BackColor != Color.Transparent) return parent.BackColor;
                parent = parent.Parent;
            }
            return WindowBackColor;
        }

        private static void Button_Paint(object sender, PaintEventArgs e)
        {
            Button button = sender as Button;
            if (button == null) return;

            ButtonVisualState state;
            if (!ButtonStates.TryGetValue(button, out state)) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 系統會在這個事件之後補畫預設按鈕的方形邊框；邊框色要跟著「當下」的
            // 父容器底色走（套主題時容器底色可能還沒定案），有差異才改以免重繪迴圈
            Color parentBack = GetEffectiveParentBackColor(button);
            if (button.FlatAppearance.BorderColor != parentBack)
            {
                button.FlatAppearance.BorderColor = parentBack;
            }

            // 先把系統畫好的方形內容蓋掉，再重畫圓角版本。
            // 圓角外的四個角落要畫「父容器真正的背景」：用猜色的方式（往上找第一個
            // 不透明的 BackColor）遇到容器底色與實際背景不同時會露出色差的角框，
            // 改請 WinForms 直接把父容器背景畫進來才會完全貼合。
            if (button.Parent != null)
            {
                ButtonRenderer.DrawParentBackground(g, button.ClientRectangle, button);
            }
            else
            {
                using (SolidBrush clear = new SolidBrush(GetEffectiveParentBackColor(button)))
                {
                    g.FillRectangle(clear, button.ClientRectangle);
                }
            }

            Rectangle bounds = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
            Color back;
            Color border;
            Color fore;

            if (!button.Enabled)
            {
                back = DisabledBackColor;
                border = BorderColor;
                fore = DisabledTextColor;
            }
            else if (state.IsPrimary)
            {
                back = state.Pressed ? AccentHoverColor : (state.Hovered ? AccentHoverColor : AccentColor);
                border = back;
                fore = UiKit.ReadableTextOn(back);
            }
            else if (state.IsDanger)
            {
                back = state.Pressed || state.Hovered ? UiKit.Mix(DangerColor, Color.Black, 0.12f) : DangerColor;
                border = back;
                fore = UiKit.ReadableTextOn(back);
            }
            else if (state.IsToggled)
            {
                back = state.Hovered || state.Pressed ? UiKit.Mix(AccentSoftColor, AccentColor, 0.12f) : AccentSoftColor;
                border = UiKit.Mix(AccentSoftColor, AccentColor, 0.45f);
                fore = AccentColor;
            }
            else
            {
                back = state.Pressed ? UiKit.Mix(ButtonBackColor, AccentColor, 0.16f)
                    : (state.Hovered ? HoverColor : ButtonBackColor);
                border = state.Hovered || state.Pressed ? BorderStrongColor : BorderColor;
                fore = TextColor;
            }

            UiKit.FillRounded(g, bounds, UiMetrics.RadiusMd, back);
            UiKit.DrawRounded(g, bounds, UiMetrics.RadiusMd, border);

            if (button.Focused && button.Enabled)
            {
                UiKit.DrawRounded(g, Rectangle.Inflate(bounds, -2, -2), UiMetrics.RadiusSm, UiKit.Mix(back, AccentColor, 0.55f));
            }

            if (state.Glyph != UiGlyph.None)
            {
                int glyphSize = Math.Min(18, Math.Min(bounds.Width, bounds.Height) - UiMetrics.Space2);
                UiKit.DrawGlyph(g, state.Glyph,
                    new RectangleF(
                        bounds.X + (bounds.Width - glyphSize) / 2f,
                        bounds.Y + (bounds.Height - glyphSize) / 2f,
                        glyphSize,
                        glyphSize),
                    fore);
                return;
            }

            Rectangle content = Rectangle.Inflate(bounds, -UiMetrics.Space2, 0);
            if (button.Image != null)
            {
                int imageSize = Math.Min(16, Math.Min(button.Image.Width, content.Height));
                Rectangle imageRect = new Rectangle(content.X, content.Y + (content.Height - imageSize) / 2, imageSize, imageSize);
                g.DrawImage(button.Image, imageRect);
                content = Rectangle.FromLTRB(imageRect.Right + UiMetrics.Space1, content.Y, content.Right, content.Bottom);
            }

            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            flags |= button.TextAlign == ContentAlignment.MiddleLeft || button.TextAlign == ContentAlignment.TopLeft || button.TextAlign == ContentAlignment.BottomLeft
                ? TextFormatFlags.Left
                : TextFormatFlags.HorizontalCenter;

            UiKit.DrawText(g, button.Text, button.Font, content, fore, flags);
        }

        // ── 分割視窗 ──────────────────────────────────────────────────

        private static void ApplySplitContainer(SplitContainer splitContainer)
        {
            splitContainer.BackColor = WindowBackColor;
            splitContainer.Panel1.BackColor = WindowBackColor;
            splitContainer.Panel2.BackColor = WindowBackColor;

            if (splitContainer.SplitterWidth < UiMetrics.SplitterWidth && !splitContainer.IsSplitterFixed)
            {
                try
                {
                    splitContainer.SplitterWidth = UiMetrics.SplitterWidth;
                }
                catch
                {
                    // 面板太小時 WinForms 會拒絕調整，維持原值即可
                }
            }

            if (StyledSplitters.Contains(splitContainer)) return;
            StyledSplitters.Add(splitContainer);
            splitContainer.Paint += SplitContainer_Paint;
            splitContainer.Disposed += (s, e) => StyledSplitters.Remove(splitContainer);
        }

        /// <summary>在較寬的拖曳區中間畫一條 1px 細線，維持好抓又不搶眼。</summary>
        private static void SplitContainer_Paint(object sender, PaintEventArgs e)
        {
            SplitContainer splitContainer = sender as SplitContainer;
            if (splitContainer == null) return;

            Rectangle rect = splitContainer.SplitterRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            if (splitContainer.Orientation == Orientation.Vertical)
            {
                UiKit.DrawVerticalHairline(e.Graphics, rect.X + rect.Width / 2, rect.Top, rect.Bottom, BorderColor);
            }
            else
            {
                UiKit.DrawHairline(e.Graphics, rect.Left, rect.Right, rect.Y + rect.Height / 2, BorderColor);
            }
        }

        // ── 分頁 ──────────────────────────────────────────────────────

        private static void ApplyTabControl(TabControl tabControl)
        {
            tabControl.BackColor = WindowBackColor;
            tabControl.ForeColor = TextColor;
            tabControl.Padding = new Point(Math.Max(tabControl.Padding.X, 14), Math.Max(tabControl.Padding.Y, 6));
            tabControl.SizeMode = TabSizeMode.Normal;

            if (StyledTabs.Contains(tabControl)) return;

            // 呼叫端已經自己接管繪製時就不要再插一手，否則兩份繪製會互相蓋掉
            if (tabControl.DrawMode == TabDrawMode.OwnerDrawFixed) return;

            StyledTabs.Add(tabControl);
            // FlatButtons 讓系統不再畫立體分頁外框，剩下的外觀由 DrawItem 全權決定
            tabControl.Appearance = TabAppearance.FlatButtons;
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem += TabControl_DrawItem;
            tabControl.Disposed += (s, e) => StyledTabs.Remove(tabControl);
        }

        private static void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = sender as TabControl;
            if (tabControl == null || e.Index < 0 || e.Index >= tabControl.TabPages.Count) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            TabPage page = tabControl.TabPages[e.Index];
            Rectangle bounds = e.Bounds;
            bool selected = tabControl.SelectedIndex == e.Index;
            bool hovered = (e.State & DrawItemState.HotLight) == DrawItemState.HotLight;

            Rectangle fill = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height + 2);
            using (SolidBrush brush = new SolidBrush(selected ? WindowBackColor : SurfaceColor))
            {
                g.FillRectangle(brush, fill);
            }

            if (!selected && hovered)
            {
                using (SolidBrush brush = new SolidBrush(HoverColor))
                {
                    g.FillRectangle(brush, fill);
                }
            }

            if (selected)
            {
                // 被選取的分頁用一條強調色底線標示，比整塊換色安靜
                using (SolidBrush brush = new SolidBrush(AccentColor))
                {
                    g.FillRectangle(brush, new Rectangle(bounds.X + 2, bounds.Bottom - 3, Math.Max(0, bounds.Width - 4), 2));
                }
            }
            else
            {
                UiKit.DrawVerticalHairline(g, bounds.Right - 1, bounds.Y + 6, bounds.Bottom - 6, BorderColor);
            }

            UiKit.DrawText(g, page.Text, selected ? UiKit.BodyBold : UiKit.Body,
                Rectangle.Inflate(bounds, -UiMetrics.Space1, 0),
                selected ? TextColor : MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ── 工具列 ────────────────────────────────────────────────────

        // 所有覆寫都讀動態顏色，共用單一實例即可，主題切換不會殘留舊色
        private static readonly AppToolStripRenderer SharedToolStripRenderer = new AppToolStripRenderer();

        public static void ApplyToolStrip(ToolStrip strip)
        {
            if (strip == null) return;

            ToolStripVariant variant = GetVariant(strip);

            strip.ForeColor = TextColor;
            strip.Renderer = SharedToolStripRenderer;
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.Font = UiKit.Body;

            switch (variant)
            {
                case ToolStripVariant.Ribbon:
                    strip.BackColor = SurfaceColor;
                    break;
                case ToolStripVariant.Menu:
                    strip.BackColor = SurfaceColor;
                    strip.Padding = new Padding(UiMetrics.Space2, 2, UiMetrics.Space2, 2);
                    break;
                case ToolStripVariant.Status:
                    strip.BackColor = SurfaceColor;
                    strip.Padding = new Padding(UiMetrics.Space3, 0, UiMetrics.Space3, 0);
                    StatusStrip statusStrip = strip as StatusStrip;
                    if (statusStrip != null) statusStrip.SizingGrip = false;
                    break;
                default:
                    strip.BackColor = SurfaceColor;
                    strip.Padding = new Padding(UiMetrics.Space2, 2, UiMetrics.Space2, 2);
                    break;
            }

            foreach (ToolStripItem item in strip.Items)
            {
                ApplyToolStripItem(item, variant);
            }
        }

        private static void ApplyToolStripItem(ToolStripItem item, ToolStripVariant variant)
        {
            // ToolStripTextBox / ToolStripComboBox 會把 BackColor 轉給內嵌的原生控制項，
            // 而那些控制項不接受 Color.Transparent，直接設會丟例外。
            bool hostsControl = item is ToolStripControlHost;
            item.BackColor = hostsControl
                ? (item.Owner != null ? item.Owner.BackColor : SurfaceColor)
                : Color.Transparent;
            item.ForeColor = TextColor;

            if (variant != ToolStripVariant.Ribbon)
            {
                item.Margin = new Padding(1, 1, 1, 1);
                item.Padding = new Padding(UiMetrics.Space1 + 1, 2, UiMetrics.Space1 + 1, 2);
            }

            ToolStripLabel label = item as ToolStripLabel;
            if (label != null && !label.IsLink)
            {
                label.ForeColor = MutedTextColor;
            }

            ToolStripTextBox textBox = item as ToolStripTextBox;
            if (textBox != null)
            {
                textBox.BackColor = TextBoxBackColor;
                textBox.ForeColor = TextColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }

            ToolStripComboBox comboBox = item as ToolStripComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = TextBoxBackColor;
                comboBox.ForeColor = TextColor;
                comboBox.FlatStyle = FlatStyle.Flat;
            }

            ToolStripDropDownItem dropDown = item as ToolStripDropDownItem;
            if (dropDown == null) return;

            dropDown.DropDown.BackColor = ElevatedColor;
            dropDown.DropDown.ForeColor = TextColor;
            dropDown.DropDown.Renderer = SharedToolStripRenderer;
            dropDown.DropDown.Padding = new Padding(0, UiMetrics.Space1, 0, UiMetrics.Space1);
            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                ApplyToolStripItem(child, ToolStripVariant.Compact);
            }
        }

        // ── 樹狀 ──────────────────────────────────────────────────────

        private static readonly Dictionary<TreeView, TreeNode> TreeHoverNodes = new Dictionary<TreeView, TreeNode>();
        private static readonly HashSet<TreeView> WiredTreeViews = new HashSet<TreeView>();

        private static void ApplyTreeView(TreeView treeView)
        {
            treeView.BackColor = ElevatedColor;
            treeView.ForeColor = TextColor;
            treeView.LineColor = BorderColor;
            treeView.BorderStyle = BorderStyle.None;
            treeView.FullRowSelect = true;
            treeView.ShowLines = false;
            treeView.HotTracking = false;
            treeView.ItemHeight = Math.Max(treeView.ItemHeight, UiMetrics.TreeItemHeight);
            treeView.Indent = Math.Max(treeView.Indent, 18);
            treeView.HideSelection = false;

            // 全自繪：把系統畫的虛線、展開方塊與焦點虛線框拿掉，
            // 換成整列圓角選取、自訂箭號與滑鼠停留回饋。事件只掛一次，
            // 語言/主題切換重複呼叫時不能累積 handler。
            treeView.DrawMode = TreeViewDrawMode.OwnerDrawAll;
            if (WiredTreeViews.Add(treeView))
            {
                treeView.DrawNode += TreeView_DrawNode;
                treeView.MouseMove += TreeView_MouseMove;
                treeView.MouseLeave += TreeView_MouseLeave;
                treeView.Disposed += (s, e) =>
                {
                    TreeHoverNodes.Remove(treeView);
                    WiredTreeViews.Remove(treeView);
                };
            }
        }

        private static void TreeView_MouseMove(object sender, MouseEventArgs e)
        {
            TreeView treeView = sender as TreeView;
            if (treeView == null) return;

            TreeNode node = treeView.GetNodeAt(e.Location);
            TreeNode previous;
            TreeHoverNodes.TryGetValue(treeView, out previous);
            if (ReferenceEquals(previous, node)) return;

            TreeHoverNodes[treeView] = node;
            InvalidateTreeRow(treeView, previous);
            InvalidateTreeRow(treeView, node);
        }

        private static void TreeView_MouseLeave(object sender, EventArgs e)
        {
            TreeView treeView = sender as TreeView;
            if (treeView == null) return;

            TreeNode previous;
            if (!TreeHoverNodes.TryGetValue(treeView, out previous) || previous == null) return;

            TreeHoverNodes[treeView] = null;
            InvalidateTreeRow(treeView, previous);
        }

        private static void InvalidateTreeRow(TreeView treeView, TreeNode node)
        {
            if (node == null) return;
            try
            {
                // 只重畫該列；OwnerDrawAll 下整棵樹 Invalidate 會明顯閃爍
                Rectangle bounds = node.Bounds;
                if (bounds.Height <= 0) return;
                treeView.Invalidate(new Rectangle(0, bounds.Y, Math.Max(1, treeView.ClientSize.Width), bounds.Height));
            }
            catch
            {
                // 節點可能已被移出樹（重新整理），略過即可
            }
        }

        private static void TreeView_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            TreeView treeView = sender as TreeView;
            if (treeView == null || e.Node == null)
            {
                e.DrawDefault = true;
                return;
            }

            Rectangle textBounds = e.Node.Bounds;
            if (e.Bounds.Height <= 0 || textBounds.Width < 0) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle row = new Rectangle(0, e.Bounds.Y, Math.Max(1, treeView.ClientSize.Width), e.Bounds.Height);
            using (SolidBrush brush = new SolidBrush(treeView.BackColor))
            {
                g.FillRectangle(brush, row);
            }

            bool selected = (e.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
            TreeNode hoveredNode;
            TreeHoverNodes.TryGetValue(treeView, out hoveredNode);
            if (hoveredNode != null && hoveredNode.TreeView == null)
            {
                // 停留的節點已被移出樹（重新整理），放掉參考讓它能被回收
                TreeHoverNodes[treeView] = null;
                hoveredNode = null;
            }
            bool isHovered = !selected && ReferenceEquals(hoveredNode, e.Node);

            Rectangle pill = new Rectangle(
                UiMetrics.Space1,
                row.Y + 1,
                Math.Max(1, row.Width - UiMetrics.Space1 * 2),
                Math.Max(1, row.Height - 2));

            if (selected)
            {
                UiKit.FillRounded(g, pill, UiMetrics.RadiusMd, SelectionColor);
            }
            else if (isHovered)
            {
                UiKit.FillRounded(g, pill, UiMetrics.RadiusMd, HoverColor);
            }

            int imageWidth = treeView.ImageList != null ? treeView.ImageList.ImageSize.Width : 0;
            int imageX = textBounds.X - imageWidth - 1;

            // 展開箭號畫在系統原本放展開方塊的位置，點擊行為不受影響
            if (e.Node.Nodes.Count > 0 && (e.Node.Level > 0 || treeView.ShowRootLines))
            {
                int glyphSize = 11;
                Rectangle glyphBounds = new Rectangle(
                    imageX - treeView.Indent + (treeView.Indent - glyphSize) / 2,
                    row.Y + (row.Height - glyphSize) / 2,
                    glyphSize,
                    glyphSize);
                UiKit.DrawGlyph(g,
                    e.Node.IsExpanded ? UiGlyph.ChevronDown : UiGlyph.ChevronRight,
                    glyphBounds,
                    selected ? SelectionTextColor : SubtleTextColor,
                    0.85f);
            }

            if (treeView.ImageList != null)
            {
                int index = selected && e.Node.SelectedImageIndex >= 0 ? e.Node.SelectedImageIndex : e.Node.ImageIndex;
                if (index >= 0 && index < treeView.ImageList.Images.Count)
                {
                    int imageHeight = treeView.ImageList.ImageSize.Height;
                    treeView.ImageList.Draw(g, imageX, row.Y + (row.Height - imageHeight) / 2, index);
                }
            }

            Color textColor = selected
                ? SelectionTextColor
                : (e.Node.ForeColor.IsEmpty ? treeView.ForeColor : e.Node.ForeColor);

            Font font = e.Node.NodeFont ?? treeView.Font;
            Rectangle labelBounds = Rectangle.FromLTRB(
                textBounds.X,
                row.Y,
                Math.Max(textBounds.X + 1, pill.Right - UiMetrics.Space2),
                row.Bottom);

            UiKit.DrawText(g, e.Node.Text, font, labelBounds, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        }

        // ── 資料表格 ──────────────────────────────────────────────────

        private static void ApplyDataGridView(DataGridView dgv)
        {
            dgv.BackgroundColor = WindowBackColor;
            dgv.GridColor = GridColor;
            dgv.BorderStyle = BorderStyle.None;
            dgv.EnableHeadersVisualStyles = false;
            dgv.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            dgv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgv.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgv.RowTemplate.Height = Math.Max(dgv.RowTemplate.Height, UiMetrics.RowHeight);
            // SizeMode 還是 AutoSize 時設定 Height 不會生效，要先關掉
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgv.ColumnHeadersHeight = Math.Max(dgv.ColumnHeadersHeight, 32);
            dgv.Font = UiKit.Body;

            Color alternating = IsDark ? Color.FromArgb(35, 39, 45) : Color.FromArgb(250, 251, 252);

            ApplyCellStyle(dgv.DefaultCellStyle, ElevatedColor);
            ApplyCellStyle(dgv.RowsDefaultCellStyle, ElevatedColor);
            ApplyCellStyle(dgv.AlternatingRowsDefaultCellStyle, alternating);

            dgv.ColumnHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = MutedTextColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceColor;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = MutedTextColor;
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(UiMetrics.Space2, UiMetrics.Space1, UiMetrics.Space2, UiMetrics.Space1);
            dgv.ColumnHeadersDefaultCellStyle.Font = UiKit.BodyBold;

            dgv.RowHeadersDefaultCellStyle.BackColor = SurfaceColor;
            dgv.RowHeadersDefaultCellStyle.ForeColor = SubtleTextColor;
            dgv.RowHeadersDefaultCellStyle.SelectionBackColor = AccentSoftColor;
            dgv.RowHeadersDefaultCellStyle.SelectionForeColor = AccentColor;
            dgv.RowHeadersDefaultCellStyle.Padding = new Padding(UiMetrics.Space1, 2, UiMetrics.Space1, 2);

            dgv.TopLeftHeaderCell.Style.BackColor = SurfaceColor;
            dgv.TopLeftHeaderCell.Style.ForeColor = MutedTextColor;
            dgv.TopLeftHeaderCell.Style.SelectionBackColor = SurfaceColor;
            dgv.TopLeftHeaderCell.Style.SelectionForeColor = MutedTextColor;
        }

        private static void ApplyCellStyle(DataGridViewCellStyle style, Color backColor)
        {
            style.BackColor = backColor;
            style.ForeColor = TextColor;
            style.SelectionBackColor = SelectionColor;
            style.SelectionForeColor = SelectionTextColor;
            style.Padding = new Padding(UiMetrics.Space2, 2, UiMetrics.Space2, 2);
        }

        // ── 自訂 Renderer ─────────────────────────────────────────────

        private class AppColorTable : ProfessionalColorTable
        {
            public AppColorTable()
            {
                UseSystemColors = false;
            }

            public override Color ToolStripGradientBegin => SurfaceColor;
            public override Color ToolStripGradientMiddle => SurfaceColor;
            public override Color ToolStripGradientEnd => SurfaceColor;
            public override Color ToolStripContentPanelGradientBegin => SurfaceColor;
            public override Color ToolStripContentPanelGradientEnd => SurfaceColor;
            public override Color ToolStripPanelGradientBegin => SurfaceColor;
            public override Color ToolStripPanelGradientEnd => SurfaceColor;
            public override Color ToolStripBorder => BorderColor;
            public override Color MenuBorder => BorderColor;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => HoverColor;
            public override Color MenuItemSelectedGradientBegin => HoverColor;
            public override Color MenuItemSelectedGradientEnd => HoverColor;
            public override Color MenuItemPressedGradientBegin => SurfaceColor;
            public override Color MenuItemPressedGradientMiddle => SurfaceColor;
            public override Color MenuItemPressedGradientEnd => SurfaceColor;
            public override Color MenuStripGradientBegin => SurfaceColor;
            public override Color MenuStripGradientEnd => SurfaceColor;
            public override Color ImageMarginGradientBegin => ElevatedColor;
            public override Color ImageMarginGradientMiddle => ElevatedColor;
            public override Color ImageMarginGradientEnd => ElevatedColor;
            public override Color SeparatorDark => BorderColor;
            public override Color SeparatorLight => BorderColor;
            public override Color ButtonSelectedGradientBegin => HoverColor;
            public override Color ButtonSelectedGradientEnd => HoverColor;
            public override Color ButtonSelectedHighlight => HoverColor;
            public override Color ButtonPressedGradientBegin => AccentSoftColor;
            public override Color ButtonPressedGradientEnd => AccentSoftColor;
            public override Color ButtonCheckedGradientBegin => AccentSoftColor;
            public override Color ButtonCheckedGradientEnd => AccentSoftColor;
            public override Color ButtonCheckedHighlight => AccentSoftColor;
            public override Color CheckBackground => AccentSoftColor;
            public override Color CheckSelectedBackground => AccentSoftColor;
            public override Color CheckPressedBackground => AccentSoftColor;
            public override Color StatusStripGradientBegin => SurfaceColor;
            public override Color StatusStripGradientEnd => SurfaceColor;
            public override Color OverflowButtonGradientBegin => SurfaceColor;
            public override Color OverflowButtonGradientEnd => SurfaceColor;
        }

        private class AppToolStripRenderer : ToolStripProfessionalRenderer
        {
            public AppToolStripRenderer() : base(new AppColorTable())
            {
                RoundedEdges = false;
            }

            private static bool IsRibbon(ToolStrip strip)
            {
                return strip != null && GetVariant(strip) == ToolStripVariant.Ribbon;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                Color back = e.ToolStrip is ToolStripDropDown ? ElevatedColor : e.ToolStrip.BackColor;
                using (SolidBrush brush = new SolidBrush(back))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                ToolStrip strip = e.ToolStrip;

                if (strip is ToolStripDropDown)
                {
                    UiKit.DrawRounded(e.Graphics, new Rectangle(0, 0, strip.Width - 1, strip.Height - 1), UiMetrics.RadiusSm, BorderStrongColor);
                    return;
                }

                if (strip is StatusStrip)
                {
                    UiKit.DrawHairline(e.Graphics, 0, strip.Width, 0, BorderColor);
                    return;
                }

                UiKit.DrawHairline(e.Graphics, 0, strip.Width, strip.Height - 1, BorderColor);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                RenderItemSurface(e.Graphics, e.Item, e.ToolStrip);
            }

            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
            {
                RenderItemSurface(e.Graphics, e.Item, e.ToolStrip);
            }

            protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
            {
                RenderItemSurface(e.Graphics, e.Item, e.ToolStrip);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                bool isDropDownItem = e.ToolStrip is ToolStripDropDown;
                Rectangle bounds = isDropDownItem
                    ? new Rectangle(UiMetrics.Space1, e.Item.ContentRectangle.Y, Math.Max(1, e.Item.Width - UiMetrics.Space1 * 2), e.Item.Height)
                    : new Rectangle(0, 2, e.Item.Width, Math.Max(1, e.Item.Height - 4));

                if (!e.Item.Enabled) return;

                ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
                bool open = menuItem != null && menuItem.DropDown != null && menuItem.DropDown.Visible;

                if (e.Item.Selected || open)
                {
                    UiKit.FillRounded(g, bounds, UiMetrics.RadiusSm, isDropDownItem ? AccentSoftColor : HoverColor);
                }
            }

            private static void RenderItemSurface(Graphics g, ToolStripItem item, ToolStrip strip)
            {
                if (!item.Enabled) return;

                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool ribbon = IsRibbon(strip);
                ToolStripButton button = item as ToolStripButton;
                bool isChecked = button != null && button.Checked;

                Rectangle bounds = ribbon
                    ? new Rectangle(2, 2, Math.Max(1, item.Width - 4), Math.Max(1, item.Height - 4))
                    : new Rectangle(1, 1, Math.Max(1, item.Width - 2), Math.Max(1, item.Height - 2));

                int radius = ribbon ? UiMetrics.RadiusLg : UiMetrics.RadiusSm;

                if (isChecked)
                {
                    UiKit.FillRounded(g, bounds, radius, AccentSoftColor);
                    if (ribbon)
                    {
                        // 底部一小段強調色，取代原本刺眼的藍色外框
                        int barWidth = Math.Max(12, bounds.Width / 3);
                        Rectangle bar = new Rectangle(bounds.X + (bounds.Width - barWidth) / 2, bounds.Bottom - 3, barWidth, 3);
                        UiKit.FillRounded(g, bar, 1.5f, AccentColor);
                    }
                    else
                    {
                        UiKit.DrawRounded(g, bounds, radius, UiKit.Mix(AccentSoftColor, AccentColor, 0.35f));
                    }

                    if (item.Pressed || item.Selected)
                    {
                        UiKit.FillRounded(g, bounds, radius, Color.FromArgb(28, AccentColor));
                    }
                    return;
                }

                if (item.Pressed)
                {
                    UiKit.FillRounded(g, bounds, radius, AccentSoftColor);
                }
                else if (item.Selected)
                {
                    UiKit.FillRounded(g, bounds, radius, HoverColor);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                bool ribbon = IsRibbon(e.ToolStrip);
                ToolStripButton button = e.Item as ToolStripButton;
                bool isChecked = button != null && button.Checked;

                if (!e.Item.Enabled)
                {
                    e.TextColor = DisabledTextColor;
                }
                else if (isChecked)
                {
                    e.TextColor = AccentColor;
                }
                else if (ribbon)
                {
                    e.TextColor = e.Item.Selected ? TextColor : MutedTextColor;
                }
                else
                {
                    e.TextColor = TextColor;
                }

                e.TextFormat |= TextFormatFlags.NoPrefix;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                GlyphSpec spec;
                if (TryGetGlyph(e.Item, out spec))
                {
                    RenderGlyphImage(e, spec);
                    return;
                }

                if (!e.Item.Enabled)
                {
                    // 停用狀態畫成半透明，比系統預設的灰階遮罩乾淨
                    if (e.Image == null) return;
                    using (System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes())
                    {
                        System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix();
                        matrix.Matrix33 = 0.35f;
                        attributes.SetColorMatrix(matrix);
                        e.Graphics.DrawImage(e.Image, e.ImageRectangle, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
                    }
                    return;
                }

                base.OnRenderItemImage(e);
            }

            /// <summary>用向量圖示取代點陣圖，顏色跟著啟用/選取狀態走。</summary>
            private static void RenderGlyphImage(ToolStripItemImageRenderEventArgs e, GlyphSpec spec)
            {
                ToolStripButton button = e.Item as ToolStripButton;
                bool isChecked = button != null && button.Checked;

                Color color;
                if (!e.Item.Enabled) color = DisabledTextColor;
                else if (spec.HasTint) color = spec.Tint;
                else if (isChecked) color = AccentColor;
                else if (e.Item.Selected || e.Item.Pressed) color = TextColor;
                else color = IsRibbon(e.ToolStrip) ? UiKit.Mix(MutedTextColor, TextColor, 0.35f) : MutedTextColor;

                // 主功能列的圖示筆畫略細，避免大尺寸下顯得笨重
                float strokeScale = IsRibbon(e.ToolStrip) ? 0.85f : 1f;
                UiKit.DrawGlyph(e.Graphics, spec.Glyph, e.ImageRectangle, color, strokeScale);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                Rectangle bounds = e.Item.Bounds;

                if (e.Vertical)
                {
                    int x = bounds.Width / 2;
                    UiKit.DrawVerticalHairline(g, x, UiMetrics.Space2, Math.Max(UiMetrics.Space2 + 1, bounds.Height - UiMetrics.Space2), BorderColor);
                }
                else
                {
                    int y = bounds.Height / 2;
                    UiKit.DrawHairline(g, UiMetrics.Space2, Math.Max(UiMetrics.Space2 + 1, bounds.Width - UiMetrics.Space2), y, BorderColor);
                }
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(ElevatedColor))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                Color color = e.Item.Enabled ? MutedTextColor : DisabledTextColor;
                Rectangle rect = e.ArrowRectangle;
                UiGlyph glyph = e.Direction == ArrowDirection.Down ? UiGlyph.ChevronDown : UiGlyph.ChevronRight;

                int size = Math.Min(12, Math.Min(rect.Width, rect.Height) + 4);
                RectangleF bounds = new RectangleF(
                    rect.X + (rect.Width - size) / 2f,
                    rect.Y + (rect.Height - size) / 2f,
                    size,
                    size);
                UiKit.DrawGlyph(e.Graphics, glyph, bounds, color, 0.9f);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle rect = e.ImageRectangle;
                UiKit.FillRounded(e.Graphics, rect, UiMetrics.RadiusSm, AccentSoftColor);
                UiKit.DrawGlyph(e.Graphics, UiGlyph.Check, Rectangle.Inflate(rect, -3, -3), AccentColor, 1.1f);
            }

            protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
            {
                // 刻意留白：現代視窗不需要右下角的抓握點
            }

            protected override void OnRenderToolStripStatusLabelBackground(ToolStripItemRenderEventArgs e)
            {
                // 狀態列文字不需要底色
            }
        }
    }
}

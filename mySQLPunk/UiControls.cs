using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace mySQLPunk
{
    /// <summary>
    /// 區塊標題列：左側標題文字，底部一條細分隔線，右側可放操作控制項。
    /// 用來取代直接把 Label 丟在面板上的做法，讓每個面板都有一致的抬頭。
    /// </summary>
    public class UiSectionHeader : Panel
    {
        private string _title = string.Empty;
        private string _subtitle = string.Empty;
        private UiGlyph _glyph = UiGlyph.None;
        private bool _showBottomBorder = true;

        public UiSectionHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = UiMetrics.SectionHeaderHeight;
            Padding = new Padding(UiMetrics.Space3, 0, UiMetrics.Space2, 0);
        }

        public string Title
        {
            get { return _title; }
            set { _title = value ?? string.Empty; Invalidate(); }
        }

        /// <summary>副標題，會以較淡的顏色接在標題右邊，適合放筆數、連線名稱等次要資訊。</summary>
        public string Subtitle
        {
            get { return _subtitle; }
            set { _subtitle = value ?? string.Empty; Invalidate(); }
        }

        public UiGlyph Glyph
        {
            get { return _glyph; }
            set { _glyph = value; Invalidate(); }
        }

        public bool ShowBottomBorder
        {
            get { return _showBottomBorder; }
            set { _showBottomBorder = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            int x = Padding.Left;
            int contentHeight = Height - (_showBottomBorder ? 1 : 0);

            if (_glyph != UiGlyph.None)
            {
                int glyphSize = 16;
                RectangleF glyphBounds = new RectangleF(x, (contentHeight - glyphSize) / 2f, glyphSize, glyphSize);
                UiKit.DrawGlyph(g, _glyph, glyphBounds, ThemeManager.MutedTextColor);
                x += glyphSize + UiMetrics.Space2;
            }

            Font titleFont = UiKit.GetFont(UiMetrics.FontSizeBody, FontStyle.Bold);
            Size titleSize = TextRenderer.MeasureText(g, _title, titleFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
            Rectangle titleBounds = new Rectangle(x, 0, Math.Max(0, Width - x - Padding.Right), contentHeight);
            UiKit.DrawText(g, _title, titleFont, titleBounds, ThemeManager.TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(_subtitle))
            {
                int subtitleX = x + titleSize.Width + UiMetrics.Space2;
                Rectangle subtitleBounds = new Rectangle(subtitleX, 0, Math.Max(0, Width - subtitleX - Padding.Right), contentHeight);
                UiKit.DrawText(g, _subtitle, UiKit.Caption, subtitleBounds, ThemeManager.SubtleTextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (_showBottomBorder)
            {
                UiKit.DrawHairline(g, 0, Width, Height - 1, ThemeManager.BorderColor);
            }
        }
    }

    /// <summary>分段切換控制項，用來取代「兩顆長得像工具列按鈕的分頁」。</summary>
    public class UiSegmentedControl : Control
    {
        public class Segment
        {
            public string Text { get; set; }
            public UiGlyph Glyph { get; set; }
            public object Tag { get; set; }

            public Segment(string text, UiGlyph glyph)
            {
                Text = text ?? string.Empty;
                Glyph = glyph;
            }
        }

        private readonly List<Segment> _segments = new List<Segment>();
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public UiSegmentedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Height = UiMetrics.ControlHeight;
            Cursor = Cursors.Hand;
            Font = UiKit.Body;
        }

        public IList<Segment> Segments { get { return _segments; } }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int clamped = value < 0 || value >= _segments.Count ? -1 : value;
                if (clamped == _selectedIndex) return;
                _selectedIndex = clamped;
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public void AddSegment(string text, UiGlyph glyph)
        {
            _segments.Add(new Segment(text, glyph));
            if (_selectedIndex < 0) _selectedIndex = 0;
            Invalidate();
        }

        public void SetSegmentText(int index, string text)
        {
            if (index < 0 || index >= _segments.Count) return;
            _segments[index].Text = text ?? string.Empty;
            Invalidate();
        }

        /// <summary>依內容算出剛好容納所有分段的寬度。</summary>
        public int GetPreferredWidth()
        {
            int total = UiMetrics.Space1 * 2;
            using (Graphics g = CreateGraphics())
            {
                foreach (Segment segment in _segments)
                {
                    total += MeasureSegmentWidth(g, segment);
                }
            }
            return total;
        }

        private int MeasureSegmentWidth(Graphics g, Segment segment)
        {
            Size textSize = TextRenderer.MeasureText(g, segment.Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
            int width = textSize.Width + UiMetrics.Space4;
            if (segment.Glyph != UiGlyph.None) width += 15 + UiMetrics.Space1;
            return Math.Max(56, width);
        }

        private Rectangle[] GetSegmentBounds()
        {
            Rectangle[] bounds = new Rectangle[_segments.Count];
            if (_segments.Count == 0) return bounds;

            int[] widths = new int[_segments.Count];
            int total = 0;
            using (Graphics g = CreateGraphics())
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    widths[i] = MeasureSegmentWidth(g, _segments[i]);
                    total += widths[i];
                }
            }

            int available = Width - UiMetrics.Space1 * 2;
            // 內容比可用寬度窄時就靠左排；太寬則等比壓縮，避免溢出
            float scale = total > available && total > 0 ? (float)available / total : 1f;

            int x = UiMetrics.Space1;
            for (int i = 0; i < _segments.Count; i++)
            {
                int w = Math.Max(1, (int)Math.Round(widths[i] * scale));
                bounds[i] = new Rectangle(x, UiMetrics.Space1, w, Math.Max(1, Height - UiMetrics.Space1 * 2));
                x += w;
            }
            return bounds;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            UiKit.FillRounded(g, track, UiMetrics.RadiusMd + 2, ThemeManager.SurfaceColor);
            UiKit.DrawRounded(g, track, UiMetrics.RadiusMd + 2, ThemeManager.BorderColor);

            Rectangle[] bounds = GetSegmentBounds();
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment segment = _segments[i];
                Rectangle rect = bounds[i];
                bool selected = i == _selectedIndex;
                bool hovered = i == _hoverIndex && !selected;

                Color textColor = ThemeManager.MutedTextColor;
                if (selected)
                {
                    UiKit.FillRounded(g, rect, UiMetrics.RadiusMd, ThemeManager.ElevatedColor);
                    UiKit.DrawRounded(g, rect, UiMetrics.RadiusMd, ThemeManager.BorderStrongColor);
                    textColor = ThemeManager.TextColor;
                }
                else if (hovered)
                {
                    UiKit.FillRounded(g, rect, UiMetrics.RadiusMd, ThemeManager.HoverColor);
                    textColor = ThemeManager.TextColor;
                }

                int contentX = rect.X + UiMetrics.Space2;
                if (segment.Glyph != UiGlyph.None)
                {
                    RectangleF glyphBounds = new RectangleF(contentX, rect.Y + (rect.Height - 15) / 2f, 15, 15);
                    UiKit.DrawGlyph(g, segment.Glyph, glyphBounds, selected ? ThemeManager.AccentColor : textColor);
                    contentX += 15 + UiMetrics.Space1;
                }

                Rectangle textBounds = Rectangle.FromLTRB(contentX, rect.Y, rect.Right - UiMetrics.Space1, rect.Bottom);
                UiKit.DrawText(g, segment.Text, selected ? UiKit.BodyBold : Font, textBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index == _hoverIndex) return;
            _hoverIndex = index;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index >= 0) SelectedIndex = index;
        }

        private int HitTest(Point point)
        {
            Rectangle[] bounds = GetSegmentBounds();
            for (int i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].Contains(point)) return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// 空狀態面板：在還沒有資料時給一個明確的圖示、標題與說明，
    /// 取代原本一大片什麼都沒有的白色區域。
    /// </summary>
    public class UiEmptyState : Control
    {
        private UiGlyph _glyph = UiGlyph.Database;
        private string _title = string.Empty;
        private string _description = string.Empty;
        private string _hint = string.Empty;

        public UiEmptyState()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = UiKit.Body;
        }

        public UiGlyph Glyph
        {
            get { return _glyph; }
            set { _glyph = value; Invalidate(); }
        }

        public string Title
        {
            get { return _title; }
            set { _title = value ?? string.Empty; Invalidate(); }
        }

        public string Description
        {
            get { return _description; }
            set { _description = value ?? string.Empty; Invalidate(); }
        }

        /// <summary>底部的操作提示，例如快捷鍵或下一步該做什麼。</summary>
        public string Hint
        {
            get { return _hint; }
            set { _hint = value ?? string.Empty; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Width < 80 || Height < 80) return;

            // 依面板大小決定要顯示到哪個層級，窄面板只留圖示與標題
            bool compact = Width < 260 || Height < 200;
            int circleSize = compact ? 48 : 64;
            int glyphSize = (int)(circleSize * 0.5f);

            Font titleFont = UiKit.GetFont(compact ? UiMetrics.FontSizeBody : UiMetrics.FontSizeSubtitle, FontStyle.Bold);
            Font bodyFont = UiKit.Caption;

            int maxTextWidth = Math.Max(120, Math.Min(Width - UiMetrics.Space5 * 2, 380));
            Size titleSize = TextRenderer.MeasureText(g, _title, titleFont, new Size(maxTextWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size descSize = compact || string.IsNullOrEmpty(_description)
                ? Size.Empty
                : TextRenderer.MeasureText(g, _description, bodyFont, new Size(maxTextWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size hintSize = compact || string.IsNullOrEmpty(_hint)
                ? Size.Empty
                : TextRenderer.MeasureText(g, _hint, bodyFont, new Size(maxTextWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            int totalHeight = circleSize + UiMetrics.Space4 + titleSize.Height;
            if (descSize.Height > 0) totalHeight += UiMetrics.Space2 + descSize.Height;
            if (hintSize.Height > 0) totalHeight += UiMetrics.Space3 + hintSize.Height;

            int y = Math.Max(UiMetrics.Space4, (Height - totalHeight) / 2);
            int centerX = Width / 2;

            Rectangle circle = new Rectangle(centerX - circleSize / 2, y, circleSize, circleSize);
            using (SolidBrush brush = new SolidBrush(ThemeManager.SurfaceColor))
            {
                g.FillEllipse(brush, circle);
            }
            using (Pen pen = new Pen(ThemeManager.BorderColor, 1f))
            {
                g.DrawEllipse(pen, circle);
            }
            UiKit.DrawGlyph(g,
                _glyph,
                new RectangleF(centerX - glyphSize / 2f, y + (circleSize - glyphSize) / 2f, glyphSize, glyphSize),
                ThemeManager.SubtleTextColor);

            y += circleSize + UiMetrics.Space4;

            UiKit.DrawText(g, _title, titleFont,
                new Rectangle(centerX - maxTextWidth / 2, y, maxTextWidth, titleSize.Height),
                ThemeManager.TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            y += titleSize.Height;

            if (descSize.Height > 0)
            {
                y += UiMetrics.Space2;
                UiKit.DrawText(g, _description, bodyFont,
                    new Rectangle(centerX - maxTextWidth / 2, y, maxTextWidth, descSize.Height),
                    ThemeManager.MutedTextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                y += descSize.Height;
            }

            if (hintSize.Height > 0)
            {
                y += UiMetrics.Space3;
                UiKit.DrawText(g, _hint, bodyFont,
                    new Rectangle(centerX - maxTextWidth / 2, y, maxTextWidth, hintSize.Height),
                    ThemeManager.SubtleTextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }
        }
    }

    /// <summary>一條 1px 的分隔線，可水平或垂直。</summary>
    public class UiDivider : Control
    {
        public UiDivider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 1;
            Width = 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Height <= Width)
            {
                UiKit.DrawHairline(e.Graphics, 0, Width, 0, ThemeManager.BorderColor);
            }
            else
            {
                UiKit.DrawVerticalHairline(e.Graphics, 0, 0, Height, ThemeManager.BorderColor);
            }
        }
    }
}

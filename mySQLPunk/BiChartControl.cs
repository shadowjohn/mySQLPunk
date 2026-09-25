using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>以 GDI+ 畫長條、折線、圓餅與數字卡；點選分類會觸發 CategoryClicked，選取的分類以強調色標示。</summary>
    public sealed class BiChartControl : Control
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(37, 99, 235), Color.FromArgb(22, 163, 74), Color.FromArgb(234, 88, 12), Color.FromArgb(147, 51, 234),
            Color.FromArgb(219, 39, 119), Color.FromArgb(13, 148, 136), Color.FromArgb(202, 138, 4), Color.FromArgb(79, 70, 229),
            Color.FromArgb(220, 38, 38), Color.FromArgb(8, 145, 178), Color.FromArgb(101, 163, 13), Color.FromArgb(100, 116, 139)
        };

        private readonly List<KeyValuePair<GraphicsPath, string>> hitRegions = new List<KeyValuePair<GraphicsPath, string>>();
        private readonly ToolTip toolTip = new ToolTip();
        private BiChartKind kind;
        private BiWidgetResult result;
        private string selectedKey;
        private string hoverKey;
        private string caption;

        public BiChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Font = UiKit.Caption;
        }

        public event Action<string> CategoryClicked;

        public void SetData(BiChartKind chartKind, BiWidgetResult data, string selected, string valueCaption)
        {
            kind = chartKind;
            result = data;
            selectedKey = selected;
            caption = valueCaption;
            hoverKey = null;
            Invalidate();
        }

        /// <summary>依座標找分類鍵；供測試與滑鼠事件使用。</summary>
        public string HitTest(Point point)
        {
            foreach (KeyValuePair<GraphicsPath, string> region in hitRegions)
            {
                if (region.Key.IsVisible(point)) return region.Value;
            }
            return null;
        }

        public IList<string> HitKeys { get { return hitRegions.Select(region => region.Value).ToList(); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string key = HitTest(e.Location);
            if (key == hoverKey) return;
            hoverKey = key;
            Cursor = key == null ? Cursors.Default : Cursors.Hand;
            BiPoint point = key == null || result == null ? null : result.Points.FirstOrDefault(item => item.Key == key);
            toolTip.SetToolTip(this, point == null ? string.Empty : point.Label + ": " + BiDashboardService.FormatNumber(point.Value));
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverKey == null) return;
            hoverKey = null;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            string key = HitTest(e.Location);
            if (key != null && CategoryClicked != null) CategoryClicked(key);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearRegions();
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ClearRegions()
        {
            foreach (KeyValuePair<GraphicsPath, string> region in hitRegions) region.Key.Dispose();
            hitRegions.Clear();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ThemeManager.ElevatedColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            ClearRegions();
            Rectangle bounds = new Rectangle(8, 6, Math.Max(0, Width - 16), Math.Max(0, Height - 12));
            if (result == null) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                UiKit.DrawText(g, result.Error, Font, bounds, ThemeManager.DangerColor, TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (kind == BiChartKind.Number)
            {
                PaintNumber(g, bounds);
                return;
            }
            if (result.Points.Count == 0)
            {
                UiKit.DrawText(g, Localization.T("Bi.NoData"), Font, bounds, ThemeManager.MutedTextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            switch (kind)
            {
                case BiChartKind.Pie: PaintPie(g, bounds); break;
                case BiChartKind.Line: PaintLine(g, bounds); break;
                default: PaintBars(g, bounds); break;
            }
        }

        private Color ColorFor(int index, string key)
        {
            Color color = kind == BiChartKind.Pie ? Palette[index % Palette.Length] : ThemeManager.AccentColor;
            if (selectedKey != null && key != selectedKey) color = UiKit.Mix(color, ThemeManager.ElevatedColor, 0.65f);
            if (key == hoverKey) color = UiKit.Mix(color, ThemeManager.TextColor, 0.2f);
            return color;
        }

        private void PaintNumber(Graphics g, Rectangle bounds)
        {
            using (Font big = UiKit.GetFont(Math.Max(14f, Math.Min(36f, bounds.Height / 3.2f)), FontStyle.Bold))
            {
                Rectangle top = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height * 2 / 3);
                UiKit.DrawText(g, BiDashboardService.FormatNumber(result.Total), big, top, ThemeManager.TextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
            }
            Rectangle bottom = new Rectangle(bounds.X, bounds.Y + bounds.Height * 2 / 3 + 4, bounds.Width, bounds.Height / 3 - 4);
            UiKit.DrawText(g, caption ?? string.Empty, Font, bottom, ThemeManager.MutedTextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }

        private void PaintBars(Graphics g, Rectangle bounds)
        {
            // 水平長條：標籤在左、數值在右，較長的分類名稱也讀得到。
            int labelWidth = Math.Min(bounds.Width / 3, 160);
            int valueWidth = 64;
            int count = result.Points.Count;
            float rowHeight = Math.Max(6f, Math.Min(28f, bounds.Height / (float)count));
            decimal max = result.Points.Max(point => Math.Abs(point.Value ?? 0m));
            if (max == 0m) max = 1m;
            float barLeft = bounds.X + labelWidth + 6;
            float barWidth = Math.Max(10, bounds.Width - labelWidth - valueWidth - 12);
            for (int i = 0; i < count; i++)
            {
                BiPoint point = result.Points[i];
                float y = bounds.Y + i * rowHeight;
                if (y + rowHeight > bounds.Bottom + 1) break;
                Rectangle labelBox = new Rectangle(bounds.X, (int)y, labelWidth, (int)rowHeight);
                if (rowHeight >= 12) UiKit.DrawText(g, point.Label, Font, labelBox, ThemeManager.TextColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                float length = (float)(Math.Abs(point.Value ?? 0m) / max) * barWidth;
                RectangleF bar = new RectangleF(barLeft, y + rowHeight * 0.18f, Math.Max(1f, length), rowHeight * 0.64f);
                using (SolidBrush brush = new SolidBrush(ColorFor(i, point.Key))) g.FillRectangle(brush, bar);
                if (rowHeight >= 12)
                {
                    Rectangle valueBox = new Rectangle((int)(barLeft + length + 4), (int)y, valueWidth + (int)(barWidth - length), (int)rowHeight);
                    UiKit.DrawText(g, BiDashboardService.FormatNumber(point.Value), Font, valueBox, ThemeManager.MutedTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
                GraphicsPath hit = new GraphicsPath();
                hit.AddRectangle(new RectangleF(bounds.X, y, bounds.Width, rowHeight));
                hitRegions.Add(new KeyValuePair<GraphicsPath, string>(hit, point.Key));
            }
        }

        private void PaintLine(Graphics g, Rectangle bounds)
        {
            int count = result.Points.Count;
            decimal max = result.Points.Max(point => point.Value ?? 0m);
            decimal min = Math.Min(0m, result.Points.Min(point => point.Value ?? 0m));
            if (max == min) max = min + 1m;
            int axisHeight = 18;
            int valueAxis = 52;
            RectangleF plot = new RectangleF(bounds.X + valueAxis, bounds.Y + 6, Math.Max(10, bounds.Width - valueAxis - 6), Math.Max(10, bounds.Height - axisHeight - 10));
            using (Pen gridPen = new Pen(ThemeManager.GridColor))
            {
                for (int step = 0; step <= 4; step++)
                {
                    float y = plot.Bottom - plot.Height * step / 4f;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    decimal label = min + (max - min) * step / 4m;
                    UiKit.DrawText(g, BiDashboardService.FormatNumber(label), Font, new Rectangle(bounds.X, (int)y - 8, valueAxis - 4, 16), ThemeManager.MutedTextColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
            float slot = plot.Width / count;
            PointF[] points = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                decimal value = result.Points[i].Value ?? 0m;
                points[i] = new PointF(plot.Left + slot * (i + 0.5f), plot.Bottom - (float)((value - min) / (max - min)) * plot.Height);
            }
            if (count > 1)
            {
                using (Pen line = new Pen(ThemeManager.AccentColor, 2f)) g.DrawLines(line, points);
            }
            int labelEvery = Math.Max(1, (int)Math.Ceiling(count * 70f / Math.Max(1f, plot.Width)));
            for (int i = 0; i < count; i++)
            {
                BiPoint point = result.Points[i];
                float radius = point.Key == selectedKey || point.Key == hoverKey ? 5f : 3.5f;
                using (SolidBrush brush = new SolidBrush(point.Key == selectedKey ? ThemeManager.WarningColor : ColorFor(i, point.Key)))
                {
                    g.FillEllipse(brush, points[i].X - radius, points[i].Y - radius, radius * 2, radius * 2);
                }
                if (i % labelEvery == 0)
                {
                    Rectangle labelBox = new Rectangle((int)(points[i].X - slot * labelEvery / 2f), (int)plot.Bottom + 2, (int)(slot * labelEvery), axisHeight);
                    UiKit.DrawText(g, point.Label, Font, labelBox, ThemeManager.MutedTextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                }
                GraphicsPath hit = new GraphicsPath();
                hit.AddRectangle(new RectangleF(plot.Left + slot * i, plot.Top, slot, plot.Height + axisHeight));
                hitRegions.Add(new KeyValuePair<GraphicsPath, string>(hit, point.Key));
            }
        }

        private void PaintPie(Graphics g, Rectangle bounds)
        {
            List<BiPoint> points = result.Points.Where(point => (point.Value ?? 0m) > 0m).ToList();
            decimal total = points.Sum(point => point.Value.Value);
            if (total <= 0m)
            {
                UiKit.DrawText(g, Localization.T("Bi.PieNeedsPositive"), Font, bounds, ThemeManager.MutedTextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int legendWidth = Math.Min(180, bounds.Width / 2);
            float diameter = Math.Max(10, Math.Min(bounds.Height - 4, bounds.Width - legendWidth - 12));
            RectangleF circle = new RectangleF(bounds.X + 4, bounds.Y + (bounds.Height - diameter) / 2f, diameter, diameter);
            float start = -90f;
            for (int i = 0; i < points.Count; i++)
            {
                BiPoint point = points[i];
                float sweep = (float)(point.Value.Value / total) * 360f;
                if (sweep >= 359.99f) sweep = 359.99f;
                GraphicsPath slice = new GraphicsPath();
                slice.AddPie(circle.X, circle.Y, circle.Width, circle.Height, start, Math.Max(0.5f, sweep));
                int paletteIndex = result.Points.IndexOf(point);
                using (SolidBrush brush = new SolidBrush(ColorFor(paletteIndex, point.Key))) g.FillPath(brush, slice);
                using (Pen separator = new Pen(ThemeManager.ElevatedColor, 1.5f)) g.DrawPath(separator, slice);
                hitRegions.Add(new KeyValuePair<GraphicsPath, string>(slice, point.Key));
                start += sweep;
            }

            float legendLeft = circle.Right + 12;
            float lineHeight = Math.Max(14f, Math.Min(20f, bounds.Height / (float)Math.Max(1, points.Count)));
            for (int i = 0; i < points.Count; i++)
            {
                float y = bounds.Y + i * lineHeight;
                if (y + lineHeight > bounds.Bottom + 1) break;
                BiPoint point = points[i];
                int paletteIndex = result.Points.IndexOf(point);
                using (SolidBrush brush = new SolidBrush(ColorFor(paletteIndex, point.Key))) g.FillRectangle(brush, legendLeft, y + lineHeight / 2f - 4, 8, 8);
                string percent = (point.Value.Value / total).ToString("P0");
                Rectangle text = new Rectangle((int)legendLeft + 12, (int)y, (int)(bounds.Right - legendLeft - 12), (int)lineHeight);
                UiKit.DrawText(g, point.Label + "  " + percent, Font, text, ThemeManager.TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                GraphicsPath hit = new GraphicsPath();
                hit.AddRectangle(new RectangleF(legendLeft, y, bounds.Right - legendLeft, lineHeight));
                hitRegions.Add(new KeyValuePair<GraphicsPath, string>(hit, point.Key));
            }
        }
    }
}

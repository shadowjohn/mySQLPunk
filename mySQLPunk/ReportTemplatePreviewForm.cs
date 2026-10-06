using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>Scrollable, full-resolution sample preview. The source file is never held open.</summary>
    public sealed class ReportTemplatePreviewForm : Form
    {
        private enum PreviewZoom { FitWidth, FitPage, Custom }
        private readonly Panel viewport = new Panel();
        private readonly PreviewImage picture = new PreviewImage();
        private readonly Label zoomLabel = new Label();
        private Image source;
        private PreviewZoom zoomMode = PreviewZoom.FitWidth;
        private double zoom = 1.0;
        private bool arranging;
        private const int Edge = 8;

        private static string T(string chinese, string english) { return ReportText.Get(chinese, english); }

        public ReportTemplatePreviewForm(string imagePath, string title)
        {
            using (Image original = Image.FromFile(imagePath)) source = new Bitmap(original);
            Name = "reportTemplatePreview";
            Text = title;
            Font = UiKit.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(1100, 800);
            MinimumSize = new Size(500, 380);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(6), Margin = Padding.Empty };
            AddButton(toolbar, "reportPreviewFitWidth", T("符合寬度", "Fit width"), () => SetMode(PreviewZoom.FitWidth));
            AddButton(toolbar, "reportPreviewFitPage", T("完整預覽", "Fit preview"), () => SetMode(PreviewZoom.FitPage));
            AddButton(toolbar, "reportPreviewActualSize", "100%", () => SetZoom(1.0));
            AddButton(toolbar, "reportPreviewZoomOut", "−", () => SetZoom(zoom / 1.25));
            AddButton(toolbar, "reportPreviewZoomIn", "+", () => SetZoom(zoom * 1.25));
            zoomLabel.Name = "reportPreviewZoomLabel";
            zoomLabel.AutoSize = true;
            zoomLabel.Padding = new Padding(8, 9, 0, 0);
            toolbar.Controls.Add(zoomLabel);
            toolbar.Controls.Add(new Label { AutoSize = true, Text = T("範例節錄；完整內容請開啟範例檔", "Sample excerpt; open the sample file for full content"), Padding = new Padding(8, 9, 0, 0) });
            layout.Controls.Add(toolbar, 0, 0);

            viewport.Name = "reportPreviewViewport";
            viewport.Dock = DockStyle.Fill;
            viewport.AutoScroll = true;
            viewport.Margin = Padding.Empty;
            viewport.TabStop = true;
            picture.Image = source;
            picture.Name = "reportPreviewImage";
            picture.TabStop = false;
            viewport.Controls.Add(picture);
            layout.Controls.Add(viewport, 0, 1);
            viewport.ClientSizeChanged += (s, e) => ArrangeImage();
            viewport.MouseWheel += (s, e) =>
            {
                if ((ModifierKeys & Keys.Control) == Keys.Control)
                {
                    var mouse = e as HandledMouseEventArgs;
                    if (mouse != null) mouse.Handled = true;
                    SetZoom(e.Delta > 0 ? zoom * 1.25 : zoom / 1.25);
                }
            };
            picture.MouseEnter += (s, e) => viewport.Focus();
            ThemeManager.ApplyTo(this);
        }

        protected override void OnShown(EventArgs e)
        {
            Rectangle working = Screen.FromControl(Owner ?? this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, working.Width), Math.Min(MinimumSize.Height, working.Height));
            Size = new Size(Math.Min(Width, working.Width), Math.Min(Height, working.Height));
            Location = new Point(working.Left + (working.Width - Width) / 2, working.Top + (working.Height - Height) / 2);
            base.OnShown(e);
            ArrangeImage();
            viewport.Focus();
        }

        private static void AddButton(FlowLayoutPanel toolbar, string name, string text, Action action)
        {
            var button = new Button { Name = name, Text = text, AccessibleName = text, AutoSize = true, MinimumSize = new Size(48, 32), Margin = new Padding(3) };
            button.Click += (s, e) => action();
            toolbar.Controls.Add(button);
        }

        private void SetMode(PreviewZoom mode)
        {
            zoomMode = mode;
            viewport.AutoScrollPosition = Point.Empty;
            ArrangeImage();
        }

        private void SetZoom(double value)
        {
            zoomMode = PreviewZoom.Custom;
            zoom = Math.Max(0.1, Math.Min(4.0, value));
            ArrangeImage();
        }

        private void ArrangeImage()
        {
            if (arranging || source == null || viewport.ClientSize.Width < 1 || viewport.ClientSize.Height < 1) return;
            arranging = true;
            try
            {
                int availableWidth = Math.Max(1, viewport.ClientSize.Width + (viewport.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - Edge * 2);
                int availableHeight = Math.Max(1, viewport.ClientSize.Height + (viewport.HorizontalScroll.Visible ? SystemInformation.HorizontalScrollBarHeight : 0) - Edge * 2);
                // Reserve a scrollbar only when the fitted image will actually be taller than the viewport.
                if (zoomMode == PreviewZoom.FitWidth)
                {
                    zoom = (double)availableWidth / source.Width;
                    if (source.Height * zoom > availableHeight)
                        zoom = (double)Math.Max(1, availableWidth - SystemInformation.VerticalScrollBarWidth) / source.Width;
                }
                else if (zoomMode == PreviewZoom.FitPage)
                    zoom = Math.Min((double)availableWidth / source.Width, (double)availableHeight / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * zoom));
                int height = Math.Max(1, (int)Math.Round(source.Height * zoom));
                Point scroll = viewport.AutoScrollPosition;
                viewport.AutoScrollPosition = Point.Empty;
                picture.Bounds = new Rectangle(Edge, Edge, width, height);
                viewport.AutoScrollMinSize = new Size(width + Edge * 2, height + Edge * 2);
                viewport.AutoScrollPosition = new Point(
                    Math.Min(-scroll.X, Math.Max(0, width + Edge * 2 - viewport.ClientSize.Width)),
                    Math.Min(-scroll.Y, Math.Max(0, height + Edge * 2 - viewport.ClientSize.Height)));
                zoomLabel.Text = (zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%  " + T("Ctrl＋滾輪縮放", "Ctrl + wheel to zoom");
            }
            finally { arranging = false; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && source != null) { picture.Image = null; source.Dispose(); source = null; }
            base.Dispose(disposing);
        }

        private sealed class PreviewImage : Control
        {
            public Image Image { get; set; }
            public PreviewImage() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (Image == null) return;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(Image, ClientRectangle, 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel);
            }
        }
    }
}

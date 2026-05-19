using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    public class AnimatedRunnerProgressBar : Control
    {
        private readonly Timer animationTimer;
        private Image runnerImage;
        private MemoryStream runnerImageStream;
        private bool imageAnimated;
        private int animationTick;
        private int maximum = 100;
        private int value;
        private string message = "";

        public AnimatedRunnerProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(360, 40);
            MinimumSize = new Size(180, 32);
            Font = new Font("Segoe UI", 9F);

            animationTimer = new Timer { Interval = 70 };
            animationTimer.Tick += (s, e) =>
            {
                animationTick++;
                if (imageAnimated && runnerImage != null) ImageAnimator.UpdateFrames(runnerImage);
                Invalidate();
            };
            animationTimer.Start();
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                maximum = Math.Max(1, value);
                if (this.value > maximum) this.value = maximum;
                Invalidate();
            }
        }

        public int Value
        {
            get { return value; }
            set
            {
                this.value = Math.Min(Math.Max(value, 0), maximum);
                Invalidate();
            }
        }

        public string Message
        {
            get { return message; }
            set
            {
                message = value ?? "";
                Invalidate();
            }
        }

        public void SetProgress(int current, int total, string progressMessage)
        {
            Maximum = Math.Max(total, 1);
            Value = current;
            Message = progressMessage;
        }

        public void LoadRunnerImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                MemoryStream memory = new MemoryStream(File.ReadAllBytes(path));
                Image image = Image.FromStream(memory);
                SetRunnerImage(image, memory);
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
                if (runnerImage != null)
                {
                    if (imageAnimated) ImageAnimator.StopAnimate(runnerImage, OnRunnerFrameChanged);
                    runnerImage.Dispose();
                    runnerImage = null;
                }
                if (runnerImageStream != null)
                {
                    runnerImageStream.Dispose();
                    runnerImageStream = null;
                }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int runnerSize = Math.Max(18, Math.Min(30, Height - 12));
            Rectangle barRect = new Rectangle(8, Height - 14, Math.Max(10, Width - 16), 8);
            float percent = maximum <= 0 ? 0f : (float)value / maximum;
            percent = Math.Max(0f, Math.Min(1f, percent));
            Color trackColor = ThemeManager.IsDark ? ThemeManager.BorderColor : Color.FromArgb(218, 224, 232);
            Color fillStart = ThemeManager.IsDark ? Color.FromArgb(255, 205, 70) : Color.FromArgb(255, 205, 70);
            Color fillEnd = ThemeManager.IsDark ? ThemeManager.AccentColor : Color.FromArgb(75, 198, 118);

            using (GraphicsPath trackPath = CreateRoundRect(barRect, 4))
            using (SolidBrush trackBrush = new SolidBrush(trackColor))
            {
                g.FillPath(trackBrush, trackPath);
            }

            int fillWidth = Math.Max(0, (int)Math.Round(barRect.Width * percent));
            if (fillWidth > 0)
            {
                Rectangle fillRect = new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height);
                using (GraphicsPath fillPath = CreateRoundRect(fillRect, 4))
                using (LinearGradientBrush fillBrush = new LinearGradientBrush(fillRect, fillStart, fillEnd, LinearGradientMode.Horizontal))
                {
                    g.FillPath(fillBrush, fillPath);
                }

                using (Pen shinePen = new Pen(Color.FromArgb(72, Color.White), 2))
                {
                    int offset = (animationTick * 3) % 22;
                    for (int x = fillRect.X - 24 + offset; x < fillRect.Right + 24; x += 18)
                    {
                        g.DrawLine(shinePen, x, fillRect.Bottom, x + 10, fillRect.Top);
                    }
                }
            }

            int runnerX = barRect.X + (int)Math.Round((barRect.Width - runnerSize) * percent);
            int bob = (int)Math.Round(Math.Sin(animationTick / 2.0) * 2.0);
            int runnerY = Math.Max(0, barRect.Y - runnerSize + 4 + bob);
            DrawRunner(g, new Rectangle(runnerX, runnerY, runnerSize, runnerSize));

            Rectangle textRect = new Rectangle(8, 2, Math.Max(10, Width - 16), Math.Max(14, barRect.Y - 4));
            TextRenderer.DrawText(g, message, Font, textRect, ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        private void SetRunnerImage(Image image, MemoryStream imageStream)
        {
            if (runnerImage != null)
            {
                if (imageAnimated) ImageAnimator.StopAnimate(runnerImage, OnRunnerFrameChanged);
                runnerImage.Dispose();
            }
            if (runnerImageStream != null) runnerImageStream.Dispose();

            runnerImage = image;
            runnerImageStream = imageStream;
            imageAnimated = ImageAnimator.CanAnimate(runnerImage);
            if (imageAnimated) ImageAnimator.Animate(runnerImage, OnRunnerFrameChanged);
            Invalidate();
        }

        private void OnRunnerFrameChanged(object sender, EventArgs e)
        {
            if (!IsDisposed) Invalidate();
        }

        private void DrawRunner(Graphics g, Rectangle bounds)
        {
            if (runnerImage != null)
            {
                if (imageAnimated) ImageAnimator.UpdateFrames(runnerImage);
                g.DrawImage(runnerImage, bounds);
                return;
            }

            using (SolidBrush bodyBrush = new SolidBrush(Color.FromArgb(255, 214, 56)))
            using (SolidBrush earBrush = new SolidBrush(Color.FromArgb(48, 48, 48)))
            using (Pen outline = new Pen(Color.FromArgb(120, 90, 20), 1.5f))
            using (Pen boltPen = new Pen(Color.FromArgb(255, 186, 0), 2.2f))
            {
                int earW = Math.Max(3, bounds.Width / 5);
                g.FillPolygon(earBrush, new[]
                {
                    new Point(bounds.Left + earW, bounds.Top + earW),
                    new Point(bounds.Left + earW * 2, bounds.Top),
                    new Point(bounds.Left + earW * 2, bounds.Top + earW * 2)
                });
                g.FillPolygon(earBrush, new[]
                {
                    new Point(bounds.Right - earW, bounds.Top + earW),
                    new Point(bounds.Right - earW * 2, bounds.Top),
                    new Point(bounds.Right - earW * 2, bounds.Top + earW * 2)
                });

                Rectangle body = new Rectangle(bounds.Left + 2, bounds.Top + 5, bounds.Width - 5, bounds.Height - 8);
                g.FillEllipse(bodyBrush, body);
                g.DrawEllipse(outline, body);

                int eyeY = body.Top + body.Height / 3;
                g.FillEllipse(earBrush, body.Left + body.Width / 3, eyeY, 3, 3);
                g.FillEllipse(earBrush, body.Left + body.Width * 2 / 3, eyeY, 3, 3);

                Point[] bolt =
                {
                    new Point(bounds.Right - 2, bounds.Top + bounds.Height / 2),
                    new Point(bounds.Right + 5, bounds.Top + bounds.Height / 3),
                    new Point(bounds.Right + 1, bounds.Top + bounds.Height * 2 / 3),
                    new Point(bounds.Right + 8, bounds.Bottom - 3)
                };
                g.DrawLines(boltPen, bolt);
            }
        }

        private static GraphicsPath CreateRoundRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

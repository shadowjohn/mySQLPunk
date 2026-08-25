using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace mySQLPunk
{
    /// <summary>
    /// 主功能列專用的彩色向量圖示。每個圖示使用相同的 24x24 格線與雙色語彙，
    /// 但保留足夠不同的輪廓，讓使用者不用讀文字也能快速辨識功能。
    /// </summary>
    internal static class MainToolbarGlyphPainter
    {
        public static bool Supports(UiGlyph glyph)
        {
            return glyph >= UiGlyph.MainConnection && glyph <= UiGlyph.MainBI;
        }

        public static void Draw(
            Graphics g,
            UiGlyph glyph,
            RectangleF bounds,
            Color outline,
            Color primary,
            Color secondary,
            Color accent,
            Color soft,
            float strokeScale)
        {
            if (g == null || !Supports(glyph) || bounds.Width <= 2 || bounds.Height <= 2) return;

            SmoothingMode oldSmoothing = g.SmoothingMode;
            PixelOffsetMode oldPixelOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float size = Math.Min(bounds.Width, bounds.Height);
            RectangleF r = new RectangleF(
                bounds.X + (bounds.Width - size) / 2f,
                bounds.Y + (bounds.Height - size) / 2f,
                size,
                size);
            float unit = size / 24f;
            float stroke = Math.Max(1.1f, unit * 1.35f) * strokeScale;

            using (Pen outlinePen = CreatePen(outline, stroke))
            using (Pen primaryPen = CreatePen(primary, stroke))
            using (Pen secondaryPen = CreatePen(secondary, stroke))
            using (Pen accentPen = CreatePen(accent, stroke))
            using (Pen whitePen = CreatePen(Color.White, Math.Max(1f, stroke * 0.82f)))
            using (SolidBrush outlineBrush = new SolidBrush(outline))
            using (SolidBrush primaryBrush = new SolidBrush(primary))
            using (SolidBrush secondaryBrush = new SolidBrush(secondary))
            using (SolidBrush accentBrush = new SolidBrush(accent))
            using (SolidBrush softBrush = new SolidBrush(soft))
            using (SolidBrush whiteBrush = new SolidBrush(Color.White))
            {
                switch (glyph)
                {
                    case UiGlyph.MainConnection:
                        DrawConnection(g, r, outlinePen, accentPen, primaryBrush, secondaryBrush, accentBrush, softBrush);
                        break;
                    case UiGlyph.MainNewQuery:
                        DrawNewQuery(g, r, outlinePen, secondaryPen, accentPen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen);
                        break;
                    case UiGlyph.MainTable:
                        DrawTable(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen);
                        break;
                    case UiGlyph.MainView:
                        DrawView(g, r, outlinePen, primaryPen, primaryBrush, secondaryBrush, accentBrush, softBrush);
                        break;
                    case UiGlyph.MainFunction:
                        DrawFunction(g, r, outlinePen, primaryPen, primaryBrush, secondaryBrush, accentBrush, softBrush);
                        break;
                    case UiGlyph.MainUser:
                        DrawUser(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen);
                        break;
                    case UiGlyph.MainMore:
                        DrawMore(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen, whiteBrush);
                        break;
                    case UiGlyph.MainQuery:
                        DrawQuery(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen, whiteBrush);
                        break;
                    case UiGlyph.MainBackup:
                        DrawBackup(g, r, outlinePen, primaryPen, secondaryPen, accentPen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen);
                        break;
                    case UiGlyph.MainEvent:
                        DrawEvent(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen, whiteBrush);
                        break;
                    case UiGlyph.MainModel:
                        DrawModel(g, r, outlinePen, primaryBrush, secondaryBrush, accentBrush, softBrush, whitePen);
                        break;
                    case UiGlyph.MainBI:
                        DrawBI(g, r, outlinePen, primaryPen, secondaryPen, accentPen, primaryBrush, secondaryBrush, accentBrush, softBrush);
                        break;
                }
            }

            g.SmoothingMode = oldSmoothing;
            g.PixelOffsetMode = oldPixelOffset;
        }

        private static Pen CreatePen(Color color, float width)
        {
            Pen pen = new Pen(color, width);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        private static PointF P(RectangleF r, float x, float y)
        {
            return new PointF(r.X + r.Width * x / 24f, r.Y + r.Height * y / 24f);
        }

        private static RectangleF R(RectangleF r, float x, float y, float width, float height)
        {
            return new RectangleF(
                r.X + r.Width * x / 24f,
                r.Y + r.Height * y / 24f,
                r.Width * width / 24f,
                r.Height * height / 24f);
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2f);
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 0.1f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            RectangleF arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRounded(Graphics g, Brush brush, RectangleF r, float x, float y, float width, float height, float radius)
        {
            using (GraphicsPath path = RoundedRect(R(r, x, y, width, height), r.Width * radius / 24f))
            {
                g.FillPath(brush, path);
            }
        }

        private static void DrawRounded(Graphics g, Pen pen, RectangleF r, float x, float y, float width, float height, float radius)
        {
            RectangleF bounds = R(r, x, y, width, height);
            bounds.Inflate(-pen.Width / 2f, -pen.Width / 2f);
            using (GraphicsPath path = RoundedRect(bounds, r.Width * radius / 24f))
            {
                g.DrawPath(pen, path);
            }
        }

        private static void Line(Graphics g, Pen pen, RectangleF r, float x1, float y1, float x2, float y2)
        {
            g.DrawLine(pen, P(r, x1, y1), P(r, x2, y2));
        }

        private static void FillCircle(Graphics g, Brush brush, RectangleF r, float x, float y, float size)
        {
            g.FillEllipse(brush, R(r, x, y, size, size));
        }

        private static void DrawCircle(Graphics g, Pen pen, RectangleF r, float x, float y, float size)
        {
            RectangleF bounds = R(r, x, y, size, size);
            bounds.Inflate(-pen.Width / 2f, -pen.Width / 2f);
            g.DrawEllipse(pen, bounds);
        }

        private static void DrawDatabase(Graphics g, RectangleF r, float x, float y, float width, float height, Brush fill, Pen outline)
        {
            float capHeight = Math.Max(2.2f, height * 0.26f);
            g.FillRectangle(fill, R(r, x, y + capHeight / 2f, width, height - capHeight));
            g.FillEllipse(fill, R(r, x, y, width, capHeight));
            g.FillEllipse(fill, R(r, x, y + height - capHeight, width, capHeight));
            g.DrawEllipse(outline, R(r, x, y, width, capHeight));
            Line(g, outline, r, x, y + capHeight / 2f, x, y + height - capHeight / 2f);
            Line(g, outline, r, x + width, y + capHeight / 2f, x + width, y + height - capHeight / 2f);
            g.DrawArc(outline, R(r, x, y + height * 0.44f, width, capHeight), 0, 180);
            g.DrawArc(outline, R(r, x, y + height - capHeight, width, capHeight), 0, 180);
        }

        private static void DrawConnection(Graphics g, RectangleF r, Pen outline, Pen accentPen, Brush primary, Brush secondary, Brush accent, Brush soft)
        {
            FillCircle(g, soft, r, 0.8f, 4.2f, 9.5f);
            FillCircle(g, soft, r, 13.7f, 4.2f, 9.5f);
            DrawDatabase(g, r, 1.5f, 5.5f, 7.5f, 12.5f, primary, outline);
            DrawDatabase(g, r, 15, 5.5f, 7.5f, 12.5f, secondary, outline);
            Line(g, accentPen, r, 9, 11.7f, 15, 11.7f);
            FillCircle(g, accent, r, 10.7f, 9.9f, 3.6f);
            DrawCircle(g, outline, r, 10.7f, 9.9f, 3.6f);
        }

        private static void DrawNewQuery(Graphics g, RectangleF r, Pen outline, Pen secondaryPen, Pen accentPen, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen)
        {
            FillRounded(g, soft, r, 2.5f, 2, 15.5f, 20, 2.3f);
            DrawRounded(g, outline, r, 2.5f, 2, 15.5f, 20, 2.3f);
            using (GraphicsPath fold = new GraphicsPath())
            {
                fold.AddPolygon(new[] { P(r, 13.2f, 2.6f), P(r, 17.5f, 6.9f), P(r, 13.2f, 6.9f) });
                g.FillPath(secondary, fold);
                g.DrawPath(outline, fold);
            }
            Line(g, secondaryPen, r, 5.3f, 8.2f, 11.7f, 8.2f);
            Line(g, secondaryPen, r, 5.3f, 11.2f, 13.6f, 11.2f);
            Line(g, accentPen, r, 5.3f, 14.2f, 10.8f, 14.2f);
            FillCircle(g, primary, r, 14.4f, 14.4f, 8.2f);
            DrawCircle(g, outline, r, 14.4f, 14.4f, 8.2f);
            Line(g, whitePen, r, 18.5f, 16.5f, 18.5f, 20.5f);
            Line(g, whitePen, r, 16.5f, 18.5f, 20.5f, 18.5f);
            using (GraphicsPath sparkle = new GraphicsPath())
            {
                sparkle.AddPolygon(new[] { P(r, 19.5f, 4), P(r, 20.5f, 5.7f), P(r, 22.2f, 6.7f), P(r, 20.5f, 7.7f), P(r, 19.5f, 9.4f), P(r, 18.5f, 7.7f), P(r, 16.8f, 6.7f), P(r, 18.5f, 5.7f) });
                g.FillPath(accent, sparkle);
            }
        }

        private static void DrawTable(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen)
        {
            FillRounded(g, soft, r, 1.5f, 2.5f, 21, 19, 2.6f);
            DrawRounded(g, outline, r, 1.5f, 2.5f, 21, 19, 2.6f);
            FillRounded(g, primary, r, 2.4f, 3.4f, 19.2f, 4.6f, 1.6f);
            FillRounded(g, secondary, r, 3.5f, 9.3f, 5.1f, 3.5f, 0.8f);
            FillRounded(g, accent, r, 9.5f, 9.3f, 5.1f, 3.5f, 0.8f);
            FillRounded(g, secondary, r, 15.5f, 9.3f, 5.1f, 3.5f, 0.8f);
            FillRounded(g, primary, r, 3.5f, 14.4f, 5.1f, 4.1f, 0.8f);
            FillRounded(g, secondary, r, 9.5f, 14.4f, 5.1f, 4.1f, 0.8f);
            FillRounded(g, accent, r, 15.5f, 14.4f, 5.1f, 4.1f, 0.8f);
            Line(g, whitePen, r, 4.3f, 5.7f, 10.2f, 5.7f);
        }

        private static void DrawView(Graphics g, RectangleF r, Pen outline, Pen primaryPen, Brush primary, Brush secondary, Brush accent, Brush soft)
        {
            FillRounded(g, soft, r, 2.2f, 2.5f, 16.5f, 15.5f, 2.2f);
            DrawRounded(g, outline, r, 2.2f, 2.5f, 16.5f, 15.5f, 2.2f);
            FillRounded(g, secondary, r, 3.2f, 3.5f, 14.5f, 3.7f, 1.2f);
            FillRounded(g, primary, r, 4.2f, 9, 8.4f, 5.8f, 1.1f);
            using (GraphicsPath eye = new GraphicsPath())
            {
                eye.AddBezier(P(r, 9, 16.5f), P(r, 12.5f, 11.6f), P(r, 18.7f, 11.6f), P(r, 22, 16.5f));
                eye.AddBezier(P(r, 22, 16.5f), P(r, 18.7f, 21.2f), P(r, 12.5f, 21.2f), P(r, 9, 16.5f));
                eye.CloseFigure();
                g.FillPath(secondary, eye);
                g.DrawPath(outline, eye);
            }
            FillCircle(g, accent, r, 13.4f, 13.9f, 5.2f);
            DrawCircle(g, primaryPen, r, 13.4f, 13.9f, 5.2f);
        }

        private static void DrawFunction(Graphics g, RectangleF r, Pen outline, Pen primaryPen, Brush primary, Brush secondary, Brush accent, Brush soft)
        {
            FillRounded(g, soft, r, 3, 4, 18, 16, 4f);
            DrawRounded(g, outline, r, 3, 4, 18, 16, 4f);
            Line(g, primaryPen, r, 1, 12, 4, 12);
            Line(g, primaryPen, r, 20, 12, 23, 12);
            FillCircle(g, secondary, r, 0.2f, 10.2f, 3.6f);
            FillCircle(g, accent, r, 20.2f, 10.2f, 3.6f);
            DrawCircle(g, outline, r, 0.2f, 10.2f, 3.6f);
            DrawCircle(g, outline, r, 20.2f, 10.2f, 3.6f);
            g.DrawBezier(primaryPen, P(r, 11.2f, 6), P(r, 7.5f, 4.8f), P(r, 9.6f, 18.5f), P(r, 6.5f, 18.2f));
            Line(g, primaryPen, r, 7.3f, 11, 12.2f, 11);
            Line(g, outline, r, 13.7f, 9.5f, 17.8f, 15.5f);
            Line(g, outline, r, 17.8f, 9.5f, 13.7f, 15.5f);
            FillCircle(g, primary, r, 10.1f, 5.3f, 1.8f);
        }

        private static void DrawUser(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen)
        {
            FillCircle(g, soft, r, 1.5f, 1.5f, 19.5f);
            FillCircle(g, primary, r, 7.4f, 4, 7.2f);
            DrawCircle(g, outline, r, 7.4f, 4, 7.2f);
            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddBezier(P(r, 4.2f, 19.2f), P(r, 5.2f, 13.3f), P(r, 16.8f, 13.3f), P(r, 17.8f, 19.2f));
                body.AddLine(P(r, 17.8f, 19.2f), P(r, 4.2f, 19.2f));
                body.CloseFigure();
                g.FillPath(secondary, body);
                g.DrawPath(outline, body);
            }
            DrawDatabase(g, r, 15.2f, 14.1f, 7.2f, 8.4f, accent, outline);
            Line(g, whitePen, r, 17.3f, 18.2f, 20.2f, 18.2f);
        }

        private static void DrawMore(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen, Brush white)
        {
            FillRounded(g, primary, r, 2, 2, 9, 9, 2.5f);
            FillRounded(g, secondary, r, 13, 2, 9, 9, 2.5f);
            FillRounded(g, accent, r, 2, 13, 9, 9, 2.5f);
            FillRounded(g, soft, r, 13, 13, 9, 9, 2.5f);
            DrawRounded(g, outline, r, 2, 2, 9, 9, 2.5f);
            DrawRounded(g, outline, r, 13, 2, 9, 9, 2.5f);
            DrawRounded(g, outline, r, 2, 13, 9, 9, 2.5f);
            DrawRounded(g, outline, r, 13, 13, 9, 9, 2.5f);
            Line(g, whitePen, r, 4.7f, 4.5f, 8.2f, 8.2f);
            Line(g, whitePen, r, 8.2f, 4.5f, 4.7f, 8.2f);
            Line(g, whitePen, r, 16.1f, 4.5f, 16.1f, 8.5f);
            Line(g, whitePen, r, 19.1f, 4.5f, 19.1f, 8.5f);
            Line(g, whitePen, r, 4.6f, 17.5f, 6.5f, 15.5f);
            Line(g, whitePen, r, 6.5f, 15.5f, 8.5f, 19.5f);
            FillCircle(g, white, r, 15.6f, 15.6f, 2.2f);
            FillCircle(g, white, r, 18.3f, 18.3f, 2.2f);
        }

        private static void DrawQuery(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen, Brush white)
        {
            FillRounded(g, primary, r, 1.5f, 3, 19, 17.5f, 2.5f);
            DrawRounded(g, outline, r, 1.5f, 3, 19, 17.5f, 2.5f);
            FillRounded(g, soft, r, 2.5f, 4, 17, 3.6f, 1.2f);
            FillCircle(g, secondary, r, 3.8f, 5, 1.5f);
            FillCircle(g, accent, r, 6.2f, 5, 1.5f);
            Line(g, whitePen, r, 5, 10.4f, 8, 13.2f);
            Line(g, whitePen, r, 8, 13.2f, 5, 16);
            Line(g, whitePen, r, 10.8f, 16, 15.2f, 16);
            FillCircle(g, accent, r, 16.4f, 14.6f, 7.2f);
            DrawCircle(g, outline, r, 16.4f, 14.6f, 7.2f);
            using (GraphicsPath play = new GraphicsPath())
            {
                play.AddPolygon(new[] { P(r, 19.2f, 16.3f), P(r, 19.2f, 20.1f), P(r, 22.2f, 18.2f) });
                g.FillPath(white, play);
            }
        }

        private static void DrawBackup(Graphics g, RectangleF r, Pen outline, Pen primaryPen, Pen secondaryPen, Pen accentPen, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen)
        {
            using (GraphicsPath cloud = new GraphicsPath())
            {
                cloud.AddBezier(P(r, 3, 13.5f), P(r, 0.5f, 10), P(r, 4, 7.7f), P(r, 7.1f, 8.4f));
                cloud.AddBezier(P(r, 7.1f, 8.4f), P(r, 8.5f, 3.3f), P(r, 16.2f, 4), P(r, 16.5f, 9.2f));
                cloud.AddBezier(P(r, 16.5f, 9.2f), P(r, 21.2f, 7.7f), P(r, 23.6f, 13.9f), P(r, 19.5f, 15.3f));
                cloud.AddLine(P(r, 19.5f, 15.3f), P(r, 4.5f, 15.3f));
                cloud.CloseFigure();
                g.FillPath(soft, cloud);
                g.DrawPath(outline, cloud);
            }
            g.DrawArc(primaryPen, R(r, 8.1f, 7.7f, 7.8f, 6.4f), 195, 245);
            using (GraphicsPath arrow = new GraphicsPath())
            {
                arrow.AddPolygon(new[] { P(r, 8, 10.1f), P(r, 11.1f, 9.8f), P(r, 9.5f, 12.5f) });
                g.FillPath(accent, arrow);
            }
            DrawDatabase(g, r, 7.7f, 13.5f, 9, 8.5f, secondary, outline);
            Line(g, whitePen, r, 10, 17.8f, 14.5f, 17.8f);
            Line(g, accentPen, r, 17.2f, 12.4f, 20.5f, 12.4f);
            FillCircle(g, primary, r, 18.9f, 10.8f, 3.2f);
        }

        private static void DrawEvent(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen, Brush white)
        {
            FillRounded(g, soft, r, 2.2f, 3.2f, 18.5f, 18, 2.8f);
            DrawRounded(g, outline, r, 2.2f, 3.2f, 18.5f, 18, 2.8f);
            FillRounded(g, primary, r, 3.1f, 5.2f, 16.7f, 4.4f, 1.2f);
            Line(g, outline, r, 6.2f, 2, 6.2f, 6.2f);
            Line(g, outline, r, 16.5f, 2, 16.5f, 6.2f);
            using (GraphicsPath bolt = new GraphicsPath())
            {
                bolt.AddPolygon(new[] { P(r, 6.3f, 11), P(r, 11.2f, 11), P(r, 8.8f, 14.6f), P(r, 12.1f, 14.6f), P(r, 6.5f, 21), P(r, 8.1f, 16.2f), P(r, 5.2f, 16.2f) });
                g.FillPath(accent, bolt);
                g.DrawPath(outline, bolt);
            }
            FillCircle(g, secondary, r, 14.2f, 14.3f, 8.2f);
            DrawCircle(g, outline, r, 14.2f, 14.3f, 8.2f);
            Line(g, whitePen, r, 18.3f, 16.3f, 18.3f, 18.5f);
            Line(g, whitePen, r, 18.3f, 18.5f, 20.1f, 19.5f);
            FillCircle(g, white, r, 17.5f, 17.7f, 1.6f);
        }

        private static void DrawModel(Graphics g, RectangleF r, Pen outline, Brush primary, Brush secondary, Brush accent, Brush soft, Pen whitePen)
        {
            Line(g, outline, r, 12, 8.5f, 12, 13.2f);
            Line(g, outline, r, 6.5f, 13.2f, 17.5f, 13.2f);
            Line(g, outline, r, 6.5f, 13.2f, 6.5f, 15.5f);
            Line(g, outline, r, 17.5f, 13.2f, 17.5f, 15.5f);
            FillRounded(g, primary, r, 7.5f, 2, 9, 7, 1.8f);
            FillRounded(g, secondary, r, 1.8f, 15, 9.3f, 7, 1.8f);
            FillRounded(g, accent, r, 12.9f, 15, 9.3f, 7, 1.8f);
            DrawRounded(g, outline, r, 7.5f, 2, 9, 7, 1.8f);
            DrawRounded(g, outline, r, 1.8f, 15, 9.3f, 7, 1.8f);
            DrawRounded(g, outline, r, 12.9f, 15, 9.3f, 7, 1.8f);
            Line(g, whitePen, r, 9.3f, 4.7f, 14.7f, 4.7f);
            Line(g, whitePen, r, 3.6f, 17.7f, 9.3f, 17.7f);
            Line(g, whitePen, r, 14.7f, 17.7f, 20.4f, 17.7f);
            FillCircle(g, soft, r, 10.6f, 11.8f, 2.8f);
        }

        private static void DrawBI(Graphics g, RectangleF r, Pen outline, Pen primaryPen, Pen secondaryPen, Pen accentPen, Brush primary, Brush secondary, Brush accent, Brush soft)
        {
            FillRounded(g, soft, r, 1.5f, 2.2f, 21, 19.5f, 2.8f);
            DrawRounded(g, outline, r, 1.5f, 2.2f, 21, 19.5f, 2.8f);
            RectangleF donut = R(r, 4, 5, 7.5f, 7.5f);
            g.DrawArc(primaryPen, donut, -90, 150);
            g.DrawArc(secondaryPen, donut, 60, 115);
            g.DrawArc(accentPen, donut, 175, 95);
            FillRounded(g, primary, r, 14, 12.8f, 2.2f, 6.3f, 0.7f);
            FillRounded(g, secondary, r, 17.2f, 10.2f, 2.2f, 8.9f, 0.7f);
            FillRounded(g, accent, r, 10.8f, 15.2f, 2.2f, 3.9f, 0.7f);
            g.DrawLines(outline, new[] { P(r, 3.8f, 18), P(r, 7.3f, 14.7f), P(r, 10.2f, 16), P(r, 14.8f, 9.5f), P(r, 19.5f, 7.3f) });
            FillCircle(g, secondary, r, 18.3f, 6.1f, 2.4f);
        }
    }
}

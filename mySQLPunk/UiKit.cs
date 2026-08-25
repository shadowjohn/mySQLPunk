using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace mySQLPunk
{
    /// <summary>
    /// 設計系統的量值 token：間距、圓角、控制項尺寸、字級。
    /// 顏色 token 統一由 ThemeManager 提供，避免兩處各自為政。
    /// </summary>
    public static class UiMetrics
    {
        // 間距階梯（4 的倍數）
        public const int Space1 = 4;
        public const int Space2 = 8;
        public const int Space3 = 12;
        public const int Space4 = 16;
        public const int Space5 = 24;

        // 圓角階梯
        public const int RadiusSm = 4;
        public const int RadiusMd = 6;
        public const int RadiusLg = 10;
        public const int RadiusPill = 999;

        // 控制項尺寸
        public const int ControlHeight = 30;
        public const int ButtonMinWidth = 88;
        public const int RowHeight = 28;
        public const int HeaderHeight = 32;
        public const int SectionHeaderHeight = 34;
        public const int TreeItemHeight = 26;
        public const int SplitterWidth = 6;

        // 字級（pt）
        // 字級：WinForms 預設是 Segoe UI 9pt，但繁中在 9pt 的正黑體偏小；
        // 現代桌面工具（VS Code / DataGrip）內文約 13px ≈ 9.75pt，取 9.5pt 為內文基準
        public const float FontSizeCaption = 8.5f;
        public const float FontSizeBody = 9.5f;
        public const float FontSizeSubtitle = 10.5f;
        public const float FontSizeTitle = 12f;
        public const float FontSizeDisplay = 15f;
    }

    /// <summary>
    /// 介面上會用到的向量圖示。全部以程式繪製，不依賴外部圖檔，
    /// 因此不會出現破圖，也能自動跟著主題換色與縮放。
    /// </summary>
    public enum UiGlyph
    {
        None = 0,
        Database,
        Table,
        TableOpen,
        View,
        Function,
        User,
        Plus,
        Trash,
        Pencil,
        Import,
        Export,
        Play,
        Refresh,
        Search,
        Info,
        Code,
        Plug,
        Chart,
        Clock,
        Archive,
        Model,
        Folder,
        FolderOpen,
        Document,
        Filter,
        Columns,
        ChevronRight,
        ChevronLeft,
        ChevronDown,
        PageFirst,
        PageLast,
        Minus,
        ArrowUp,
        ArrowDown,
        Stop,
        Detach,
        Attach,
        Check,
        Close,
        Warning,
        Save,
        Copy,
        More,
        MainConnection,
        MainNewQuery,
        MainTable,
        MainView,
        MainFunction,
        MainUser,
        MainMore,
        MainQuery,
        MainBackup,
        MainEvent,
        MainModel,
        MainBI
    }

    /// <summary>
    /// 繪圖與字型的共用工具。
    /// </summary>
    public static class UiKit
    {
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
        private static string _resolvedFamily;
        private static string _resolvedMonoFamily;

        /// <summary>介面主要字體，若系統沒有偏好字型會逐一往下退。</summary>
        public static string FontFamilyName
        {
            get
            {
                if (_resolvedFamily != null) return _resolvedFamily;

                string[] preferred = { "Microsoft JhengHei UI", "Segoe UI", "Microsoft JhengHei", "Tahoma" };
                foreach (string name in preferred)
                {
                    if (IsFontFamilyInstalled(name))
                    {
                        _resolvedFamily = name;
                        return _resolvedFamily;
                    }
                }

                using (Font systemFont = SystemFonts.MessageBoxFont)
                {
                    _resolvedFamily = systemFont.FontFamily.Name;
                }
                return _resolvedFamily;
            }
        }

        /// <summary>等寬字體，供 SQL、DDL 等程式碼區塊使用。</summary>
        public static string MonoFontFamilyName
        {
            get
            {
                if (_resolvedMonoFamily != null) return _resolvedMonoFamily;

                string[] preferred = { "Cascadia Mono", "Consolas", "Courier New" };
                foreach (string name in preferred)
                {
                    if (IsFontFamilyInstalled(name))
                    {
                        _resolvedMonoFamily = name;
                        return _resolvedMonoFamily;
                    }
                }

                _resolvedMonoFamily = FontFamily.GenericMonospace.Name;
                return _resolvedMonoFamily;
            }
        }

        private static bool IsFontFamilyInstalled(string name)
        {
            try
            {
                using (new FontFamily(name))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取得快取的介面字型，避免重複建立造成 GDI 物件累積。</summary>
        public static Font GetFont(float size, FontStyle style = FontStyle.Regular)
        {
            return GetFont(FontFamilyName, size, style);
        }

        /// <summary>取得快取的等寬字型。</summary>
        public static Font GetMonoFont(float size, FontStyle style = FontStyle.Regular)
        {
            return GetFont(MonoFontFamilyName, size, style);
        }

        public static Font GetFont(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            lock (FontCache)
            {
                Font font;
                if (FontCache.TryGetValue(key, out font)) return font;

                try
                {
                    font = new Font(family, size, style, GraphicsUnit.Point);
                }
                catch
                {
                    using (Font systemFont = SystemFonts.MessageBoxFont)
                    {
                        font = new Font(systemFont.FontFamily, size, style, GraphicsUnit.Point);
                    }
                }

                FontCache[key] = font;
                return font;
            }
        }

        public static Font Body { get { return GetFont(UiMetrics.FontSizeBody); } }
        public static Font BodyBold { get { return GetFont(UiMetrics.FontSizeBody, FontStyle.Bold); } }
        public static Font Caption { get { return GetFont(UiMetrics.FontSizeCaption); } }
        public static Font Subtitle { get { return GetFont(UiMetrics.FontSizeSubtitle, FontStyle.Bold); } }
        public static Font Title { get { return GetFont(UiMetrics.FontSizeTitle, FontStyle.Bold); } }
        public static Font Display { get { return GetFont(UiMetrics.FontSizeDisplay, FontStyle.Bold); } }

        /// <summary>把兩個顏色以 amount 比例混合（0 = a，1 = b）。</summary>
        public static Color Mix(Color a, Color b, float amount)
        {
            if (amount <= 0f) return a;
            if (amount >= 1f) return b;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * amount),
                (int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount),
                (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        /// <summary>依背景亮度挑選可讀性最好的前景色。</summary>
        public static Color ReadableTextOn(Color background)
        {
            double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance > 0.6 ? Color.FromArgb(24, 28, 34) : Color.White;
        }

        /// <summary>建立圓角矩形路徑；半徑會自動夾在矩形能容納的範圍內。</summary>
        public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return path;
            }

            float max = Math.Min(bounds.Width, bounds.Height) / 2f;
            float r = Math.Max(0f, Math.Min(radius, max));

            if (r <= 0.01f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            float d = r * 2f;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, RectangleF bounds, float radius, Color color)
        {
            if (color.A == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
            g.SmoothingMode = previous;
        }

        public static void DrawRounded(Graphics g, RectangleF bounds, float radius, Color color, float width)
        {
            if (color.A == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF inset = RectangleF.Inflate(bounds, -width / 2f, -width / 2f);
            using (GraphicsPath path = RoundedRect(inset, radius))
            using (Pen pen = new Pen(color, width))
            {
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = previous;
        }

        public static void DrawRounded(Graphics g, RectangleF bounds, float radius, Color color)
        {
            DrawRounded(g, bounds, radius, color, 1f);
        }

        /// <summary>畫一條 1px 的水平細線，用於區塊分隔。</summary>
        public static void DrawHairline(Graphics g, int left, int right, int y, Color color)
        {
            using (Pen pen = new Pen(color, 1f))
            {
                g.DrawLine(pen, left, y, right, y);
            }
        }

        /// <summary>畫一條 1px 的垂直細線。</summary>
        public static void DrawVerticalHairline(Graphics g, int x, int top, int bottom, Color color)
        {
            using (Pen pen = new Pen(color, 1f))
            {
                g.DrawLine(pen, x, top, x, bottom);
            }
        }

        /// <summary>在指定範圍畫出向量圖示。</summary>
        public static void DrawGlyph(Graphics g, UiGlyph glyph, RectangleF bounds, Color color, float strokeScale)
        {
            if (glyph == UiGlyph.None || bounds.Width <= 2 || bounds.Height <= 2) return;

            // 主功能列有自己的彩色繪圖系統；單色入口仍使用同一套新輪廓，
            // 讓測試、列印或其他需要單色輸出的情境不會退回舊圖示。
            if (MainToolbarGlyphPainter.Supports(glyph))
            {
                MainToolbarGlyphPainter.Draw(
                    g,
                    glyph,
                    bounds,
                    color,
                    color,
                    color,
                    color,
                    Color.FromArgb(38, color),
                    strokeScale);
                return;
            }

            SmoothingMode previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 以 24x24 的設計格線為基準，維持等比例並置中
            float size = Math.Min(bounds.Width, bounds.Height);
            RectangleF r = new RectangleF(
                bounds.X + (bounds.Width - size) / 2f,
                bounds.Y + (bounds.Height - size) / 2f,
                size,
                size);

            float stroke = Math.Max(1.15f, size / 13f) * strokeScale;
            using (Pen pen = new Pen(color, stroke))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PaintGlyph(g, glyph, r, pen, brush);
            }

            g.SmoothingMode = previousSmoothing;
        }

        public static void DrawGlyph(Graphics g, UiGlyph glyph, RectangleF bounds, Color color)
        {
            DrawGlyph(g, glyph, bounds, color, 1f);
        }

        /// <summary>使用主功能列專用的彩色圖示系統。</summary>
        public static void DrawMainToolbarGlyph(
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
            MainToolbarGlyphPainter.Draw(g, glyph, bounds, outline, primary, secondary, accent, soft, strokeScale);
        }

        /// <summary>把 24x24 設計座標換算成實際畫布座標。</summary>
        private static PointF P(RectangleF r, float x, float y)
        {
            return new PointF(r.X + r.Width * x / 24f, r.Y + r.Height * y / 24f);
        }

        private static RectangleF R(RectangleF r, float x, float y, float w, float h)
        {
            return new RectangleF(
                r.X + r.Width * x / 24f,
                r.Y + r.Height * y / 24f,
                r.Width * w / 24f,
                r.Height * h / 24f);
        }

        private static void Line(Graphics g, Pen pen, RectangleF r, float x1, float y1, float x2, float y2)
        {
            g.DrawLine(pen, P(r, x1, y1), P(r, x2, y2));
        }

        private static void Rect(Graphics g, Pen pen, RectangleF r, float x, float y, float w, float h, float radius)
        {
            using (GraphicsPath path = RoundedRect(R(r, x, y, w, h), r.Width * radius / 24f))
            {
                g.DrawPath(pen, path);
            }
        }

        private static void FillRect(Graphics g, Brush brush, RectangleF r, float x, float y, float w, float h, float radius)
        {
            using (GraphicsPath path = RoundedRect(R(r, x, y, w, h), r.Width * radius / 24f))
            {
                g.FillPath(brush, path);
            }
        }

        private static void PaintGlyph(Graphics g, UiGlyph glyph, RectangleF r, Pen pen, SolidBrush brush)
        {
            GraphicsPath poly;

            switch (glyph)
            {
                case UiGlyph.Database:
                    g.DrawEllipse(pen, R(r, 4, 3, 16, 5));
                    Line(g, pen, r, 4, 5.5f, 4, 18.5f);
                    Line(g, pen, r, 20, 5.5f, 20, 18.5f);
                    g.DrawArc(pen, R(r, 4, 9, 16, 5), 0, 180);
                    g.DrawArc(pen, R(r, 4, 16, 16, 5), 0, 180);
                    break;

                case UiGlyph.Table:
                    Rect(g, pen, r, 3.5f, 4.5f, 17, 15, 2.5f);
                    Line(g, pen, r, 3.5f, 9.5f, 20.5f, 9.5f);
                    Line(g, pen, r, 3.5f, 14.5f, 20.5f, 14.5f);
                    Line(g, pen, r, 10, 9.5f, 10, 19.5f);
                    break;

                case UiGlyph.TableOpen:
                    Rect(g, pen, r, 3.5f, 4.5f, 17, 15, 2.5f);
                    Line(g, pen, r, 3.5f, 9.5f, 20.5f, 9.5f);
                    FillRect(g, brush, r, 4.8f, 10.8f, 4.6f, 2.6f, 1f);
                    Line(g, pen, r, 11.5f, 12.2f, 19.5f, 12.2f);
                    Line(g, pen, r, 4.8f, 16.5f, 19.5f, 16.5f);
                    break;

                case UiGlyph.View:
                    g.DrawCurve(pen, new[] { P(r, 2.5f, 12), P(r, 12, 5.5f), P(r, 21.5f, 12) }, 0.6f);
                    g.DrawCurve(pen, new[] { P(r, 2.5f, 12), P(r, 12, 18.5f), P(r, 21.5f, 12) }, 0.6f);
                    g.DrawEllipse(pen, R(r, 9.4f, 9.4f, 5.2f, 5.2f));
                    break;

                case UiGlyph.Function:
                    // ƒ 與 x 的抽象化：左側曲線 + 右側交叉
                    g.DrawArc(pen, R(r, 5.5f, 4, 7, 7), 200, 160);
                    Line(g, pen, r, 8.5f, 8, 8.5f, 20);
                    Line(g, pen, r, 5.5f, 11.5f, 12, 11.5f);
                    Line(g, pen, r, 14.5f, 12.5f, 20.5f, 19.5f);
                    Line(g, pen, r, 20.5f, 12.5f, 14.5f, 19.5f);
                    break;

                case UiGlyph.User:
                    g.DrawEllipse(pen, R(r, 8, 3.5f, 8, 8));
                    g.DrawArc(pen, R(r, 3.5f, 13, 17, 15), 200, 140);
                    break;

                case UiGlyph.Plus:
                    Line(g, pen, r, 12, 5, 12, 19);
                    Line(g, pen, r, 5, 12, 19, 12);
                    break;

                case UiGlyph.Trash:
                    Line(g, pen, r, 4, 6.5f, 20, 6.5f);
                    Line(g, pen, r, 9.5f, 6.5f, 9.5f, 4.2f);
                    Line(g, pen, r, 9.5f, 4.2f, 14.5f, 4.2f);
                    Line(g, pen, r, 14.5f, 4.2f, 14.5f, 6.5f);
                    Line(g, pen, r, 6, 6.5f, 7, 20);
                    Line(g, pen, r, 18, 6.5f, 17, 20);
                    Line(g, pen, r, 7, 20, 17, 20);
                    Line(g, pen, r, 10.5f, 10, 10.8f, 16.5f);
                    Line(g, pen, r, 13.5f, 10, 13.2f, 16.5f);
                    break;

                case UiGlyph.Pencil:
                    Line(g, pen, r, 4, 20, 4, 16.5f);
                    Line(g, pen, r, 4, 16.5f, 15.5f, 5);
                    Line(g, pen, r, 15.5f, 5, 19, 8.5f);
                    Line(g, pen, r, 19, 8.5f, 7.5f, 20);
                    Line(g, pen, r, 7.5f, 20, 4, 20);
                    Line(g, pen, r, 13, 7.5f, 16.5f, 11);
                    break;

                case UiGlyph.Import:
                    Line(g, pen, r, 12, 3.5f, 12, 14.5f);
                    Line(g, pen, r, 7.5f, 10, 12, 14.5f);
                    Line(g, pen, r, 16.5f, 10, 12, 14.5f);
                    Line(g, pen, r, 4, 17, 4, 20);
                    Line(g, pen, r, 4, 20, 20, 20);
                    Line(g, pen, r, 20, 20, 20, 17);
                    break;

                case UiGlyph.Export:
                    Line(g, pen, r, 12, 14.5f, 12, 3.5f);
                    Line(g, pen, r, 7.5f, 8, 12, 3.5f);
                    Line(g, pen, r, 16.5f, 8, 12, 3.5f);
                    Line(g, pen, r, 4, 17, 4, 20);
                    Line(g, pen, r, 4, 20, 20, 20);
                    Line(g, pen, r, 20, 20, 20, 17);
                    break;

                case UiGlyph.Play:
                    using (poly = new GraphicsPath())
                    {
                        poly.AddPolygon(new[] { P(r, 7.5f, 4.5f), P(r, 19.5f, 12), P(r, 7.5f, 19.5f) });
                        g.FillPath(brush, poly);
                    }
                    break;

                case UiGlyph.Refresh:
                    g.DrawArc(pen, R(r, 4.5f, 4.5f, 15, 15), 60, 250);
                    using (poly = new GraphicsPath())
                    {
                        poly.AddPolygon(new[] { P(r, 17.8f, 2.8f), P(r, 20.2f, 9.6f), P(r, 13.4f, 7.6f) });
                        g.FillPath(brush, poly);
                    }
                    break;

                case UiGlyph.Search:
                    g.DrawEllipse(pen, R(r, 4, 4, 12.5f, 12.5f));
                    Line(g, pen, r, 15.5f, 15.5f, 20, 20);
                    break;

                case UiGlyph.Info:
                    g.DrawEllipse(pen, R(r, 3.5f, 3.5f, 17, 17));
                    Line(g, pen, r, 12, 11, 12, 16.5f);
                    FillRect(g, brush, r, 11.1f, 7.2f, 1.8f, 1.8f, 1f);
                    break;

                case UiGlyph.Code:
                    Line(g, pen, r, 8.5f, 7, 3.5f, 12);
                    Line(g, pen, r, 3.5f, 12, 8.5f, 17);
                    Line(g, pen, r, 15.5f, 7, 20.5f, 12);
                    Line(g, pen, r, 20.5f, 12, 15.5f, 17);
                    Line(g, pen, r, 13.5f, 4.5f, 10.5f, 19.5f);
                    break;

                case UiGlyph.Plug:
                    Rect(g, pen, r, 7, 6.5f, 10, 8, 3f);
                    Line(g, pen, r, 9.5f, 6.5f, 9.5f, 3);
                    Line(g, pen, r, 14.5f, 6.5f, 14.5f, 3);
                    // 纜線用弧線收尾
                    g.DrawCurve(pen, new[] { P(r, 12, 14.5f), P(r, 12.2f, 17.2f), P(r, 9.6f, 19.4f), P(r, 6.8f, 20.6f) }, 0.6f);
                    break;

                case UiGlyph.Chart:
                    Line(g, pen, r, 4, 3.5f, 4, 20);
                    Line(g, pen, r, 4, 20, 20.5f, 20);
                    FillRect(g, brush, r, 7, 13, 3, 5, 1f);
                    FillRect(g, brush, r, 12, 8.5f, 3, 9.5f, 1f);
                    FillRect(g, brush, r, 17, 11, 3, 7, 1f);
                    break;

                case UiGlyph.Clock:
                    g.DrawEllipse(pen, R(r, 3.5f, 3.5f, 17, 17));
                    Line(g, pen, r, 12, 7.5f, 12, 12);
                    Line(g, pen, r, 12, 12, 15.5f, 14);
                    break;

                case UiGlyph.Archive:
                    Rect(g, pen, r, 3.5f, 4, 17, 4.5f, 1.5f);
                    Line(g, pen, r, 5, 8.5f, 5, 20);
                    Line(g, pen, r, 19, 8.5f, 19, 20);
                    Line(g, pen, r, 5, 20, 19, 20);
                    Line(g, pen, r, 9.5f, 12.5f, 14.5f, 12.5f);
                    break;

                case UiGlyph.Model:
                    g.DrawEllipse(pen, R(r, 9, 2.5f, 6, 6));
                    g.DrawEllipse(pen, R(r, 2.5f, 15.5f, 6, 6));
                    g.DrawEllipse(pen, R(r, 15.5f, 15.5f, 6, 6));
                    Line(g, pen, r, 10.3f, 8, 6.6f, 15.4f);
                    Line(g, pen, r, 13.7f, 8, 17.4f, 15.4f);
                    break;

                case UiGlyph.Folder:
                    // 圓角含籤片的現代資料夾
                    using (poly = new GraphicsPath())
                    {
                        poly.AddArc(R(r, 3.5f, 16.4f, 3.1f, 3.1f), 90, 90);
                        poly.AddArc(R(r, 3.5f, 4.8f, 3.1f, 3.1f), 180, 90);
                        poly.AddLine(P(r, 9.1f, 4.8f), P(r, 11.5f, 7.4f));
                        poly.AddLine(P(r, 11.5f, 7.4f), P(r, 18.9f, 7.4f));
                        poly.AddArc(R(r, 17.4f, 7.4f, 3.1f, 3.1f), 270, 90);
                        poly.AddArc(R(r, 17.4f, 16.4f, 3.1f, 3.1f), 0, 90);
                        poly.CloseFigure();
                        g.DrawPath(pen, poly);
                    }
                    break;

                case UiGlyph.FolderOpen:
                    // 後蓋 + 掀開的前蓋
                    using (poly = new GraphicsPath())
                    {
                        poly.AddLine(P(r, 3f, 17.2f), P(r, 3f, 6.3f));
                        poly.AddArc(R(r, 3f, 4.8f, 3f, 3f), 180, 90);
                        poly.AddLine(P(r, 8.4f, 4.8f), P(r, 10.7f, 7.3f));
                        poly.AddLine(P(r, 10.7f, 7.3f), P(r, 17.8f, 7.3f));
                        g.DrawPath(pen, poly);
                    }
                    using (poly = new GraphicsPath())
                    {
                        poly.AddPolygon(new[] { P(r, 3f, 19f), P(r, 6.6f, 10.7f), P(r, 21.3f, 10.7f), P(r, 17.7f, 19f) });
                        g.DrawPath(pen, poly);
                    }
                    break;

                case UiGlyph.Document:
                    // 摺角文件 + 內容線
                    using (poly = new GraphicsPath())
                    {
                        poly.AddLine(P(r, 5.5f, 20.5f), P(r, 5.5f, 3.5f));
                        poly.AddLine(P(r, 5.5f, 3.5f), P(r, 14.5f, 3.5f));
                        poly.AddLine(P(r, 14.5f, 3.5f), P(r, 18.5f, 7.5f));
                        poly.AddLine(P(r, 18.5f, 7.5f), P(r, 18.5f, 20.5f));
                        poly.CloseFigure();
                        g.DrawPath(pen, poly);
                    }
                    Line(g, pen, r, 14.5f, 3.5f, 14.5f, 7.5f);
                    Line(g, pen, r, 14.5f, 7.5f, 18.5f, 7.5f);
                    Line(g, pen, r, 8.5f, 11.5f, 15.5f, 11.5f);
                    Line(g, pen, r, 8.5f, 14.8f, 15.5f, 14.8f);
                    Line(g, pen, r, 8.5f, 18, 13, 18);
                    break;

                case UiGlyph.Filter:
                    Line(g, pen, r, 3.5f, 5.5f, 20.5f, 5.5f);
                    Line(g, pen, r, 6.5f, 11.5f, 17.5f, 11.5f);
                    Line(g, pen, r, 9.5f, 17.5f, 14.5f, 17.5f);
                    break;

                case UiGlyph.Columns:
                    Rect(g, pen, r, 3.5f, 4.5f, 17, 15, 2.5f);
                    Line(g, pen, r, 9.2f, 4.5f, 9.2f, 19.5f);
                    Line(g, pen, r, 14.8f, 4.5f, 14.8f, 19.5f);
                    break;

                case UiGlyph.ChevronRight:
                    Line(g, pen, r, 9.5f, 5.5f, 16, 12);
                    Line(g, pen, r, 16, 12, 9.5f, 18.5f);
                    break;

                case UiGlyph.ChevronLeft:
                    Line(g, pen, r, 14.5f, 5.5f, 8, 12);
                    Line(g, pen, r, 8, 12, 14.5f, 18.5f);
                    break;

                case UiGlyph.ChevronDown:
                    Line(g, pen, r, 5.5f, 9.5f, 12, 16);
                    Line(g, pen, r, 12, 16, 18.5f, 9.5f);
                    break;

                case UiGlyph.PageFirst:
                    Line(g, pen, r, 6.5f, 5.5f, 6.5f, 18.5f);
                    Line(g, pen, r, 17.5f, 5.5f, 11, 12);
                    Line(g, pen, r, 11, 12, 17.5f, 18.5f);
                    break;

                case UiGlyph.PageLast:
                    Line(g, pen, r, 17.5f, 5.5f, 17.5f, 18.5f);
                    Line(g, pen, r, 6.5f, 5.5f, 13, 12);
                    Line(g, pen, r, 13, 12, 6.5f, 18.5f);
                    break;

                case UiGlyph.Minus:
                    Line(g, pen, r, 5, 12, 19, 12);
                    break;

                case UiGlyph.ArrowUp:
                    Line(g, pen, r, 12, 19.5f, 12, 4.5f);
                    Line(g, pen, r, 6, 10.5f, 12, 4.5f);
                    Line(g, pen, r, 18, 10.5f, 12, 4.5f);
                    break;

                case UiGlyph.ArrowDown:
                    Line(g, pen, r, 12, 4.5f, 12, 19.5f);
                    Line(g, pen, r, 6, 13.5f, 12, 19.5f);
                    Line(g, pen, r, 18, 13.5f, 12, 19.5f);
                    break;

                case UiGlyph.Stop:
                    FillRect(g, brush, r, 6.5f, 6.5f, 11, 11, 2f);
                    break;

                case UiGlyph.Detach:
                    Rect(g, pen, r, 3.5f, 8, 12.5f, 12.5f, 2.5f);
                    Line(g, pen, r, 12, 12, 20.5f, 3.5f);
                    Line(g, pen, r, 14.5f, 3.5f, 20.5f, 3.5f);
                    Line(g, pen, r, 20.5f, 3.5f, 20.5f, 9.5f);
                    break;

                case UiGlyph.Attach:
                    Rect(g, pen, r, 8, 3.5f, 12.5f, 12.5f, 2.5f);
                    Line(g, pen, r, 12, 12, 3.5f, 20.5f);
                    Line(g, pen, r, 9.5f, 20.5f, 3.5f, 20.5f);
                    Line(g, pen, r, 3.5f, 20.5f, 3.5f, 14.5f);
                    break;

                case UiGlyph.Check:
                    Line(g, pen, r, 4.5f, 12.5f, 9.5f, 17.5f);
                    Line(g, pen, r, 9.5f, 17.5f, 19.5f, 6.5f);
                    break;

                case UiGlyph.Close:
                    Line(g, pen, r, 6, 6, 18, 18);
                    Line(g, pen, r, 18, 6, 6, 18);
                    break;

                case UiGlyph.Warning:
                    Line(g, pen, r, 12, 3.5f, 21, 19.5f);
                    Line(g, pen, r, 21, 19.5f, 3, 19.5f);
                    Line(g, pen, r, 3, 19.5f, 12, 3.5f);
                    Line(g, pen, r, 12, 9.5f, 12, 14);
                    FillRect(g, brush, r, 11.1f, 16, 1.8f, 1.8f, 1f);
                    break;

                case UiGlyph.Save:
                    Line(g, pen, r, 4, 19.5f, 4, 4.5f);
                    Line(g, pen, r, 4, 4.5f, 16, 4.5f);
                    Line(g, pen, r, 16, 4.5f, 20, 8.5f);
                    Line(g, pen, r, 20, 8.5f, 20, 19.5f);
                    Line(g, pen, r, 4, 19.5f, 20, 19.5f);
                    Rect(g, pen, r, 8, 4.5f, 8, 5, 0.5f);
                    Rect(g, pen, r, 7.5f, 13, 9, 6.5f, 1f);
                    break;

                case UiGlyph.Copy:
                    Rect(g, pen, r, 8.5f, 8.5f, 12, 12, 2.5f);
                    Line(g, pen, r, 5.5f, 15.5f, 3.5f, 15.5f);
                    Line(g, pen, r, 3.5f, 15.5f, 3.5f, 3.5f);
                    Line(g, pen, r, 3.5f, 3.5f, 15.5f, 3.5f);
                    Line(g, pen, r, 15.5f, 3.5f, 15.5f, 5.5f);
                    break;

                case UiGlyph.More:
                    FillRect(g, brush, r, 4.6f, 10.6f, 2.8f, 2.8f, 1.4f);
                    FillRect(g, brush, r, 10.6f, 10.6f, 2.8f, 2.8f, 1.4f);
                    FillRect(g, brush, r, 16.6f, 10.6f, 2.8f, 2.8f, 1.4f);
                    break;

                case UiGlyph.MainConnection:
                    // 資料庫端點接上纜線，比單純電源插頭更貼近「資料庫連線」。
                    g.DrawEllipse(pen, R(r, 2.5f, 4, 11, 4));
                    Line(g, pen, r, 2.5f, 6, 2.5f, 15.5f);
                    Line(g, pen, r, 13.5f, 6, 13.5f, 12.5f);
                    g.DrawArc(pen, R(r, 2.5f, 9.5f, 11, 4), 0, 180);
                    g.DrawArc(pen, R(r, 2.5f, 13.5f, 11, 4), 0, 180);
                    Rect(g, pen, r, 15.5f, 10.5f, 6, 6.5f, 2f);
                    Line(g, pen, r, 17.2f, 10.5f, 17.2f, 8.2f);
                    Line(g, pen, r, 19.8f, 10.5f, 19.8f, 8.2f);
                    g.DrawCurve(pen, new[] { P(r, 18.5f, 17), P(r, 18.5f, 20), P(r, 14.5f, 20.5f), P(r, 12.5f, 18.5f) }, 0.5f);
                    break;

                case UiGlyph.MainNewQuery:
                    // SQL 文件搭配右上角新增徽章。
                    Rect(g, pen, r, 3.5f, 3.5f, 13.5f, 17, 2f);
                    Line(g, pen, r, 6.5f, 9, 9, 11.5f);
                    Line(g, pen, r, 9, 11.5f, 6.5f, 14);
                    Line(g, pen, r, 10.8f, 14, 14, 14);
                    g.DrawEllipse(pen, R(r, 14, 2.5f, 8, 8));
                    Line(g, pen, r, 18, 4.7f, 18, 8.3f);
                    Line(g, pen, r, 16.2f, 6.5f, 19.8f, 6.5f);
                    break;

                case UiGlyph.MainTable:
                    // 帶有強調表頭的資料格，讓主功能列與一般欄位圖示有所區別。
                    Rect(g, pen, r, 2.5f, 3.5f, 19, 17, 2.5f);
                    FillRect(g, brush, r, 4.2f, 5.2f, 15.6f, 3.2f, 1f);
                    Line(g, pen, r, 2.5f, 10.5f, 21.5f, 10.5f);
                    Line(g, pen, r, 2.5f, 15.5f, 21.5f, 15.5f);
                    Line(g, pen, r, 9, 10.5f, 9, 20.5f);
                    Line(g, pen, r, 15, 10.5f, 15, 20.5f);
                    break;

                case UiGlyph.MainView:
                    // 眼睛加上掃描線，表示檢視是資料庫中的可查詢投影。
                    g.DrawCurve(pen, new[] { P(r, 2, 12), P(r, 7, 6.2f), P(r, 12, 5.5f), P(r, 17, 6.2f), P(r, 22, 12) }, 0.45f);
                    g.DrawCurve(pen, new[] { P(r, 2, 12), P(r, 7, 17.8f), P(r, 12, 18.5f), P(r, 17, 17.8f), P(r, 22, 12) }, 0.45f);
                    g.DrawEllipse(pen, R(r, 8.5f, 8.5f, 7, 7));
                    FillRect(g, brush, r, 10.7f, 10.7f, 2.6f, 2.6f, 1.3f);
                    Line(g, pen, r, 5, 3.5f, 7, 5.5f);
                    Line(g, pen, r, 19, 3.5f, 17, 5.5f);
                    break;

                case UiGlyph.MainFunction:
                    // 手寫感 f 搭配 (x)，保持數學函式辨識度又不依賴字型。
                    g.DrawBezier(pen, P(r, 11.5f, 3), P(r, 6, 2.5f), P(r, 9.5f, 17.5f), P(r, 4.5f, 21));
                    Line(g, pen, r, 5.5f, 10, 12.5f, 10);
                    g.DrawArc(pen, R(r, 12.5f, 7, 4, 11), 100, 160);
                    Line(g, pen, r, 15.5f, 10, 19, 15);
                    Line(g, pen, r, 19, 10, 15.5f, 15);
                    g.DrawArc(pen, R(r, 18, 7, 4, 11), 280, 160);
                    break;

                case UiGlyph.MainUser:
                    // 使用者輪廓搭配權限盾牌，對應資料庫帳號與角色管理。
                    g.DrawEllipse(pen, R(r, 4.5f, 3, 7, 7));
                    g.DrawArc(pen, R(r, 1.5f, 11, 14, 12), 195, 150);
                    using (poly = new GraphicsPath())
                    {
                        poly.AddPolygon(new[] { P(r, 15, 11), P(r, 21.5f, 13.2f), P(r, 20.5f, 19), P(r, 18.2f, 21.5f), P(r, 15.8f, 19) });
                        poly.CloseFigure();
                        g.DrawPath(pen, poly);
                    }
                    Line(g, pen, r, 16.8f, 16.3f, 18, 17.5f);
                    Line(g, pen, r, 18, 17.5f, 20.2f, 14.8f);
                    break;

                case UiGlyph.MainMore:
                    // 2x2 工具模組取代省略號，讓「其它」看起來是功能集合。
                    Rect(g, pen, r, 3, 3, 7.5f, 7.5f, 2f);
                    Rect(g, pen, r, 13.5f, 3, 7.5f, 7.5f, 2f);
                    Rect(g, pen, r, 3, 13.5f, 7.5f, 7.5f, 2f);
                    Rect(g, pen, r, 13.5f, 13.5f, 7.5f, 7.5f, 2f);
                    FillRect(g, brush, r, 5.4f, 5.4f, 2.7f, 2.7f, 1.2f);
                    FillRect(g, brush, r, 15.9f, 15.9f, 2.7f, 2.7f, 1.2f);
                    break;

                case UiGlyph.MainQuery:
                    // SQL 終端視窗，以提示符號與游標表達可執行查詢。
                    Rect(g, pen, r, 2.5f, 3.5f, 19, 17, 2.5f);
                    Line(g, pen, r, 2.5f, 8, 21.5f, 8);
                    FillRect(g, brush, r, 5, 5.2f, 1.6f, 1.6f, 0.8f);
                    FillRect(g, brush, r, 8, 5.2f, 1.6f, 1.6f, 0.8f);
                    Line(g, pen, r, 6, 11.5f, 9, 14.5f);
                    Line(g, pen, r, 9, 14.5f, 6, 17.5f);
                    Line(g, pen, r, 12, 17.5f, 17.5f, 17.5f);
                    break;

                case UiGlyph.MainBackup:
                    // 資料庫圓柱加回復箭頭，直接傳達資料庫備份／還原。
                    g.DrawEllipse(pen, R(r, 2.5f, 4, 11.5f, 4));
                    Line(g, pen, r, 2.5f, 6, 2.5f, 17.5f);
                    Line(g, pen, r, 14, 6, 14, 10);
                    g.DrawArc(pen, R(r, 2.5f, 9, 11.5f, 4), 0, 180);
                    g.DrawArc(pen, R(r, 2.5f, 14.5f, 11.5f, 4), 0, 180);
                    g.DrawArc(pen, R(r, 11.5f, 9.5f, 10, 10), 35, 285);
                    using (poly = new GraphicsPath())
                    {
                        poly.AddPolygon(new[] { P(r, 12, 8.5f), P(r, 17.3f, 9.4f), P(r, 14.5f, 13.8f) });
                        g.FillPath(brush, poly);
                    }
                    break;

                case UiGlyph.MainEvent:
                    // 日曆與時鐘組合，對應排程事件而不是一般時間顯示。
                    Rect(g, pen, r, 2.5f, 4.5f, 16.5f, 16, 2.5f);
                    Line(g, pen, r, 2.5f, 9, 19, 9);
                    Line(g, pen, r, 6.5f, 2.5f, 6.5f, 6.5f);
                    Line(g, pen, r, 15, 2.5f, 15, 6.5f);
                    g.DrawEllipse(pen, R(r, 12.5f, 12, 9, 9));
                    Line(g, pen, r, 17, 14.2f, 17, 16.5f);
                    Line(g, pen, r, 17, 16.5f, 19, 17.7f);
                    FillRect(g, brush, r, 5, 12, 2.3f, 2.3f, 1f);
                    break;

                case UiGlyph.MainModel:
                    // 兩張實體表以關聯線相接，取代抽象圓點模型。
                    Rect(g, pen, r, 2.5f, 3.5f, 8.5f, 7.5f, 1.8f);
                    Line(g, pen, r, 2.5f, 6.8f, 11, 6.8f);
                    Rect(g, pen, r, 13, 13, 8.5f, 7.5f, 1.8f);
                    Line(g, pen, r, 13, 16.3f, 21.5f, 16.3f);
                    Line(g, pen, r, 11, 7.2f, 16.5f, 7.2f);
                    Line(g, pen, r, 16.5f, 7.2f, 16.5f, 13);
                    FillRect(g, brush, r, 5, 8.2f, 3.5f, 1.2f, 0.6f);
                    FillRect(g, brush, r, 15.5f, 17.8f, 3.5f, 1.2f, 0.6f);
                    break;

                case UiGlyph.MainBI:
                    // 儀表板同時呈現長條與趨勢線，和一般 Chart 圖示區隔。
                    Rect(g, pen, r, 2.5f, 3.5f, 19, 17, 2.5f);
                    Line(g, pen, r, 2.5f, 8, 21.5f, 8);
                    FillRect(g, brush, r, 5, 14.5f, 2.5f, 3.5f, 0.8f);
                    FillRect(g, brush, r, 9, 11.5f, 2.5f, 6.5f, 0.8f);
                    FillRect(g, brush, r, 13, 13.2f, 2.5f, 4.8f, 0.8f);
                    g.DrawLines(pen, new[] { P(r, 5.5f, 12.5f), P(r, 9.5f, 9.8f), P(r, 14, 11), P(r, 19, 8.8f) });
                    FillRect(g, brush, r, 17.8f, 7.6f, 2.4f, 2.4f, 1.2f);
                    break;
            }
        }

        /// <summary>把向量圖示畫成點陣圖，供 ToolStrip 這類只吃 Image 的控制項使用。</summary>
        public static Bitmap RenderGlyph(UiGlyph glyph, int size, Color color, float strokeScale)
        {
            Bitmap bitmap = new Bitmap(Math.Max(1, size), Math.Max(1, size));
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawGlyph(g, glyph, new RectangleF(0, 0, size, size), color, strokeScale);
            }
            return bitmap;
        }

        public static Bitmap RenderGlyph(UiGlyph glyph, int size, Color color)
        {
            return RenderGlyph(glyph, size, color, 1f);
        }

        /// <summary>把彩色主功能圖示輸出成透明點陣圖，供測試與設計預覽使用。</summary>
        public static Bitmap RenderMainToolbarGlyph(
            UiGlyph glyph,
            int size,
            Color outline,
            Color primary,
            Color secondary,
            Color accent,
            Color soft,
            float strokeScale)
        {
            Bitmap bitmap = new Bitmap(Math.Max(1, size), Math.Max(1, size));
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawMainToolbarGlyph(g, glyph, new RectangleF(0, 0, size, size), outline, primary, secondary, accent, soft, strokeScale);
            }
            return bitmap;
        }

        /// <summary>把既有的點陣圖示縮放到指定尺寸並置中，維持等比例。</summary>
        public static Bitmap ScaleImage(Image source, int canvasSize, int contentSize)
        {
            Bitmap bitmap = new Bitmap(Math.Max(1, canvasSize), Math.Max(1, canvasSize));
            if (source == null) return bitmap;

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float scale = Math.Min((float)contentSize / source.Width, (float)contentSize / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                g.DrawImage(source, new Rectangle((canvasSize - width) / 2, (canvasSize - height) / 2, width, height));
            }
            return bitmap;
        }

        /// <summary>畫文字的統一入口，一律不解析 &amp; 快捷鍵前綴。</summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix);
        }
    }
}

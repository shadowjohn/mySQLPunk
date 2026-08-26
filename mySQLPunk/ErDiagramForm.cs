using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ErDiagramForm : Form, IDockableForm
    {
        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly ErDiagramCanvas canvas;
        private readonly ToolStrip toolStrip;
        private readonly ToolStripButton refreshButton;
        private readonly ToolStripButton zoomOutButton;
        private readonly ToolStripButton zoomInButton;
        private readonly ToolStripButton fitButton;
        private readonly ToolStripButton exportButton;
        private readonly ToolStripButton floatButton;
        private readonly ToolStripButton dockButton;
        private readonly ToolStripLabel zoomLabel;
        private readonly ToolStripStatusLabel statusLabel;
        private Form1 mainHost;
        private bool loaded;

        public ErDiagramForm(IDatabase database, string databaseName)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName ?? string.Empty;

            Text = Localization.Format("ErDiagram.Title", this.databaseName);
            Width = 1120;
            Height = 760;
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;

            toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            refreshButton = new ToolStripButton(Localization.T("ErDiagram.Refresh"));
            zoomOutButton = new ToolStripButton("−");
            zoomLabel = new ToolStripLabel("100%");
            zoomInButton = new ToolStripButton("+");
            fitButton = new ToolStripButton(Localization.T("ErDiagram.Fit"));
            exportButton = new ToolStripButton(Localization.T("ErDiagram.ExportPng"));
            floatButton = new ToolStripButton(Localization.T("Query.Float"));
            dockButton = new ToolStripButton(Localization.T("Query.Dock")) { Visible = false };

            toolStrip.Items.AddRange(new ToolStripItem[]
            {
                refreshButton,
                new ToolStripSeparator(),
                zoomOutButton,
                zoomLabel,
                zoomInButton,
                fitButton,
                new ToolStripSeparator(),
                exportButton,
                new ToolStripSeparator(),
                floatButton,
                dockButton
            });

            canvas = new ErDiagramCanvas { Dock = DockStyle.Fill };
            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("ErDiagram.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(canvas);
            Controls.Add(statusStrip);
            Controls.Add(toolStrip);

            refreshButton.Click += (sender, args) => RefreshDiagram();
            zoomOutButton.Click += (sender, args) => canvas.ZoomBy(-0.1f);
            zoomInButton.Click += (sender, args) => canvas.ZoomBy(0.1f);
            fitButton.Click += (sender, args) => canvas.FitToWindow();
            exportButton.Click += (sender, args) => ExportPng();
            floatButton.Click += (sender, args) => { if (mainHost != null) mainHost.FloatDockableForm(this); };
            dockButton.Click += (sender, args) => { if (mainHost != null) mainHost.DockDockableForm(this); };
            canvas.ZoomChanged += (sender, args) => zoomLabel.Text = Math.Round(canvas.Zoom * 100f) + "%";

            Shown += (sender, args) =>
            {
                if (loaded) return;
                loaded = true;
                RefreshDiagram();
            };

            ThemeManager.ApplyTo(this);
            canvas.ApplyTheme();
        }

        public void SetMainHost(Form1 mainHost)
        {
            this.mainHost = mainHost;
        }

        public string GetDisplayTitle()
        {
            return Text;
        }

        public bool HasUnsavedChanges()
        {
            return false;
        }

        public bool UsesDatabase(IDatabase targetDatabase)
        {
            return targetDatabase != null && ReferenceEquals(database, targetDatabase);
        }

        public void PrepareForDocking()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            TopMost = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            floatButton.Visible = true;
            dockButton.Visible = false;
        }

        public void PrepareForFloating()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            Dock = DockStyle.None;
            TopLevel = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            floatButton.Visible = false;
            dockButton.Visible = mainHost != null;
        }

        private void RefreshDiagram()
        {
            Cursor previousCursor = Cursor;
            refreshButton.Enabled = false;
            statusLabel.Text = Localization.T("ErDiagram.Loading");
            Cursor = Cursors.WaitCursor;
            try
            {
                SchemaModelSnapshot snapshot = SchemaModelService.Load(database, databaseName);
                canvas.SetSnapshot(snapshot);
                statusLabel.Text = Localization.Format(
                    snapshot.Warnings.Count == 0 ? "ErDiagram.Status" : "ErDiagram.StatusWithWarnings",
                    snapshot.Tables.Count,
                    snapshot.Relationships.Count,
                    snapshot.Warnings.Count);
                BeginInvoke(new Action(canvas.FitToWindow));
            }
            catch (Exception ex)
            {
                statusLabel.Text = Localization.Format("ErDiagram.LoadFailed", ExceptionMessageService.GetReason(ex));
                MessageBox.Show(statusLabel.Text, Localization.T("View.ERDiagram"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                refreshButton.Enabled = true;
            }
        }

        private void ExportPng()
        {
            if (!canvas.HasDiagram)
            {
                MessageBox.Show(Localization.T("ErDiagram.NothingToExport"), Localization.T("View.ERDiagram"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PNG|*.png",
                DefaultExt = "png",
                AddExtension = true,
                FileName = MakeSafeFileName(databaseName) + "_er_diagram.png",
                Title = Localization.T("ErDiagram.ExportPng")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (Bitmap bitmap = canvas.RenderDiagramToBitmap())
                    {
                        bitmap.Save(dialog.FileName, ImageFormat.Png);
                    }
                    statusLabel.Text = Localization.Format("ErDiagram.Exported", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("ErDiagram.ExportFailed", ExceptionMessageService.GetReason(ex)),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string MakeSafeFileName(string value)
        {
            string output = string.IsNullOrWhiteSpace(value) ? "database" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) output = output.Replace(invalid, '_');
            return output;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (mainHost != null) mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }
    }

    public sealed class ErDiagramCanvas : ScrollableControl
    {
        private const int CardWidth = 300;
        private const int HeaderHeight = 38;
        private const int RowHeight = 24;
        private const int HorizontalGap = 110;
        private const int VerticalGap = 70;
        private const int DiagramMargin = 48;
        private const int MaximumVisibleColumns = 16;

        private readonly List<TableCard> cards = new List<TableCard>();
        private SchemaModelSnapshot snapshot;
        private Size logicalSize = new Size(1, 1);
        private float zoom = 1f;
        private bool panning;
        private Point panStart;
        private Point scrollStart;

        public ErDiagramCanvas()
        {
            AutoScroll = true;
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        public event EventHandler ZoomChanged;

        public float Zoom
        {
            get { return zoom; }
        }

        public bool HasDiagram
        {
            get { return snapshot != null && snapshot.Tables.Count > 0; }
        }

        public Size LogicalDiagramSize
        {
            get { return logicalSize; }
        }

        public void ApplyTheme()
        {
            BackColor = ThemeManager.WindowBackColor;
            ForeColor = ThemeManager.TextColor;
            Invalidate();
        }

        public void SetSnapshot(SchemaModelSnapshot value)
        {
            snapshot = value;
            BuildLayout();
            AutoScrollPosition = Point.Empty;
            Invalidate();
        }

        public void ZoomBy(float delta)
        {
            SetZoom(zoom + delta);
        }

        public void FitToWindow()
        {
            if (!HasDiagram || logicalSize.Width <= 0 || logicalSize.Height <= 0) return;
            float availableWidth = Math.Max(1, ClientSize.Width - 28);
            float availableHeight = Math.Max(1, ClientSize.Height - 28);
            float fit = Math.Min(availableWidth / logicalSize.Width, availableHeight / logicalSize.Height);
            SetZoom(Math.Min(1f, fit));
            AutoScrollPosition = Point.Empty;
        }

        public Bitmap RenderDiagramToBitmap()
        {
            if (!HasDiagram) throw new InvalidOperationException("Diagram is empty.");
            const float maximumDimension = 8000f;
            const float maximumPixels = 48000000f;
            float dimensionScale = Math.Min(maximumDimension / logicalSize.Width, maximumDimension / logicalSize.Height);
            float pixelScale = (float)Math.Sqrt(maximumPixels / Math.Max(1d, logicalSize.Width * (double)logicalSize.Height));
            float exportScale = Math.Min(1f, Math.Min(dimensionScale, pixelScale));
            int width = Math.Max(1, (int)Math.Ceiling(logicalSize.Width * exportScale));
            int height = Math.Max(1, (int)Math.Ceiling(logicalSize.Height * exportScale));
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(ThemeManager.WindowBackColor);
                graphics.ScaleTransform(exportScale, exportScale);
                DrawDiagram(graphics);
            }
            return bitmap;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (!HasDiagram)
            {
                using (Brush brush = new SolidBrush(ThemeManager.MutedTextColor))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    e.Graphics.DrawString(Localization.T("ErDiagram.Empty"), Font, brush, ClientRectangle, format);
                }
                return;
            }

            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            e.Graphics.ScaleTransform(zoom, zoom);
            DrawDiagram(e.Graphics);
            e.Graphics.Restore(state);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ZoomBy(e.Delta > 0 ? 0.1f : -0.1f);
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Middle) return;
            panning = true;
            panStart = e.Location;
            scrollStart = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
            Cursor = Cursors.Hand;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!panning) return;
            AutoScrollPosition = new Point(
                Math.Max(0, scrollStart.X - (e.X - panStart.X)),
                Math.Max(0, scrollStart.Y - (e.Y - panStart.Y)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Middle || !panning) return;
            panning = false;
            Capture = false;
            Cursor = Cursors.Default;
        }

        private void SetZoom(float value)
        {
            float next = Math.Max(0.1f, Math.Min(2f, value));
            if (Math.Abs(next - zoom) < 0.001f) return;
            zoom = next;
            UpdateScrollSize();
            Invalidate();
            EventHandler handler = ZoomChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void BuildLayout()
        {
            cards.Clear();
            if (!HasDiagram)
            {
                logicalSize = new Size(1, 1);
                UpdateScrollSize();
                return;
            }

            int columnCount = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(snapshot.Tables.Count)));
            int[] rowHeights = new int[(int)Math.Ceiling(snapshot.Tables.Count / (double)columnCount)];
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                SchemaTableModel table = snapshot.Tables[index];
                int visible = Math.Min(MaximumVisibleColumns, table.Columns.Count);
                int extraRow = table.Columns.Count > MaximumVisibleColumns ? 1 : 0;
                int height = HeaderHeight + Math.Max(1, visible + extraRow) * RowHeight + 8;
                int row = index / columnCount;
                rowHeights[row] = Math.Max(rowHeights[row], height);
            }

            int y = DiagramMargin;
            int maxRight = DiagramMargin;
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                int row = index / columnCount;
                int column = index % columnCount;
                if (column == 0 && row > 0) y += rowHeights[row - 1] + VerticalGap;

                SchemaTableModel table = snapshot.Tables[index];
                int visible = Math.Min(MaximumVisibleColumns, table.Columns.Count);
                int extraRow = table.Columns.Count > MaximumVisibleColumns ? 1 : 0;
                int height = HeaderHeight + Math.Max(1, visible + extraRow) * RowHeight + 8;
                Rectangle bounds = new Rectangle(
                    DiagramMargin + column * (CardWidth + HorizontalGap),
                    y,
                    CardWidth,
                    height);
                cards.Add(new TableCard(table, bounds));
                maxRight = Math.Max(maxRight, bounds.Right);
            }

            int lastRow = rowHeights.Length - 1;
            int bottom = y + rowHeights[lastRow] + DiagramMargin;
            logicalSize = new Size(maxRight + DiagramMargin, Math.Max(bottom, DiagramMargin * 2));
            UpdateScrollSize();
        }

        private void UpdateScrollSize()
        {
            AutoScrollMinSize = new Size(
                Math.Max(1, (int)Math.Ceiling(logicalSize.Width * zoom)),
                Math.Max(1, (int)Math.Ceiling(logicalSize.Height * zoom)));
        }

        private void DrawDiagram(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawRelationships(graphics);
            foreach (TableCard card in cards) DrawTable(graphics, card);
        }

        private void DrawRelationships(Graphics graphics)
        {
            if (snapshot == null) return;
            Dictionary<string, TableCard> byName = cards.ToDictionary(card => card.Table.Name, StringComparer.OrdinalIgnoreCase);
            using (Pen pen = new Pen(ThemeManager.AccentColor, 1.6f))
            using (AdjustableArrowCap arrow = new AdjustableArrowCap(3.5f, 5f, true))
            {
                pen.CustomEndCap = arrow;
                foreach (SchemaRelationshipModel relationship in snapshot.Relationships)
                {
                    TableCard from;
                    TableCard to;
                    if (!byName.TryGetValue(relationship.FromTable, out from) || !byName.TryGetValue(relationship.ToTable, out to)) continue;

                    PointF start = GetColumnAnchor(from, relationship.FromColumn, true);
                    PointF end = GetColumnAnchor(to, relationship.ToColumn, false);
                    if (ReferenceEquals(from, to))
                    {
                        float loopX = from.Bounds.Right + 34;
                        graphics.DrawLines(pen, new[]
                        {
                            start,
                            new PointF(loopX, start.Y),
                            new PointF(loopX, end.Y + 18),
                            new PointF(end.X, end.Y + 18),
                            end
                        });
                        continue;
                    }

                    bool leftToRight = from.Bounds.Left <= to.Bounds.Left;
                    start.X = leftToRight ? from.Bounds.Right : from.Bounds.Left;
                    end.X = leftToRight ? to.Bounds.Left : to.Bounds.Right;
                    float middleX = (start.X + end.X) / 2f;
                    graphics.DrawLines(pen, new[]
                    {
                        start,
                        new PointF(middleX, start.Y),
                        new PointF(middleX, end.Y),
                        end
                    });
                }
            }
        }

        private static PointF GetColumnAnchor(TableCard card, string columnName, bool right)
        {
            int index = card.Table.Columns.FindIndex(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 0;
            index = Math.Min(index, MaximumVisibleColumns);
            float y = card.Bounds.Top + HeaderHeight + index * RowHeight + RowHeight / 2f;
            return new PointF(right ? card.Bounds.Right : card.Bounds.Left, y);
        }

        private void DrawTable(Graphics graphics, TableCard card)
        {
            Rectangle bounds = card.Bounds;
            using (Brush cardBrush = new SolidBrush(ThemeManager.ElevatedColor))
            using (Brush headerBrush = new SolidBrush(ThemeManager.AccentSoftColor))
            using (Pen borderPen = new Pen(ThemeManager.BorderStrongColor))
            using (Pen rowPen = new Pen(ThemeManager.GridColor))
            using (Brush textBrush = new SolidBrush(ThemeManager.TextColor))
            using (Brush mutedBrush = new SolidBrush(ThemeManager.MutedTextColor))
            using (Brush keyBrush = new SolidBrush(ThemeManager.AccentColor))
            using (Font headerFont = new Font(Font, FontStyle.Bold))
            using (Font keyFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f), FontStyle.Bold))
            using (StringFormat headerFormat = EllipsisFormat(StringAlignment.Near))
            using (StringFormat nameFormat = EllipsisFormat(StringAlignment.Near))
            using (StringFormat typeFormat = EllipsisFormat(StringAlignment.Far))
            {
                graphics.FillRectangle(cardBrush, bounds);
                graphics.FillRectangle(headerBrush, new Rectangle(bounds.Left, bounds.Top, bounds.Width, HeaderHeight));
                graphics.DrawRectangle(borderPen, bounds);
                graphics.DrawString(card.Table.Name, headerFont, textBrush,
                    new RectangleF(bounds.Left + 12, bounds.Top + 9, bounds.Width - 24, HeaderHeight - 12), headerFormat);

                int visible = Math.Min(MaximumVisibleColumns, card.Table.Columns.Count);
                if (visible == 0)
                {
                    graphics.DrawString(Localization.T("ErDiagram.NoColumns"), Font, mutedBrush,
                        new RectangleF(bounds.Left + 12, bounds.Top + HeaderHeight + 5, bounds.Width - 24, RowHeight), nameFormat);
                }
                for (int index = 0; index < visible; index++)
                {
                    SchemaColumnModel column = card.Table.Columns[index];
                    int rowTop = bounds.Top + HeaderHeight + index * RowHeight;
                    graphics.DrawLine(rowPen, bounds.Left, rowTop, bounds.Right, rowTop);
                    if (column.IsPrimaryKey)
                    {
                        graphics.DrawString("PK", keyFont, keyBrush,
                            new RectangleF(bounds.Left + 9, rowTop + 5, 24, RowHeight - 6), nameFormat);
                    }
                    graphics.DrawString(column.Name, Font, textBrush,
                        new RectangleF(bounds.Left + 36, rowTop + 4, 142, RowHeight - 6), nameFormat);
                    string typeText = (column.DataType ?? string.Empty) + (column.IsNullable ? " ?" : string.Empty);
                    graphics.DrawString(typeText.Trim(), Font, mutedBrush,
                        new RectangleF(bounds.Left + 174, rowTop + 4, bounds.Width - 184, RowHeight - 6), typeFormat);
                }

                if (card.Table.Columns.Count > MaximumVisibleColumns)
                {
                    int rowTop = bounds.Top + HeaderHeight + visible * RowHeight;
                    graphics.DrawLine(rowPen, bounds.Left, rowTop, bounds.Right, rowTop);
                    graphics.DrawString(Localization.Format("ErDiagram.MoreColumns", card.Table.Columns.Count - MaximumVisibleColumns),
                        Font, mutedBrush, new RectangleF(bounds.Left + 12, rowTop + 4, bounds.Width - 24, RowHeight - 6), nameFormat);
                }
            }
        }

        private static StringFormat EllipsisFormat(StringAlignment alignment)
        {
            return new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        private sealed class TableCard
        {
            public TableCard(SchemaTableModel table, Rectangle bounds)
            {
                Table = table;
                Bounds = bounds;
            }

            public SchemaTableModel Table { get; private set; }
            public Rectangle Bounds { get; private set; }
        }
    }
}

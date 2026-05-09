using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace mySQLPunk
{
    public class ConnectionTypeSelectionForm : Form
    {
        public const string MySql = "mysql";
        public const string PostgreSql = "postgresql";
        public const string SqlServer = "sqlserver";
        public const string Oracle = "oracle";
        public const string Sqlite = "sqlite";

        private readonly TextBox searchBox;
        private readonly Button gridButton;
        private readonly Button listButton;
        private readonly Button nextButton;
        private readonly FlowLayoutPanel recentPanel;
        private readonly FlowLayoutPanel allPanel;
        private readonly List<ConnectionTypeOption> options;
        private readonly List<ConnectionTypeCard> cards = new List<ConnectionTypeCard>();
        private bool listMode;

        public string SelectedConnectionType { get; private set; }

        public ConnectionTypeSelectionForm()
        {
            Text = Localization.T("ConnectionWizard.Title");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(660, 430);
            MinimumSize = new Size(600, 380);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            string imagePath = Path.Combine(Application.StartupPath, "image");
            options = new List<ConnectionTypeOption>
            {
                new ConnectionTypeOption(MySql, "MySQL", Color.FromArgb(0, 117, 143), Path.Combine(imagePath, "brand_mysql.png")),
                new ConnectionTypeOption(PostgreSql, "PostgreSQL", Color.FromArgb(51, 103, 145), Path.Combine(imagePath, "brand_postgresql.png")),
                new ConnectionTypeOption(SqlServer, "SQL Server", Color.FromArgb(190, 44, 44), Path.Combine(imagePath, "brand_sqlserver.png")),
                new ConnectionTypeOption(Oracle, "Oracle", Color.FromArgb(198, 24, 24), Path.Combine(imagePath, "brand_oracle.png")),
                new ConnectionTypeOption(Sqlite, "SQLite", Color.FromArgb(0, 109, 165), Path.Combine(imagePath, "brand_sqlite.png"))
            };

            Label titleLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.SelectType"),
                AutoSize = true,
                Location = new Point(14, 14)
            };

            gridButton = new Button
            {
                Text = "▦",
                Location = new Point(390, 8),
                Size = new Size(28, 28),
                FlatStyle = FlatStyle.Flat
            };
            listButton = new Button
            {
                Text = "☰",
                Location = new Point(420, 8),
                Size = new Size(28, 28),
                FlatStyle = FlatStyle.Flat
            };

            searchBox = new TextBox
            {
                Location = new Point(456, 10),
                Width = 150
            };
            searchBox.TextChanged += (s, e) => RenderCards();
            searchBox.GotFocus += (s, e) =>
            {
                if (searchBox.Text == Localization.T("ConnectionWizard.Search"))
                {
                    searchBox.Text = string.Empty;
                    searchBox.ForeColor = ThemeManager.TextColor;
                }
            };
            searchBox.LostFocus += (s, e) => ApplySearchPlaceholder();

            Label recentLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.Recent"),
                AutoSize = true,
                Location = new Point(14, 54)
            };

            recentPanel = new FlowLayoutPanel
            {
                Location = new Point(14, 78),
                Size = new Size(610, 108),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                WrapContents = true
            };

            Label allLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.All"),
                AutoSize = true,
                Location = new Point(14, 204)
            };

            allPanel = new FlowLayoutPanel
            {
                Location = new Point(14, 230),
                Size = new Size(610, 110),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                WrapContents = true
            };

            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(86, 30)
            };
            nextButton = new Button
            {
                Text = Localization.T("Common.Next"),
                DialogResult = DialogResult.OK,
                Size = new Size(86, 30),
                Enabled = false
            };
            footer.Resize += (s, e) =>
            {
                cancelButton.Location = new Point(footer.Width - 102, 11);
                nextButton.Location = new Point(footer.Width - 196, 11);
            };
            footer.Controls.Add(cancelButton);
            footer.Controls.Add(nextButton);

            gridButton.Click += (s, e) => SetListMode(false);
            listButton.Click += (s, e) => SetListMode(true);
            nextButton.Click += (s, e) => DialogResult = DialogResult.OK;

            Controls.Add(titleLabel);
            Controls.Add(gridButton);
            Controls.Add(listButton);
            Controls.Add(searchBox);
            Controls.Add(recentLabel);
            Controls.Add(recentPanel);
            Controls.Add(allLabel);
            Controls.Add(allPanel);
            Controls.Add(footer);

            AcceptButton = nextButton;
            CancelButton = cancelButton;

            ThemeManager.ApplyTo(this);
            footer.BackColor = ThemeManager.SurfaceColor;
            ApplySearchPlaceholder();
            SetListMode(false);
            RenderCards();
        }

        private void ApplySearchPlaceholder()
        {
            if (!string.IsNullOrEmpty(searchBox.Text)) return;

            searchBox.Text = Localization.T("ConnectionWizard.Search");
            searchBox.ForeColor = ThemeManager.MutedTextColor;
        }

        private void SetListMode(bool value)
        {
            listMode = value;
            gridButton.BackColor = listMode ? ThemeManager.ButtonBackColor : ThemeManager.SelectionColor;
            listButton.BackColor = listMode ? ThemeManager.SelectionColor : ThemeManager.ButtonBackColor;
            RenderCards();
        }

        private void RenderCards()
        {
            recentPanel.Controls.Clear();
            allPanel.Controls.Clear();
            cards.Clear();

            string keyword = searchBox.Text == Localization.T("ConnectionWizard.Search")
                ? string.Empty
                : searchBox.Text.Trim();

            IEnumerable<ConnectionTypeOption> filtered = options.Where(option =>
                keyword.Length == 0 ||
                option.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

            ConnectionTypeOption mysql = options.First(option => option.Kind == MySql);
            if (keyword.Length == 0 || mysql.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                recentPanel.Controls.Add(CreateCard(mysql));
            }

            foreach (ConnectionTypeOption option in filtered)
            {
                allPanel.Controls.Add(CreateCard(option));
            }
        }

        private ConnectionTypeCard CreateCard(ConnectionTypeOption option)
        {
            ConnectionTypeCard card = new ConnectionTypeCard(option, listMode)
            {
                Selected = option.Kind == SelectedConnectionType
            };
            card.Click += (s, e) => SelectOption(option.Kind);
            card.DoubleClick += (s, e) =>
            {
                SelectOption(option.Kind);
                DialogResult = DialogResult.OK;
                Close();
            };
            cards.Add(card);
            return card;
        }

        private void SelectOption(string kind)
        {
            SelectedConnectionType = kind;
            nextButton.Enabled = true;
            foreach (ConnectionTypeCard card in cards)
            {
                card.Selected = card.Option.Kind == kind;
                card.Invalidate();
            }
        }

        private class ConnectionTypeOption
        {
            public string Kind { get; private set; }
            public string Name { get; private set; }
            public Color Color { get; private set; }
            public string IconPath { get; private set; }

            public ConnectionTypeOption(string kind, string name, Color color, string iconPath)
            {
                Kind = kind;
                Name = name;
                Color = color;
                IconPath = iconPath;
            }
        }

        private class ConnectionTypeCard : Control
        {
            public ConnectionTypeOption Option { get; private set; }
            public bool Selected { get; set; }

            private readonly bool listMode;
            private readonly Image iconImage;

            public ConnectionTypeCard(ConnectionTypeOption option, bool listMode)
            {
                Option = option;
                this.listMode = listMode;
                iconImage = LoadIcon(option.IconPath);
                Size = listMode ? new Size(540, 54) : new Size(126, 96);
                Margin = new Padding(4, 3, 16, 5);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                TabStop = true;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && iconImage != null)
                {
                    iconImage.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                Color backColor = Selected ? ThemeManager.SelectionColor : ThemeManager.ElevatedColor;
                Color borderColor = Selected ? ThemeManager.AccentColor : ThemeManager.BorderColor;
                using (GraphicsPath cardPath = RoundedRectangle(bounds, 8))
                using (SolidBrush brush = new SolidBrush(backColor))
                using (Pen pen = new Pen(borderColor, Selected ? 2 : 1))
                {
                    e.Graphics.FillPath(brush, cardPath);
                    e.Graphics.DrawPath(pen, cardPath);
                }

                Rectangle tileBounds = listMode
                    ? new Rectangle(12, 9, 36, 36)
                    : new Rectangle((Width - 54) / 2, 10, 54, 54);
                DrawIconTile(e.Graphics, tileBounds);

                Rectangle textBounds = listMode
                    ? new Rectangle(60, 0, Width - 72, Height)
                    : new Rectangle(6, 68, Width - 12, 22);
                TextRenderer.DrawText(
                    e.Graphics,
                    Option.Name,
                    Font,
                    textBounds,
                    ThemeManager.TextColor,
                    listMode ? TextFormatFlags.VerticalCenter | TextFormatFlags.Left : TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }

            private void DrawIconTile(Graphics graphics, Rectangle bounds)
            {
                if (iconImage != null)
                {
                    Rectangle imageBounds = InflateToFit(bounds, iconImage.Size, listMode ? 30 : 48);
                    graphics.DrawImage(iconImage, imageBounds);
                    return;
                }

                Rectangle shadow = new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height);
                using (GraphicsPath shadowPath = RoundedRectangle(shadow, 8))
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(28, Color.Black)))
                {
                    graphics.FillPath(shadowBrush, shadowPath);
                }

                using (GraphicsPath path = RoundedRectangle(bounds, 8))
                using (SolidBrush brush = new SolidBrush(Option.Color))
                {
                    graphics.FillPath(brush, path);
                }

                DrawFallbackIcon(graphics, bounds);
            }

            private static Image LoadIcon(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                try
                {
                    using (Image source = Image.FromFile(path))
                    {
                        return new Bitmap(source);
                    }
                }
                catch
                {
                    return null;
                }
            }

            private static Rectangle InflateToFit(Rectangle bounds, Size sourceSize, int maxSize)
            {
                float scale = Math.Min((float)maxSize / sourceSize.Width, (float)maxSize / sourceSize.Height);
                int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
                int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
                return new Rectangle(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2, width, height);
            }

            private void DrawFallbackIcon(Graphics graphics, Rectangle bounds)
            {
                using (Pen pen = new Pen(Color.White, Math.Max(2, bounds.Width / 12)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    Rectangle cylinder = new Rectangle(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 5, bounds.Width / 2, bounds.Height * 3 / 5);
                    graphics.DrawEllipse(pen, cylinder.Left, cylinder.Top, cylinder.Width, cylinder.Height / 3);
                    graphics.DrawLine(pen, cylinder.Left, cylinder.Top + cylinder.Height / 6, cylinder.Left, cylinder.Bottom - cylinder.Height / 6);
                    graphics.DrawLine(pen, cylinder.Right, cylinder.Top + cylinder.Height / 6, cylinder.Right, cylinder.Bottom - cylinder.Height / 6);
                    graphics.DrawArc(pen, cylinder.Left, cylinder.Bottom - cylinder.Height / 3, cylinder.Width, cylinder.Height / 3, 0, 180);
                    if (Option.Kind == Oracle)
                    {
                        graphics.DrawEllipse(pen, bounds.Left + bounds.Width / 5, bounds.Top + bounds.Height / 3, bounds.Width * 3 / 5, bounds.Height / 3);
                    }
                }
            }

            private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
            {
                int diameter = radius * 2;
                GraphicsPath path = new GraphicsPath();
                path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}

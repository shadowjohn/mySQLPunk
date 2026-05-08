using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            Size = new Size(640, 390);
            MinimumSize = new Size(560, 330);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            options = new List<ConnectionTypeOption>
            {
                new ConnectionTypeOption(MySql, "MySQL", Color.FromArgb(52, 205, 81)),
                new ConnectionTypeOption(PostgreSql, "PostgreSQL", Color.FromArgb(80, 145, 240)),
                new ConnectionTypeOption(SqlServer, "SQL Server", Color.FromArgb(255, 178, 27)),
                new ConnectionTypeOption(Oracle, "Oracle", Color.FromArgb(245, 0, 61)),
                new ConnectionTypeOption(Sqlite, "SQLite", Color.FromArgb(87, 207, 199))
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
                Size = new Size(590, 92),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                WrapContents = true
            };

            Label allLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.All"),
                AutoSize = true,
                Location = new Point(14, 188)
            };

            allPanel = new FlowLayoutPanel
            {
                Location = new Point(14, 214),
                Size = new Size(590, 74),
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

            public ConnectionTypeOption(string kind, string name, Color color)
            {
                Kind = kind;
                Name = name;
                Color = color;
            }
        }

        private class ConnectionTypeCard : Control
        {
            public ConnectionTypeOption Option { get; private set; }
            public bool Selected { get; set; }

            private readonly bool listMode;

            public ConnectionTypeCard(ConnectionTypeOption option, bool listMode)
            {
                Option = option;
                this.listMode = listMode;
                Size = listMode ? new Size(540, 48) : new Size(118, 88);
                Margin = new Padding(4, 3, 18, 3);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                TabStop = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                Color backColor = Selected ? ThemeManager.SelectionColor : ThemeManager.WindowBackColor;
                Color borderColor = Selected ? ThemeManager.AccentColor : ThemeManager.WindowBackColor;
                using (SolidBrush brush = new SolidBrush(backColor))
                using (Pen pen = new Pen(borderColor, Selected ? 2 : 1))
                {
                    e.Graphics.FillRectangle(brush, bounds);
                    e.Graphics.DrawRectangle(pen, bounds);
                }

                Rectangle iconBounds = listMode
                    ? new Rectangle(12, 8, 32, 32)
                    : new Rectangle((Width - 50) / 2, 10, 50, 50);
                DrawDatabaseIcon(e.Graphics, iconBounds, Option);

                Rectangle textBounds = listMode
                    ? new Rectangle(56, 0, Width - 64, Height)
                    : new Rectangle(4, 62, Width - 8, 22);
                TextRenderer.DrawText(
                    e.Graphics,
                    Option.Name,
                    Font,
                    textBounds,
                    ThemeManager.TextColor,
                    listMode ? TextFormatFlags.VerticalCenter | TextFormatFlags.Left : TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }

            private static void DrawDatabaseIcon(Graphics graphics, Rectangle bounds, ConnectionTypeOption option)
            {
                using (GraphicsPath path = RoundedRectangle(bounds, 5))
                using (SolidBrush brush = new SolidBrush(option.Color))
                {
                    graphics.FillPath(brush, path);
                }

                using (Pen whitePen = new Pen(Color.White, Math.Max(2, bounds.Width / 13)))
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                {
                    whitePen.StartCap = LineCap.Round;
                    whitePen.EndCap = LineCap.Round;

                    if (option.Kind == MySql)
                    {
                        Point[] curve =
                        {
                            new Point(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 4),
                            new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 3),
                            new Point(bounds.Left + bounds.Width * 3 / 4, bounds.Top + bounds.Height * 2 / 3),
                            new Point(bounds.Left + bounds.Width * 2 / 3, bounds.Top + bounds.Height * 3 / 4)
                        };
                        graphics.DrawCurve(whitePen, curve);
                        graphics.FillEllipse(whiteBrush, bounds.Left + bounds.Width / 5, bounds.Top + bounds.Height / 6, bounds.Width / 3, bounds.Height / 2);
                    }
                    else if (option.Kind == PostgreSql)
                    {
                        graphics.DrawEllipse(whitePen, bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 5, bounds.Width / 2, bounds.Height / 2);
                        graphics.DrawLine(whitePen, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height * 4 / 5);
                        graphics.DrawArc(whitePen, bounds.Left + bounds.Width / 5, bounds.Top + bounds.Height / 2, bounds.Width * 3 / 5, bounds.Height / 3, 15, 150);
                    }
                    else if (option.Kind == SqlServer)
                    {
                        Rectangle cylinder = new Rectangle(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 5, bounds.Width / 2, bounds.Height * 3 / 5);
                        graphics.DrawEllipse(whitePen, cylinder.Left, cylinder.Top, cylinder.Width, cylinder.Height / 3);
                        graphics.DrawLine(whitePen, cylinder.Left, cylinder.Top + cylinder.Height / 6, cylinder.Left, cylinder.Bottom - cylinder.Height / 6);
                        graphics.DrawLine(whitePen, cylinder.Right, cylinder.Top + cylinder.Height / 6, cylinder.Right, cylinder.Bottom - cylinder.Height / 6);
                        graphics.DrawArc(whitePen, cylinder.Left, cylinder.Bottom - cylinder.Height / 3, cylinder.Width, cylinder.Height / 3, 0, 180);
                    }
                    else if (option.Kind == Oracle)
                    {
                        graphics.DrawEllipse(whitePen, bounds.Left + bounds.Width / 5, bounds.Top + bounds.Height / 3, bounds.Width * 3 / 5, bounds.Height / 3);
                    }
                    else
                    {
                        graphics.DrawArc(whitePen, bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 5, bounds.Width / 2, bounds.Height * 3 / 5, 90, 210);
                        graphics.DrawLine(whitePen, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 3, bounds.Left + bounds.Width / 4, bounds.Bottom - bounds.Height / 5);
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

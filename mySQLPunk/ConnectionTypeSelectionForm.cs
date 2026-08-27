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
        public const string MongoDb = "mongodb";
        public const string Redis = "redis";
        public const string Snowflake = "snowflake";

        private readonly TextBox searchBox;
        private readonly Button gridButton;
        private readonly Button listButton;
        private readonly Button nextButton;
        private readonly FlowLayoutPanel recentPanel;
        private readonly FlowLayoutPanel allPanel;
        private readonly List<string> recentConnectionTypes = new List<string>();
        private readonly List<ConnectionTypeOption> options;
        private readonly List<ConnectionTypeCard> cards = new List<ConnectionTypeCard>();
        private readonly Panel selectionPage;
        private readonly Panel connectionFormPage;
        private bool listMode;
        private Form embeddedConnectionForm;

        public string SelectedConnectionType { get; private set; }
        public Func<string, Form> CreateConnectionForm { get; set; }
        public Func<Dictionary<string, object>, Form> CreateConnectionFormFromDraft { get; set; }

        public ConnectionTypeSelectionForm()
            : this(null)
        {
        }

        public ConnectionTypeSelectionForm(IEnumerable<string> recentTypes)
        {
            Text = Localization.T("ConnectionWizard.Title");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 620);
            MinimumSize = new Size(760, 520);
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
                new ConnectionTypeOption(Sqlite, "SQLite", Color.FromArgb(0, 109, 165), Path.Combine(imagePath, "brand_sqlite.png")),
                new ConnectionTypeOption(MongoDb, "MongoDB", Color.FromArgb(0, 168, 89), Path.Combine(imagePath, "brand_mongodb.png")),
                new ConnectionTypeOption(Redis, "Redis / Garnet", Color.FromArgb(220, 56, 45), Path.Combine(imagePath, "brand_redis.png")),
                new ConnectionTypeOption(Snowflake, "Snowflake", Color.FromArgb(41, 181, 232), Path.Combine(imagePath, "brand_snowflake.png"))
            };

            recentConnectionTypes.AddRange(BuildRecentConnectionTypes(recentTypes));

            selectionPage = new Panel
            {
                Dock = DockStyle.Fill
            };
            connectionFormPage = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            // ── 標題列 ────────────────────────────────────────────────
            // 原本標題、檢視切換與搜尋框都用絕對座標 + Anchor 排版，
            // 面板實際尺寸算出來之後這些控制項會被推出可視範圍，改成停駐排版。
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                Padding = new Padding(UiMetrics.Space5, UiMetrics.Space4, UiMetrics.Space5, UiMetrics.Space2)
            };

            Label titleLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.SelectType"),
                Dock = DockStyle.Fill,
                Font = UiKit.Title,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // 原本用 ▦ / ☰ 這種字元當圖示，字型不同就會變成方框或被截斷，改用向量圖示
            gridButton = new Button
            {
                Size = new Size(34, UiMetrics.ControlHeight),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(UiMetrics.Space1, 0, 0, 0)
            };
            listButton = new Button
            {
                Size = new Size(34, UiMetrics.ControlHeight),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(UiMetrics.Space1, 0, 0, 0)
            };

            searchBox = new TextBox
            {
                Width = 190,
                Margin = new Padding(UiMetrics.Space3, UiMetrics.Space1, 0, 0)
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

            FlowLayoutPanel headerTools = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = false,
                Width = 290
            };
            headerTools.Controls.Add(searchBox);
            headerTools.Controls.Add(listButton);
            headerTools.Controls.Add(gridButton);

            header.Controls.Add(titleLabel);
            header.Controls.Add(headerTools);

            // ── 內容區 ────────────────────────────────────────────────
            Panel body = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space5, 0, UiMetrics.Space5, UiMetrics.Space2)
            };

            Label recentLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.Recent"),
                Dock = DockStyle.Top,
                Height = 26,
                Font = UiKit.BodyBold,
                TextAlign = ContentAlignment.MiddleLeft
            };

            recentPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 124,
                WrapContents = true
            };

            Panel sectionGap = new Panel
            {
                Dock = DockStyle.Top,
                Height = UiMetrics.Space4
            };

            Label allLabel = new Label
            {
                Text = Localization.T("ConnectionWizard.All"),
                Dock = DockStyle.Top,
                Height = 26,
                Font = UiKit.BodyBold,
                TextAlign = ContentAlignment.MiddleLeft
            };

            allPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true
            };

            // 停駐排版是反著堆的：Fill 先加，Top 由下往上加
            body.Controls.Add(allPanel);
            body.Controls.Add(allLabel);
            body.Controls.Add(sectionGap);
            body.Controls.Add(recentPanel);
            body.Controls.Add(recentLabel);

            // ── 動作列 ────────────────────────────────────────────────
            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                Padding = new Padding(UiMetrics.Space5, UiMetrics.Space3, UiMetrics.Space5, UiMetrics.Space3)
            };
            footer.Paint += (s, e) => UiKit.DrawHairline(e.Graphics, 0, footer.Width, 0, ThemeManager.BorderColor);

            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 0, 0, 0)
            };
            nextButton = new Button
            {
                Text = Localization.T("Common.Next"),
                Size = new Size(96, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 0, 0, 0),
                Enabled = false
            };
            Button importUriButton = new Button
            {
                Text = Localization.T("ConnectionWizard.ImportUri"),
                AutoSize = true,
                MinimumSize = new Size(124, UiMetrics.ControlHeight),
                Dock = DockStyle.Left
            };

            FlowLayoutPanel footerButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            footerButtons.Controls.Add(cancelButton);
            footerButtons.Controls.Add(nextButton);
            footer.Controls.Add(footerButtons);
            footer.Controls.Add(importUriButton);

            gridButton.Click += (s, e) => SetListMode(false);
            listButton.Click += (s, e) => SetListMode(true);
            nextButton.Click += (s, e) => ShowSelectedConnectionForm();
            importUriButton.Click += (s, e) => ImportConnectionUri();

            selectionPage.Controls.Add(body);
            selectionPage.Controls.Add(footer);
            selectionPage.Controls.Add(header);
            Controls.Add(connectionFormPage);
            Controls.Add(selectionPage);

            AcceptButton = nextButton;
            CancelButton = cancelButton;

            ThemeManager.ApplyTo(this);
            ThemeManager.MarkAsPrimary(nextButton);
            ThemeManager.SetGlyph(gridButton, UiGlyph.Columns);
            ThemeManager.SetGlyph(listButton, UiGlyph.Filter);

            ToolTip viewModeTip = new ToolTip();
            viewModeTip.SetToolTip(gridButton, Localization.T("ConnectionWizard.GridView"));
            viewModeTip.SetToolTip(listButton, Localization.T("ConnectionWizard.ListView"));
            header.BackColor = ThemeManager.WindowBackColor;
            body.BackColor = ThemeManager.WindowBackColor;
            footer.BackColor = ThemeManager.SurfaceColor;
            footerButtons.BackColor = ThemeManager.SurfaceColor;
            headerTools.BackColor = ThemeManager.WindowBackColor;
            titleLabel.ForeColor = ThemeManager.TextColor;
            recentLabel.ForeColor = ThemeManager.MutedTextColor;
            allLabel.ForeColor = ThemeManager.MutedTextColor;
            ApplySearchPlaceholder();
            SetListMode(false);
            RenderCards();
        }

        public static void RememberRecentConnectionType(string kind)
        {
            kind = NormalizeKind(kind);
            if (string.IsNullOrEmpty(kind)) return;

            List<string> values = LoadRecordedRecentConnectionTypes();
            values.RemoveAll(value => string.Equals(value, kind, StringComparison.OrdinalIgnoreCase));
            values.Insert(0, kind);
            if (values.Count > 5) values.RemoveRange(5, values.Count - 5);
            SaveRecordedRecentConnectionTypes(values);
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
            ThemeManager.SetToggled(gridButton, !listMode);
            ThemeManager.SetToggled(listButton, listMode);
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

            List<ConnectionTypeOption> recentOptions = recentConnectionTypes
                .Select(kind => options.FirstOrDefault(option => string.Equals(option.Kind, kind, StringComparison.OrdinalIgnoreCase)))
                .Where(option => option != null)
                .Where(option => keyword.Length == 0 || option.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (recentOptions.Count == 0)
            {
                recentPanel.Controls.Add(new Label
                {
                    Text = Localization.T("ConnectionWizard.NoRecent"),
                    AutoSize = true,
                    ForeColor = ThemeManager.MutedTextColor,
                    Margin = new Padding(4, 18, 4, 4)
                });
            }
            else
            {
                foreach (ConnectionTypeOption option in recentOptions)
                {
                    recentPanel.Controls.Add(CreateCard(option));
                }
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
                ShowSelectedConnectionForm();
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

        private void ShowSelectedConnectionForm()
        {
            if (string.IsNullOrWhiteSpace(SelectedConnectionType)) return;

            if (CreateConnectionForm == null)
            {
                DialogResult = DialogResult.OK;
                return;
            }

            Form form = CreateConnectionForm(SelectedConnectionType);
            if (form == null) return;

            ShowConnectionForm(form);
        }

        private void ImportConnectionUri()
        {
            using (ConnectionUriImportForm dialog = new ConnectionUriImportForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ConnectionDraft == null) return;

                object kindValue;
                string kind = dialog.ConnectionDraft.TryGetValue("db_kind", out kindValue) && kindValue != null
                    ? NormalizeKind(kindValue.ToString())
                    : string.Empty;
                if (string.IsNullOrEmpty(kind)) return;

                SelectedConnectionType = kind;
                if (CreateConnectionFormFromDraft == null)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                Form form = CreateConnectionFormFromDraft(dialog.ConnectionDraft);
                if (form != null) ShowConnectionForm(form);
            }
        }

        private void ShowConnectionForm(Form form)
        {
            if (form == null) return;

            embeddedConnectionForm = form;
            Text = form.Text;

            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.Manual;
            Size preferred = form.AutoSize ? form.PreferredSize : form.Size;
            if (preferred.Width < 200 || preferred.Height < 150) preferred = form.Size;
            form.AutoSize = false;
            form.Dock = DockStyle.Fill;

            selectionPage.Visible = false;
            connectionFormPage.Controls.Clear();
            connectionFormPage.Visible = true;
            connectionFormPage.BringToFront();

            // 換頁時把視窗縮放成編輯頁的大小、維持中心點不動，看起來才像同一個精靈換頁，
            // 而不是關掉一個視窗又冒出另一個
            Point center = new Point(Left + Width / 2, Top + Height / 2);
            Size target = new Size(
                preferred.Width + Width - ClientSize.Width,
                preferred.Height + Height - ClientSize.Height);
            MinimumSize = Size.Empty;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Size = target;
            Location = new Point(Math.Max(0, center.X - Width / 2), Math.Max(0, center.Y - Height / 2));

            form.FormClosed += EmbeddedConnectionForm_FormClosed;
            connectionFormPage.Controls.Add(form);
            form.Show();
            ActiveControl = form;
            AcceptButton = form.AcceptButton;
            CancelButton = form.CancelButton;
        }

        private void EmbeddedConnectionForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            embeddedConnectionForm = null;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (embeddedConnectionForm != null && !embeddedConnectionForm.IsDisposed)
            {
                embeddedConnectionForm.FormClosed -= EmbeddedConnectionForm_FormClosed;
                embeddedConnectionForm.Close();
            }
            base.OnFormClosed(e);
        }

        private List<string> BuildRecentConnectionTypes(IEnumerable<string> recentTypes)
        {
            List<string> values = new List<string>();
            AddRecentTypes(values, LoadRecordedRecentConnectionTypes());
            AddRecentTypes(values, recentTypes);
            return values;
        }

        private static void AddRecentTypes(List<string> target, IEnumerable<string> source)
        {
            if (source == null) return;

            foreach (string raw in source)
            {
                if (target.Count >= 5) return;
                string kind = NormalizeKind(raw);
                if (string.IsNullOrEmpty(kind)) continue;
                if (target.Any(value => string.Equals(value, kind, StringComparison.OrdinalIgnoreCase))) continue;
                target.Add(kind);
            }
        }

        private static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return string.Empty;

            switch (kind.Trim().ToLowerInvariant())
            {
                case "mysql":
                    return MySql;
                case "postgresql":
                case "postgres":
                    return PostgreSql;
                case "sqlserver":
                case "mssql":
                case "sql server":
                    return SqlServer;
                case "oracle":
                    return Oracle;
                case "sqlite":
                    return Sqlite;
                case "mongodb":
                case "mongo":
                    return MongoDb;
                case "redis":
                case "garnet":
                    return Redis;
                case "snowflake":
                    return Snowflake;
                default:
                    return string.Empty;
            }
        }

        private static List<string> LoadRecordedRecentConnectionTypes()
        {
            try
            {
                string path = GetRecentConnectionTypesPath();
                if (!File.Exists(path)) return new List<string>();
                return File.ReadAllLines(path)
                    .Select(NormalizeKind)
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void SaveRecordedRecentConnectionTypes(List<string> values)
        {
            try
            {
                string path = GetRecentConnectionTypesPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, values.Select(NormalizeKind).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray());
            }
            catch
            {
            }
        }

        private static string GetRecentConnectionTypesPath()
        {
            return Path.Combine(Application.UserAppDataPath, "recent-connection-types.txt");
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
            private bool hovered;

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

                // 原本卡片完全沒有停留回饋，滑過去不知道自己指到哪一張
                MouseEnter += (s, e) => { hovered = true; Invalidate(); };
                MouseLeave += (s, e) => { hovered = false; Invalidate(); };
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
                Color backColor = Selected
                    ? ThemeManager.AccentSoftColor
                    : (hovered ? ThemeManager.HoverColor : ThemeManager.ElevatedColor);
                Color borderColor = Selected
                    ? ThemeManager.AccentColor
                    : (hovered ? ThemeManager.BorderStrongColor : ThemeManager.BorderColor);
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

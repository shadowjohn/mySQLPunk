using System;
using System.Drawing;
using System.Windows.Forms;

namespace mySQLPunk
{
    public class OptionsForm : Form
    {
        private readonly ListBox navigationList;
        private readonly Panel contentPanel;
        private readonly RadioButton lightThemeRadio;
        private readonly RadioButton darkThemeRadio;
        private readonly ComboBox languageCombo;
        private readonly ThemePreviewControl lightPreview;
        private readonly ThemePreviewControl darkPreview;

        public string SelectedLanguage { get; private set; }
        public string SelectedTheme { get; private set; }

        public OptionsForm()
        {
            SelectedLanguage = Localization.CurrentLanguage;
            SelectedTheme = ThemeManager.CurrentTheme;

            Text = Localization.T("Options.Title");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 610);
            MinimumSize = new Size(760, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            navigationList = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 160,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            navigationList.Items.AddRange(new object[]
            {
                Localization.T("Options.General"),
                Localization.T("Options.Navigation"),
                Localization.T("Options.AutoComplete"),
                Localization.T("Options.Editor"),
                Localization.T("Options.Record"),
                Localization.T("Options.AI"),
                Localization.T("Options.AutoRecovery"),
                Localization.T("Options.FileLocation"),
                Localization.T("Options.Connection"),
                Localization.T("Options.Environment"),
                Localization.T("Options.Advanced")
            });
            navigationList.SelectedIndex = 0;

            contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18)
            };

            Panel buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                Padding = new Padding(12, 8, 12, 8)
            };
            Button okButton = new Button
            {
                Text = Localization.T("Common.OK"),
                DialogResult = DialogResult.OK,
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            okButton.Location = new Point(buttonPanel.Width - 184, 12);
            cancelButton.Location = new Point(buttonPanel.Width - 94, 12);
            buttonPanel.Resize += (s, e) =>
            {
                okButton.Location = new Point(buttonPanel.Width - 184, 12);
                cancelButton.Location = new Point(buttonPanel.Width - 94, 12);
            };
            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);

            Label sectionTitle = new Label
            {
                Text = Localization.T("Options.General"),
                AutoSize = true,
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                Location = new Point(18, 18)
            };

            Label themeLabel = new Label
            {
                Text = Localization.T("Options.ThemeLabel"),
                AutoSize = true,
                Location = new Point(18, 70)
            };

            lightPreview = new ThemePreviewControl(ThemeManager.Light)
            {
                Location = new Point(105, 58),
                Size = new Size(162, 102)
            };
            darkPreview = new ThemePreviewControl(ThemeManager.Dark)
            {
                Location = new Point(300, 58),
                Size = new Size(162, 102)
            };

            lightThemeRadio = new RadioButton
            {
                Text = Localization.T("Options.Light"),
                AutoSize = true,
                Location = new Point(135, 166)
            };
            darkThemeRadio = new RadioButton
            {
                Text = Localization.T("Options.Dark"),
                AutoSize = true,
                Location = new Point(330, 166)
            };
            lightThemeRadio.Checked = SelectedTheme != ThemeManager.Dark;
            darkThemeRadio.Checked = SelectedTheme == ThemeManager.Dark;

            Label languageLabel = new Label
            {
                Text = Localization.T("Options.LanguageLabel"),
                AutoSize = true,
                Location = new Point(18, 215)
            };

            languageCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(105, 210),
                Width = 250
            };
            languageCombo.Items.Add(new LanguageItem(Localization.T("Menu.LanguageZh"), Localization.TraditionalChinese));
            languageCombo.Items.Add(new LanguageItem(Localization.T("Menu.LanguageEn"), Localization.English));
            languageCombo.SelectedIndex = SelectedLanguage == Localization.English ? 1 : 0;

            Label noteLabel = new Label
            {
                Text = Localization.T("Options.RestartNote"),
                AutoSize = true,
                Location = new Point(18, 260),
                MaximumSize = new Size(600, 0)
            };

            lightThemeRadio.CheckedChanged += (s, e) => UpdateSelection();
            darkThemeRadio.CheckedChanged += (s, e) => UpdateSelection();
            lightPreview.Click += (s, e) => lightThemeRadio.Checked = true;
            darkPreview.Click += (s, e) => darkThemeRadio.Checked = true;
            languageCombo.SelectedIndexChanged += (s, e) => UpdateSelection();
            okButton.Click += (s, e) => UpdateSelection();

            contentPanel.Controls.Add(sectionTitle);
            contentPanel.Controls.Add(themeLabel);
            contentPanel.Controls.Add(lightPreview);
            contentPanel.Controls.Add(darkPreview);
            contentPanel.Controls.Add(lightThemeRadio);
            contentPanel.Controls.Add(darkThemeRadio);
            contentPanel.Controls.Add(languageLabel);
            contentPanel.Controls.Add(languageCombo);
            contentPanel.Controls.Add(noteLabel);

            Controls.Add(contentPanel);
            Controls.Add(navigationList);
            Controls.Add(buttonPanel);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            ThemeManager.ApplyTo(this);
            navigationList.BackColor = ThemeManager.ElevatedColor;
            navigationList.ForeColor = ThemeManager.TextColor;
            contentPanel.BackColor = ThemeManager.WindowBackColor;
            buttonPanel.BackColor = ThemeManager.SurfaceColor;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            SelectedTheme = darkThemeRadio.Checked ? ThemeManager.Dark : ThemeManager.Light;
            LanguageItem item = languageCombo.SelectedItem as LanguageItem;
            SelectedLanguage = item == null ? Localization.TraditionalChinese : item.Value;
            lightPreview.Selected = SelectedTheme == ThemeManager.Light;
            darkPreview.Selected = SelectedTheme == ThemeManager.Dark;
            lightPreview.Invalidate();
            darkPreview.Invalidate();
        }

        private class LanguageItem
        {
            public string Text { get; private set; }
            public string Value { get; private set; }

            public LanguageItem(string text, string value)
            {
                Text = text;
                Value = value;
            }

            public override string ToString()
            {
                return Text;
            }
        }

        private class ThemePreviewControl : Control
        {
            private readonly string previewTheme;

            public bool Selected { get; set; }

            public ThemePreviewControl(string theme)
            {
                previewTheme = theme;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                bool dark = previewTheme == ThemeManager.Dark;
                Color window = dark ? Color.FromArgb(30, 34, 38) : Color.White;
                Color surface = dark ? Color.FromArgb(38, 43, 48) : Color.FromArgb(245, 245, 245);
                Color elevated = dark ? Color.FromArgb(45, 51, 57) : Color.White;
                Color text = dark ? Color.FromArgb(235, 240, 244) : Color.FromArgb(51, 51, 51);
                Color muted = dark ? Color.FromArgb(170, 181, 189) : Color.FromArgb(105, 105, 105);
                Color accent = dark ? Color.FromArgb(80, 170, 220) : Color.FromArgb(0, 120, 212);
                Color grid = dark ? Color.FromArgb(58, 65, 72) : Color.FromArgb(220, 228, 232);

                Rectangle outer = new Rectangle(0, 0, Width - 1, Height - 1);
                using (SolidBrush brush = new SolidBrush(window))
                using (Pen border = new Pen(Selected ? accent : grid, Selected ? 3 : 1))
                {
                    e.Graphics.FillRectangle(brush, outer);
                    e.Graphics.DrawRectangle(border, outer);
                }

                using (SolidBrush brush = new SolidBrush(surface))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(1, 1, Width - 2, 18));
                    e.Graphics.FillRectangle(brush, new Rectangle(1, 19, 48, Height - 20));
                }

                using (SolidBrush brush = new SolidBrush(elevated))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(54, 25, Width - 62, Height - 34));
                }

                DrawCircle(e.Graphics, 12, 10, Color.FromArgb(70, 170, 90));
                DrawCircle(e.Graphics, 32, 10, Color.FromArgb(50, 150, 210));
                DrawCircle(e.Graphics, 52, 10, Color.FromArgb(240, 170, 60));

                using (Pen pen = new Pen(accent, 2))
                {
                    e.Graphics.DrawLine(pen, 65, 10, 78, 10);
                    e.Graphics.DrawLine(pen, 88, 10, 101, 10);
                    e.Graphics.DrawLine(pen, 111, 10, 124, 10);
                }

                using (SolidBrush brush = new SolidBrush(text))
                using (Font font = new Font("Segoe UI", 5.5f))
                {
                    e.Graphics.DrawString("mySQLPunk", font, brush, 6, 27);
                    e.Graphics.DrawString("Tables", font, brush, 10, 45);
                    e.Graphics.DrawString("Views", font, brush, 10, 60);
                }

                using (Pen pen = new Pen(grid, 1))
                {
                    for (int x = 62; x < Width - 10; x += 18)
                    {
                        e.Graphics.DrawLine(pen, x, 30, x, Height - 14);
                    }
                    for (int y = 36; y < Height - 12; y += 14)
                    {
                        e.Graphics.DrawLine(pen, 58, y, Width - 9, y);
                    }
                }

                using (SolidBrush brush = new SolidBrush(accent))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(67, 58, 6, 20));
                    e.Graphics.FillRectangle(brush, new Rectangle(82, 47, 6, 31));
                    e.Graphics.FillRectangle(brush, new Rectangle(97, 54, 6, 24));
                    e.Graphics.FillRectangle(brush, new Rectangle(112, 41, 6, 37));
                }

                using (SolidBrush brush = new SolidBrush(muted))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(128, 50, 6, 28));
                }
            }

            private static void DrawCircle(Graphics graphics, int x, int y, Color color)
            {
                using (SolidBrush brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, x - 4, y - 4, 8, 8);
                }
            }
        }
    }
}

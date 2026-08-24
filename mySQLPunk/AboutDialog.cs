using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    public sealed class AboutDialog : Form
    {
        public const string AvatarAnimationFileName = "mySQLPunk_avatar_wink.gif";

        private Stream _avatarStream;

        public AboutDialog(string productVersion)
        {
            Text = Localization.T("Menu.About");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 350);
            BackColor = ThemeManager.WindowBackColor;
            ForeColor = ThemeManager.TextColor;
            Font = UiKit.Body;

            PictureBox avatar = CreateAvatarBox(BuildAvatarAnimationPath(AppDomain.CurrentDomain.BaseDirectory));
            Label title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 34,
                Text = Localization.T("About.MascotTitle"),
                Font = UiKit.Display,
                ForeColor = ThemeManager.TextColor
            };
            Label subtitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 34,
                Text = Localization.T("About.MascotSubtitle"),
                Font = UiKit.Caption,
                ForeColor = ThemeManager.MutedTextColor
            };
            Label body = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = Form1.BuildAboutMessage(productVersion),
                Font = UiKit.Body,
                ForeColor = ThemeManager.TextColor
            };
            Button okButton = new Button
            {
                Text = Localization.T("Common.OK"),
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Width = 96,
                Height = 30
            };

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(18),
                BackColor = ThemeManager.WindowBackColor
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            Panel avatarPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.WindowBackColor,
                Padding = new Padding(0, 8, 20, 8)
            };
            avatarPanel.Controls.Add(avatar);

            Panel textPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.WindowBackColor
            };
            textPanel.Controls.Add(body);
            textPanel.Controls.Add(subtitle);
            textPanel.Controls.Add(title);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = ThemeManager.WindowBackColor,
                Padding = new Padding(0, 12, 6, 0)
            };
            okButton.Margin = Padding.Empty;
            buttons.Controls.Add(okButton);

            root.Controls.Add(avatarPanel, 0, 0);
            root.Controls.Add(textPanel, 1, 0);
            root.Controls.Add(buttons, 0, 1);
            root.SetColumnSpan(buttons, 2);

            Controls.Add(root);
            AcceptButton = okButton;
            CancelButton = okButton;

            ThemeManager.ApplyTo(this);
        }

        public static string BuildAvatarAnimationPath(string baseDirectory)
        {
            return Path.Combine(baseDirectory ?? string.Empty, "image", AvatarAnimationFileName);
        }

        private PictureBox CreateAvatarBox(string avatarPath)
        {
            PictureBox avatar = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeManager.WindowBackColor,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            if (File.Exists(avatarPath))
            {
                byte[] bytes = File.ReadAllBytes(avatarPath);
                _avatarStream = new MemoryStream(bytes);
                avatar.Image = Image.FromStream(_avatarStream);
            }
            else
            {
                avatar.Paint += (sender, e) =>
                {
                    Rectangle bounds = Rectangle.Inflate(avatar.ClientRectangle, -24, -24);
                    UiKit.DrawGlyph(e.Graphics, UiGlyph.Database, bounds, ThemeManager.AccentColor, 2f);
                };
            }

            return avatar;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control control in Controls)
                {
                    DisposeImages(control);
                }
                if (_avatarStream != null)
                {
                    _avatarStream.Dispose();
                    _avatarStream = null;
                }
            }

            base.Dispose(disposing);
        }

        private static void DisposeImages(Control root)
        {
            PictureBox pictureBox = root as PictureBox;
            if (pictureBox != null && pictureBox.Image != null)
            {
                Image image = pictureBox.Image;
                pictureBox.Image = null;
                image.Dispose();
            }

            foreach (Control child in root.Controls)
            {
                DisposeImages(child);
            }
        }
    }
}

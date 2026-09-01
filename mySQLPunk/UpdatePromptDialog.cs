using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 發現新版本時的更新提示：版本資訊＋更新內容摘要，
    /// 「立即更新」走既有的下載／SHA-256 校驗／啟動安裝流程。
    /// 啟動時的自動檢查與手動檢查更新都用這一個。
    /// </summary>
    public sealed class UpdatePromptDialog : Form
    {
        public UpdatePromptDialog(AppUpdateCheckResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            Text = Localization.T("Update.PromptTitle");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 420);
            Padding = new Padding(20, 16, 20, 16);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel header = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };
            PictureBox glyph = new PictureBox
            {
                Size = new Size(24, 24),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = UiKit.RenderGlyph(UiGlyph.Refresh, 24, ThemeManager.AccentColor),
                Margin = new Padding(0, 0, 8, 0)
            };
            Label title = new Label
            {
                AutoSize = true,
                Text = string.IsNullOrWhiteSpace(result.ReleaseName)
                    ? Localization.T("Update.PromptTitle")
                    : result.ReleaseName,
                Font = UiKit.Subtitle,
                Margin = new Padding(0, 3, 0, 0)
            };
            header.Controls.Add(glyph);
            header.Controls.Add(title);

            Label versions = new Label
            {
                AutoSize = true,
                Text = Localization.Format("Update.PromptVersions", result.LatestVersion, result.CurrentVersion),
                ForeColor = ThemeManager.MutedTextColor,
                Margin = new Padding(0, 0, 0, 10)
            };

            RichTextBox notes = new RichTextBox
            {
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12),
                TabStop = false,
                DetectUrls = false,
                WordWrap = true
            };

            TableLayoutPanel footer = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Button releasePageButton = new Button
            {
                Text = Localization.T("Update.ViewReleasePage"),
                AutoSize = true,
                MinimumSize = new Size(UiMetrics.ButtonMinWidth, UiMetrics.ControlHeight),
                Padding = new Padding(10, 2, 10, 2),
                Margin = new Padding(0),
                Enabled = !string.IsNullOrWhiteSpace(result.ReleasePageUrl)
            };
            releasePageButton.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true }); }
                catch { }
            };

            Button updateButton = new Button
            {
                Text = Localization.T("Update.UpdateNow"),
                DialogResult = DialogResult.OK,
                AutoSize = true,
                MinimumSize = new Size(UiMetrics.ButtonMinWidth, UiMetrics.ControlHeight),
                Padding = new Padding(10, 2, 10, 2),
                Margin = new Padding(8, 0, 0, 0)
            };
            Button laterButton = new Button
            {
                Text = Localization.T("Update.Later"),
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(UiMetrics.ButtonMinWidth, UiMetrics.ControlHeight),
                Padding = new Padding(10, 2, 10, 2),
                Margin = new Padding(8, 0, 0, 0)
            };

            footer.Controls.Add(releasePageButton, 0, 0);
            footer.Controls.Add(updateButton, 2, 0);
            footer.Controls.Add(laterButton, 3, 0);

            AcceptButton = updateButton;
            CancelButton = laterButton;

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(versions, 0, 1);
            root.Controls.Add(notes, 0, 2);
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);

            ThemeManager.ApplyTo(this);
            // ThemeManager.ApplyTo 設定 ForeColor 會覆寫 RichTextBox 全部文字的顏色，
            // 所以更新內容必須在套完主題之後才渲染。
            RenderNotes(notes, result.ReleaseNotes);
        }

        /// <summary>
        /// 把 GitHub Release 的 Markdown 摘要轉成可讀的排版：
        /// 標題粗體＋強調色、`- ` 清單縮排成「•」、**粗體** 生效、去除反引號與連結語法。
        /// 只處理發版腳本會產生的子集，不是完整 Markdown 解析。
        /// </summary>
        private static void RenderNotes(RichTextBox box, string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                box.Text = Localization.T("Update.PromptNoNotes");
                return;
            }

            Color headingColor = ThemeManager.AccentColor;
            string[] lines = notes.Replace("\r\n", "\n").Split('\n');
            bool previousBlank = true;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0)
                {
                    if (!previousBlank) box.AppendText(Environment.NewLine);
                    previousBlank = true;
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    string heading = trimmed.TrimStart('#').Trim();
                    if (!previousBlank) box.AppendText(Environment.NewLine);
                    box.SelectionIndent = 0;
                    box.SelectionHangingIndent = 0;
                    box.SelectionFont = UiKit.Subtitle;
                    box.SelectionColor = headingColor;
                    box.AppendText(StripInlineMarkup(heading) + Environment.NewLine);
                }
                else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    box.SelectionIndent = 6;
                    box.SelectionHangingIndent = 14;
                    AppendInline(box, "• " + trimmed.Substring(2).Trim());
                    box.AppendText(Environment.NewLine);
                }
                else
                {
                    box.SelectionIndent = 0;
                    box.SelectionHangingIndent = 0;
                    AppendInline(box, trimmed);
                    box.AppendText(Environment.NewLine);
                }
                previousBlank = false;
            }

            box.SelectionStart = 0;
            box.SelectionLength = 0;
            box.ScrollToCaret();
        }

        /// <summary>依 **…** 交替切換粗體，其餘 Markdown 記號先剝掉再輸出。</summary>
        private static void AppendInline(RichTextBox box, string text)
        {
            string[] segments = text.Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0) continue;
                box.SelectionFont = (i % 2 == 1) ? UiKit.BodyBold : UiKit.Body;
                box.SelectionColor = ThemeManager.TextColor;
                box.AppendText(StripInlineMarkup(segments[i]));
            }
        }

        private static string StripInlineMarkup(string text)
        {
            text = text.Replace("**", "").Replace("`", "");
            // [文字](網址) → 文字
            return System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        }
    }
}

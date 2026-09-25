using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 同步 SQL 的唯讀預覽。只提供複製與另存；不提供送進查詢視窗，避免在連著來源的連線上誤執行。
    /// </summary>
    public sealed class SchemaSyncScriptForm : Form
    {
        private readonly SchemaSyncScript script;
        private readonly ToolStripStatusLabel statusLabel;

        public SchemaSyncScriptForm(SchemaSyncScript script, string targetDescription)
        {
            if (script == null) throw new ArgumentNullException("script");
            this.script = script;

            Text = Localization.T("SchemaSync.WindowTitle");
            Width = 980;
            Height = 700;
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterParent;

            Label targetLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(10, 6, 10, 0),
                AutoEllipsis = true,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(181, 71, 8),
                Text = Localization.Format("SchemaSync.RunOnTarget", SchemaSyncScriptService.SingleLine(targetDescription))
            };
            Label summaryLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(10, 2, 10, 0),
                AutoEllipsis = true,
                Text = script.Summary
            };

            TextBox editor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 10f),
                Text = script.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
            };

            ToolStrip toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            ToolStripButton copyButton = new ToolStripButton(Localization.T("SchemaSync.Copy"));
            ToolStripButton saveButton = new ToolStripButton(Localization.T("SchemaSync.Save"));
            ToolStripButton closeButton = new ToolStripButton(Localization.T("Common.Close"));
            toolStrip.Items.AddRange(new ToolStripItem[] { copyButton, saveButton, new ToolStripSeparator(), closeButton });

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("SchemaSync.DestructiveNotice"))
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(editor);
            Controls.Add(summaryLabel);
            Controls.Add(targetLabel);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            copyButton.Click += (sender, args) => CopyScript();
            saveButton.Click += (sender, args) => SaveScript();
            closeButton.Click += (sender, args) => Close();
            ThemeManager.ApplyTo(this);
        }

        public SchemaSyncScript Script
        {
            get { return script; }
        }

        private void CopyScript()
        {
            try
            {
                Clipboard.SetText(script.Text);
                statusLabel.Text = Localization.T("SchemaSync.Copied");
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
            }
        }

        private void SaveScript()
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "SQL|*.sql",
                DefaultExt = "sql",
                AddExtension = true,
                FileName = "schema_sync_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql",
                Title = Localization.T("SchemaSync.Save")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, script.Text, new UTF8Encoding(false));
                    if (File.Exists(dialog.FileName)) File.Replace(temporary, dialog.FileName, null);
                    else File.Move(temporary, dialog.FileName);
                    statusLabel.Text = Localization.Format("SchemaSync.Saved", dialog.FileName);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    MessageBox.Show(Localization.Format("SchemaSync.SaveFailed", ExceptionMessageService.GetReason(ex)),
                        Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}

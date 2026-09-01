using System;
using System.Drawing;
using System.Windows.Forms;

namespace mySQLPunk
{
    /// <summary>
    /// Punky 代為操作遇到危險 SQL(DROP/TRUNCATE/DELETE、無 WHERE 的 UPDATE 等)時的確認視窗。
    /// 一律顯示即將執行的完整 SQL,預設焦點在「取消」。
    /// </summary>
    public class AiAgentDangerConfirmForm : Form
    {
        private AiAgentDangerConfirmForm(string riskReason, string target, string sql)
        {
            Text = Localization.T("Ai.AgentDangerTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 330);

            Label warningLabel = new Label
            {
                Text = Localization.Format("Ai.AgentDangerReason", riskReason ?? ""),
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Location = new Point(16, 14),
                ForeColor = ThemeManager.DangerColor
            };
            Controls.Add(warningLabel);

            Label targetLabel = new Label
            {
                Text = Localization.Format("Ai.AgentDangerTarget", target ?? ""),
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Location = new Point(16, warningLabel.Bottom + 22)
            };
            Controls.Add(targetLabel);

            TextBox sqlBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = UiKit.GetMonoFont(UiMetrics.FontSizeBody),
                Text = (sql ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                Location = new Point(16, 84),
                Size = new Size(528, 190),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(sqlBox);

            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, UiMetrics.ControlHeight),
                Location = new Point(448, 288),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            Button executeButton = new Button
            {
                Text = Localization.T("Ai.AgentDangerExecute"),
                DialogResult = DialogResult.OK,
                Size = new Size(96, UiMetrics.ControlHeight),
                Location = new Point(344, 288),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            Controls.Add(executeButton);
            Controls.Add(cancelButton);

            AcceptButton = cancelButton; // Enter 也走取消,避免誤按放行危險操作
            CancelButton = cancelButton;
            ActiveControl = cancelButton;
            ThemeManager.ApplyTo(this);
        }

        /// <summary>顯示確認視窗;回 true 代表使用者同意執行。</summary>
        public static bool Confirm(IWin32Window owner, string riskReason, string target, string sql)
        {
            using (var form = new AiAgentDangerConfirmForm(riskReason, target, sql))
            {
                return form.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}

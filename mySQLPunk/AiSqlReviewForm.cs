using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>在任何 SQL 回寫編輯器前，先以逐行並排方式讓使用者確認。</summary>
    public sealed class AiSqlReviewForm : Form
    {
        private readonly DataGridView _diffGrid;
        private readonly Label _introLabel;
        private readonly Button _applyButton;
        private readonly Button _cancelButton;

        public AiSqlReviewForm(string originalSql, string suggestedSql)
        {
            Text = Localization.T("Ai.SqlReviewTitle");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1040, 680);
            MinimumSize = new Size(760, 480);
            Font = UiKit.Body;

            UiSectionHeader header = new UiSectionHeader
            {
                Dock = DockStyle.Top,
                Title = Localization.T("Ai.SqlReviewTitle"),
                Subtitle = Localization.T("Ai.SqlReviewSubtitle"),
                Glyph = UiGlyph.Code
            };

            _introLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(UiMetrics.Space3, 11, UiMetrics.Space3, 0),
                Text = Localization.T("Ai.SqlReviewIntro"),
                ForeColor = ThemeManager.WarningColor
            };

            _diffGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = true,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            ConfigureColumns();
            PopulateDiff(AiSqlReviewService.BuildDiff(originalSql, suggestedSql));

            _applyButton = new Button
            {
                Text = Localization.T("Ai.SqlReviewApply"),
                DialogResult = DialogResult.OK,
                AutoSize = true,
                MinimumSize = new Size(118, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 4, 0, 4)
            };
            _cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(88, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 4, 0, 4)
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(UiMetrics.Space3, 5, UiMetrics.Space3, 5),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.Add(_applyButton);
            buttons.Controls.Add(_cancelButton);

            Controls.Add(_diffGrid);
            Controls.Add(_introLabel);
            Controls.Add(buttons);
            Controls.Add(header);
            AcceptButton = _applyButton;
            CancelButton = _cancelButton;

            ThemeManager.ApplyTo(this);
            ThemeManager.MarkAsPrimary(_applyButton);
            _introLabel.ForeColor = ThemeManager.WarningColor;
            ApplyDiffColors();
        }

        private void ConfigureColumns()
        {
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "OriginalLine",
                HeaderText = "#",
                Width = 52,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "OriginalSql",
                HeaderText = Localization.T("Ai.SqlReviewOriginal"),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SuggestedLine",
                HeaderText = "#",
                Width = 52,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SuggestedSql",
                HeaderText = Localization.T("Ai.SqlReviewSuggested"),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });

            Font sqlFont = UiKit.GetMonoFont(UiMetrics.FontSizeBody);
            _diffGrid.Columns["OriginalSql"].DefaultCellStyle.Font = sqlFont;
            _diffGrid.Columns["SuggestedSql"].DefaultCellStyle.Font = sqlFont;
        }

        private void PopulateDiff(IList<AiSqlDiffRow> rows)
        {
            foreach (AiSqlDiffRow diff in rows)
            {
                int rowIndex = _diffGrid.Rows.Add(
                    diff.OriginalLineNumber.HasValue ? diff.OriginalLineNumber.Value.ToString() : string.Empty,
                    diff.OriginalText ?? string.Empty,
                    diff.SuggestedLineNumber.HasValue ? diff.SuggestedLineNumber.Value.ToString() : string.Empty,
                    diff.SuggestedText ?? string.Empty);
                _diffGrid.Rows[rowIndex].Tag = diff.Kind;
            }
        }

        private void ApplyDiffColors()
        {
            Color removedBack = ThemeManager.IsDark
                ? Color.FromArgb(75, 42, 46)
                : Color.FromArgb(255, 235, 235);
            Color addedBack = ThemeManager.IsDark
                ? Color.FromArgb(35, 68, 52)
                : Color.FromArgb(231, 249, 238);
            Color emptyBack = ThemeManager.IsDark
                ? Color.FromArgb(31, 34, 39)
                : Color.FromArgb(247, 248, 250);

            foreach (DataGridViewRow row in _diffGrid.Rows)
            {
                AiSqlDiffKind kind = row.Tag is AiSqlDiffKind ? (AiSqlDiffKind)row.Tag : AiSqlDiffKind.Same;
                if (kind == AiSqlDiffKind.Removed || kind == AiSqlDiffKind.Changed)
                {
                    row.Cells[0].Style.BackColor = removedBack;
                    row.Cells[1].Style.BackColor = removedBack;
                }
                else if (kind == AiSqlDiffKind.Added)
                {
                    row.Cells[0].Style.BackColor = emptyBack;
                    row.Cells[1].Style.BackColor = emptyBack;
                }

                if (kind == AiSqlDiffKind.Added || kind == AiSqlDiffKind.Changed)
                {
                    row.Cells[2].Style.BackColor = addedBack;
                    row.Cells[3].Style.BackColor = addedBack;
                }
                else if (kind == AiSqlDiffKind.Removed)
                {
                    row.Cells[2].Style.BackColor = emptyBack;
                    row.Cells[3].Style.BackColor = emptyBack;
                }
            }
        }
    }
}

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
        private readonly Label _selectionLabel;
        private readonly Button _applyButton;
        private readonly Button _cancelButton;
        private readonly IList<AiSqlDiffRow> _diffRows;
        private readonly string _originalSql;
        private readonly int _changeGroupCount;
        private bool _syncingSelection;

        public string SelectedSql { get; private set; }

        public AiSqlReviewForm(string originalSql, string suggestedSql)
        {
            _originalSql = originalSql ?? string.Empty;
            _diffRows = AiSqlReviewService.BuildDiff(originalSql, suggestedSql);
            _changeGroupCount = GetMaximumChangeGroup(_diffRows);

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
                ReadOnly = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            ConfigureColumns();
            PopulateDiff(_diffRows);

            _applyButton = new Button
            {
                Text = Localization.T("Ai.SqlReviewApply"),
                DialogResult = DialogResult.OK,
                AutoSize = true,
                MinimumSize = new Size(118, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 4, 0, 4)
            };
            _applyButton.Click += (sender, args) => UpdateSelectedSql();
            _cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(88, UiMetrics.ControlHeight),
                Margin = new Padding(UiMetrics.Space2, 4, 0, 4)
            };

            _selectionLabel = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(UiMetrics.Space3, 15, UiMetrics.Space2, 0),
                ForeColor = ThemeManager.MutedTextColor
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 300,
                Padding = new Padding(0, 5, UiMetrics.Space3, 5),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.Add(_applyButton);
            buttons.Controls.Add(_cancelButton);
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 50 };
            footer.Controls.Add(_selectionLabel);
            footer.Controls.Add(buttons);

            Controls.Add(_diffGrid);
            Controls.Add(_introLabel);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = _applyButton;
            CancelButton = _cancelButton;

            ThemeManager.ApplyTo(this);
            ThemeManager.MarkAsPrimary(_applyButton);
            _introLabel.ForeColor = ThemeManager.WarningColor;
            _selectionLabel.ForeColor = ThemeManager.MutedTextColor;
            ApplyDiffColors();
            _diffGrid.CurrentCellDirtyStateChanged += DiffGridCurrentCellDirtyStateChanged;
            _diffGrid.CellValueChanged += DiffGridCellValueChanged;
            UpdateSelectedSql();
        }

        private void ConfigureColumns()
        {
            _diffGrid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "ApplyChange",
                HeaderText = Localization.T("Ai.SqlReviewApplyGroup"),
                Width = 62,
                ThreeState = false,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "OriginalLine",
                HeaderText = "#",
                Width = 52,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "OriginalSql",
                HeaderText = Localization.T("Ai.SqlReviewOriginal"),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SuggestedLine",
                HeaderText = "#",
                Width = 52,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _diffGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SuggestedSql",
                HeaderText = Localization.T("Ai.SqlReviewSuggested"),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                ReadOnly = true,
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
                    diff.Kind == AiSqlDiffKind.Same ? (object)null : true,
                    diff.OriginalLineNumber.HasValue ? diff.OriginalLineNumber.Value.ToString() : string.Empty,
                    diff.OriginalText ?? string.Empty,
                    diff.SuggestedLineNumber.HasValue ? diff.SuggestedLineNumber.Value.ToString() : string.Empty,
                    diff.SuggestedText ?? string.Empty);
                DataGridViewRow gridRow = _diffGrid.Rows[rowIndex];
                gridRow.Tag = diff;
                if (diff.Kind == AiSqlDiffKind.Same)
                {
                    DataGridViewTextBoxCell emptyCell = new DataGridViewTextBoxCell();
                    gridRow.Cells["ApplyChange"] = emptyCell;
                    emptyCell.Value = string.Empty;
                    emptyCell.ReadOnly = true;
                }
                else
                {
                    gridRow.Cells["ApplyChange"].ToolTipText = Localization.Format(
                        "Ai.SqlReviewGroupTooltip",
                        diff.ChangeGroup);
                }
            }
        }

        private void DiffGridCurrentCellDirtyStateChanged(object sender, EventArgs args)
        {
            if (_diffGrid.IsCurrentCellDirty
                && _diffGrid.CurrentCell != null
                && _diffGrid.CurrentCell.OwningColumn != null
                && _diffGrid.CurrentCell.OwningColumn.Name == "ApplyChange")
            {
                _diffGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void DiffGridCellValueChanged(object sender, DataGridViewCellEventArgs args)
        {
            if (_syncingSelection
                || args.RowIndex < 0
                || args.ColumnIndex != _diffGrid.Columns["ApplyChange"].Index)
            {
                return;
            }

            DataGridViewRow changedRow = _diffGrid.Rows[args.RowIndex];
            AiSqlDiffRow changedDiff = changedRow.Tag as AiSqlDiffRow;
            if (changedDiff == null || changedDiff.ChangeGroup <= 0) return;

            bool selected = Convert.ToBoolean(changedRow.Cells["ApplyChange"].Value ?? false);
            _syncingSelection = true;
            try
            {
                foreach (DataGridViewRow row in _diffGrid.Rows)
                {
                    AiSqlDiffRow diff = row.Tag as AiSqlDiffRow;
                    if (diff != null && diff.ChangeGroup == changedDiff.ChangeGroup)
                        row.Cells["ApplyChange"].Value = selected;
                }
            }
            finally
            {
                _syncingSelection = false;
            }
            UpdateSelectedSql();
        }

        private void UpdateSelectedSql()
        {
            HashSet<int> selectedGroups = GetSelectedChangeGroups();
            SelectedSql = AiSqlReviewService.BuildSelectedSql(_diffRows, selectedGroups, _originalSql);
            if (_selectionLabel != null)
            {
                _selectionLabel.Text = Localization.Format(
                    "Ai.SqlReviewSelectedGroups",
                    selectedGroups.Count,
                    _changeGroupCount);
            }
            if (_applyButton != null)
            {
                _applyButton.Enabled = selectedGroups.Count > 0
                    && !string.IsNullOrWhiteSpace(SelectedSql);
            }
        }

        private HashSet<int> GetSelectedChangeGroups()
        {
            HashSet<int> selected = new HashSet<int>();
            foreach (DataGridViewRow row in _diffGrid.Rows)
            {
                AiSqlDiffRow diff = row.Tag as AiSqlDiffRow;
                if (diff == null || diff.ChangeGroup <= 0) continue;
                if (Convert.ToBoolean(row.Cells["ApplyChange"].Value ?? false))
                    selected.Add(diff.ChangeGroup);
            }
            return selected;
        }

        private static int GetMaximumChangeGroup(IList<AiSqlDiffRow> rows)
        {
            int maximum = 0;
            foreach (AiSqlDiffRow row in rows)
            {
                if (row != null && row.ChangeGroup > maximum) maximum = row.ChangeGroup;
            }
            return maximum;
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
                AiSqlDiffRow diff = row.Tag as AiSqlDiffRow;
                AiSqlDiffKind kind = diff == null ? AiSqlDiffKind.Same : diff.Kind;
                if (kind == AiSqlDiffKind.Removed || kind == AiSqlDiffKind.Changed)
                {
                    row.Cells[1].Style.BackColor = removedBack;
                    row.Cells[2].Style.BackColor = removedBack;
                }
                else if (kind == AiSqlDiffKind.Added)
                {
                    row.Cells[1].Style.BackColor = emptyBack;
                    row.Cells[2].Style.BackColor = emptyBack;
                }

                if (kind == AiSqlDiffKind.Added || kind == AiSqlDiffKind.Changed)
                {
                    row.Cells[3].Style.BackColor = addedBack;
                    row.Cells[4].Style.BackColor = addedBack;
                }
                else if (kind == AiSqlDiffKind.Removed)
                {
                    row.Cells[3].Style.BackColor = emptyBack;
                    row.Cells[4].Style.BackColor = emptyBack;
                }
            }
        }
    }
}

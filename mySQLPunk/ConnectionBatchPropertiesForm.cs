using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ConnectionBatchPropertiesForm : Form
    {
        private readonly CheckedListBox connectionList;
        private readonly ComboBox starCombo;
        private readonly ComboBox groupCombo;
        private readonly ComboBox colorCombo;

        public ConnectionBatchPropertiesForm(
            IList<Dictionary<string, object>> connections,
            IEnumerable<string> groups,
            int initiallySelectedIndex)
        {
            Text = Localization.T("ConnectionBatch.Title");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(680, 510);
            Padding = new Padding(UiMetrics.Space5);

            Label description = new Label
            {
                Text = Localization.T("ConnectionBatch.Description"),
                Dock = DockStyle.Top,
                Height = 42,
                ForeColor = ThemeManager.MutedTextColor
            };

            connectionList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false
            };
            for (int i = 0; i < (connections == null ? 0 : connections.Count); i++)
            {
                Dictionary<string, object> connection = connections[i];
                connectionList.Items.Add(new ConnectionChoice(i, connection), i == initiallySelectedIndex);
            }

            Button selectAllButton = new Button { Text = Localization.T("ConnectionBatch.SelectAll"), AutoSize = true };
            Button clearSelectionButton = new Button { Text = Localization.T("ConnectionBatch.ClearSelection"), AutoSize = true };
            selectAllButton.Click += (s, e) => SetAllChecked(true);
            clearSelectionButton.Click += (s, e) => SetAllChecked(false);
            FlowLayoutPanel selectionButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };
            selectionButtons.Controls.Add(selectAllButton);
            selectionButtons.Controls.Add(clearSelectionButton);

            starCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            starCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.NoChange"), null, false));
            starCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.AddStar"), "T", true));
            starCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.RemoveStar"), "F", true));
            starCombo.SelectedIndex = 0;

            groupCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            groupCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.NoChange"), null, false));
            groupCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.NoGroup"), string.Empty, true));
            foreach (string group in (groups ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item))
            {
                groupCombo.Items.Add(new BatchChoice(group, group, true));
            }
            groupCombo.SelectedIndex = 0;

            colorCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            colorCombo.Items.Add(new BatchChoice(Localization.T("ConnectionBatch.NoChange"), null, false));
            foreach (string colorKey in ConnectionBatchPropertiesService.SupportedColorKeys)
            {
                colorCombo.Items.Add(new BatchChoice(GetColorText(colorKey), colorKey, true));
            }
            colorCombo.SelectedIndex = 0;

            TableLayoutPanel properties = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(0, UiMetrics.Space2, 0, 0)
            };
            properties.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            properties.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            properties.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            properties.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            properties.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            AddPropertyRow(properties, 0, Localization.T("ConnectionBatch.Star"), starCombo);
            AddPropertyRow(properties, 1, Localization.T("ConnectionBatch.Group"), groupCombo);
            AddPropertyRow(properties, 2, Localization.T("ConnectionBatch.Color"), colorCombo);

            Button applyButton = new Button
            {
                Text = Localization.T("ConnectionBatch.Apply"),
                AutoSize = true,
                MinimumSize = new Size(96, UiMetrics.ControlHeight)
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(96, UiMetrics.ControlHeight)
            };
            applyButton.Click += ApplyButton_Click;
            FlowLayoutPanel footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            footer.Controls.Add(cancelButton);
            footer.Controls.Add(applyButton);

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(description, 0, 0);
            layout.Controls.Add(connectionList, 0, 1);
            layout.Controls.Add(selectionButtons, 0, 2);
            layout.Controls.Add(properties, 0, 3);
            layout.Controls.Add(footer, 0, 4);
            Controls.Add(layout);

            AcceptButton = applyButton;
            CancelButton = cancelButton;
            Form1.ApplyModernTheme(this);
            ThemeManager.MarkAsPrimary(applyButton);
        }

        public List<int> SelectedConnectionIndexes { get; private set; }
        public ConnectionBatchPropertiesChange Change { get; private set; }

        private void ApplyButton_Click(object sender, EventArgs e)
        {
            List<int> selected = connectionList.CheckedItems.Cast<ConnectionChoice>()
                .Select(item => item.Index)
                .ToList();
            if (selected.Count == 0)
            {
                ShowWarning("ConnectionBatch.SelectAtLeastOne");
                return;
            }

            ConnectionBatchPropertiesChange change = BuildChange();
            if (!change.HasChanges)
            {
                ShowWarning("ConnectionBatch.ChooseProperty");
                return;
            }

            SelectedConnectionIndexes = selected;
            Change = change;
            DialogResult = DialogResult.OK;
            Close();
        }

        private ConnectionBatchPropertiesChange BuildChange()
        {
            BatchChoice star = starCombo.SelectedItem as BatchChoice;
            BatchChoice group = groupCombo.SelectedItem as BatchChoice;
            BatchChoice color = colorCombo.SelectedItem as BatchChoice;

            bool applyGroup = group != null ? group.Apply : groupCombo.SelectedIndex < 0 && !string.IsNullOrWhiteSpace(groupCombo.Text);
            string groupValue = group != null ? group.Value : groupCombo.Text;
            return new ConnectionBatchPropertiesChange
            {
                Starred = star == null || !star.Apply ? (bool?)null : string.Equals(star.Value, "T", StringComparison.Ordinal),
                ApplyGroup = applyGroup,
                Group = groupValue ?? string.Empty,
                ApplyColor = color != null && color.Apply,
                ColorKey = color == null ? "default" : color.Value
            };
        }

        private void SetAllChecked(bool value)
        {
            for (int i = 0; i < connectionList.Items.Count; i++) connectionList.SetItemChecked(i, value);
        }

        private static void AddPropertyRow(TableLayoutPanel panel, int row, string label, Control control)
        {
            Label fieldLabel = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Control field = UiField.Wrap(control);
            field.Margin = new Padding(0, 4, 0, 4);
            panel.Controls.Add(fieldLabel, 0, row);
            panel.Controls.Add(field, 1, row);
        }

        private void ShowWarning(string key)
        {
            MessageBox.Show(Localization.T(key), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string GetColorText(string key)
        {
            switch (key)
            {
                case "red": return Localization.T("Menu.ColorRed");
                case "orange": return Localization.T("Menu.ColorOrange");
                case "yellow": return Localization.T("Menu.ColorYellow");
                case "green": return Localization.T("Menu.ColorGreen");
                case "blue": return Localization.T("Menu.ColorBlue");
                case "purple": return Localization.T("Menu.ColorPurple");
                default: return Localization.T("Menu.ColorDefault");
            }
        }

        private sealed class ConnectionChoice
        {
            public ConnectionChoice(int index, Dictionary<string, object> connection)
            {
                Index = index;
                string name = ConnectionBatchPropertiesService.BuildDisplayName(connection);
                string provider = GetValue(connection, "db_kind");
                string group = GetValue(connection, "conn_group");
                Text = string.IsNullOrWhiteSpace(group)
                    ? name + "  [" + provider + "]"
                    : name + "  [" + provider + "]  —  " + group;
            }

            public int Index { get; private set; }
            public string Text { get; private set; }
            public override string ToString() { return Text; }
        }

        private sealed class BatchChoice
        {
            public BatchChoice(string text, string value, bool apply)
            {
                Text = text;
                Value = value;
                Apply = apply;
            }

            public string Text { get; private set; }
            public string Value { get; private set; }
            public bool Apply { get; private set; }
            public override string ToString() { return Text; }
        }

        private static string GetValue(Dictionary<string, object> connection, string key)
        {
            if (connection != null && connection.ContainsKey(key) && connection[key] != null)
                return connection[key].ToString();
            return string.Empty;
        }
    }
}

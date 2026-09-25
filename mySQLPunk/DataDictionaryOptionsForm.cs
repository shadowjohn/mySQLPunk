using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>資料字典的範本與個人化設定（標題、作者、主色、章節與資料表篩選）。</summary>
    public sealed class DataDictionaryOptionsForm : Form
    {
        private static readonly DataDictionaryTemplate[] Templates = { DataDictionaryTemplate.Full, DataDictionaryTemplate.Compact, DataDictionaryTemplate.ColumnsOnly };

        private readonly ComboBox templateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox titleBox = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox authorBox = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox colorBox = new TextBox { Width = 90 };
        private readonly Panel colorSwatch = new Panel { Width = 24, Height = 22, BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox tocBox = new CheckBox { AutoSize = true };
        private readonly CheckBox viewsBox = new CheckBox { AutoSize = true };
        private readonly CheckBox indexesBox = new CheckBox { AutoSize = true };
        private readonly CheckBox ddlBox = new CheckBox { AutoSize = true };
        private readonly TextBox filterBox = new TextBox { Dock = DockStyle.Fill };
        private readonly Label errorLabel = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(180, 35, 24) };

        public DataDictionaryOptionsForm(DataDictionaryOptions options)
        {
            Text = Localization.T("Dict.OptionsTitle");
            Width = 560;
            Height = 440;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            templateBox.Items.AddRange(new object[] { Localization.T("Dict.Template.Full"), Localization.T("Dict.Template.Compact"), Localization.T("Dict.Template.ColumnsOnly") });
            tocBox.Text = Localization.T("Dict.Option.Toc");
            viewsBox.Text = Localization.T("Dict.Option.Views");
            indexesBox.Text = Localization.T("Dict.Option.Indexes");
            ddlBox.Text = Localization.T("Dict.Option.Ddl");

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(layout, 0, Localization.T("Dict.Option.Template"), templateBox);
            AddRow(layout, 1, Localization.T("Dict.Option.Title"), titleBox);
            AddRow(layout, 2, Localization.T("Dict.Author"), authorBox);
            FlowLayoutPanel colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Button pickColor = new Button { AutoSize = true, Text = Localization.T("Dict.Option.PickColor") };
            colorRow.Controls.Add(colorBox);
            colorRow.Controls.Add(colorSwatch);
            colorRow.Controls.Add(pickColor);
            AddRow(layout, 3, Localization.T("Dict.Option.Color"), colorRow);
            FlowLayoutPanel sections = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
            sections.Controls.AddRange(new Control[] { tocBox, viewsBox, indexesBox, ddlBox });
            AddRow(layout, 4, Localization.T("Dict.Option.Sections"), sections);
            AddRow(layout, 5, Localization.T("Dict.Option.Filter"), filterBox);
            Label filterHint = new Label { AutoSize = true, ForeColor = Color.Gray, Text = Localization.T("Dict.Option.FilterHint") };
            layout.Controls.Add(filterHint, 1, 6);
            layout.Controls.Add(errorLabel, 0, 7);
            layout.SetColumnSpan(errorLabel, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            Button ok = new Button { AutoSize = true, Text = Localization.T("Common.OK") };
            Button cancel = new Button { AutoSize = true, Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 8);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;

            DataDictionaryOptions value = options ?? new DataDictionaryOptions();
            templateBox.SelectedIndex = Math.Max(0, Array.IndexOf(Templates, value.Template));
            titleBox.Text = value.Title ?? string.Empty;
            authorBox.Text = value.Author ?? string.Empty;
            colorBox.Text = value.AccentColor ?? "#2563eb";
            tocBox.Checked = value.IncludeToc;
            viewsBox.Checked = value.IncludeViews;
            indexesBox.Checked = value.IncludeIndexes;
            ddlBox.Checked = value.IncludeDdl;
            filterBox.Text = value.TableFilter ?? string.Empty;
            UpdateSwatch();
            UpdateSectionState();

            colorBox.TextChanged += (sender, args) => UpdateSwatch();
            templateBox.SelectedIndexChanged += (sender, args) => UpdateSectionState();
            pickColor.Click += (sender, args) =>
            {
                using (ColorDialog dialog = new ColorDialog { FullOpen = true, Color = colorSwatch.BackColor })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        colorBox.Text = "#" + dialog.Color.R.ToString("x2", CultureInfo.InvariantCulture) + dialog.Color.G.ToString("x2", CultureInfo.InvariantCulture) + dialog.Color.B.ToString("x2", CultureInfo.InvariantCulture);
                    }
                }
            };
            ok.Click += (sender, args) =>
            {
                try
                {
                    DataDictionaryOptions result = Collect();
                    result.Validate();
                    Options = result;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (InvalidOperationException ex)
                {
                    errorLabel.Text = ex.Message;
                }
            };
            ThemeManager.ApplyTo(this);
        }

        public DataDictionaryOptions Options { get; private set; }

        private DataDictionaryOptions Collect()
        {
            return new DataDictionaryOptions
            {
                Template = Templates[Math.Max(0, templateBox.SelectedIndex)],
                Title = titleBox.Text.Trim(),
                Author = authorBox.Text.Trim(),
                AccentColor = colorBox.Text.Trim(),
                IncludeToc = tocBox.Checked,
                IncludeViews = viewsBox.Checked,
                IncludeIndexes = indexesBox.Checked,
                IncludeDdl = ddlBox.Checked,
                TableFilter = filterBox.Text.Trim()
            };
        }

        private void UpdateSwatch()
        {
            try
            {
                colorSwatch.BackColor = ColorTranslator.FromHtml(colorBox.Text.Trim());
            }
            catch (Exception)
            {
                colorSwatch.BackColor = SystemColors.Control;
            }
        }

        private void UpdateSectionState()
        {
            DataDictionaryTemplate template = Templates[Math.Max(0, templateBox.SelectedIndex)];
            tocBox.Enabled = template != DataDictionaryTemplate.ColumnsOnly;
            indexesBox.Enabled = template != DataDictionaryTemplate.ColumnsOnly;
            ddlBox.Enabled = template == DataDictionaryTemplate.Full;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Text = label, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 5);
            layout.Controls.Add(control, 1, row);
        }
    }
}

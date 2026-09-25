using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 資料產生器字典管理：內建字典唯讀（可另存成自訂字典），自訂字典可新增、編輯、從 CSV 匯入、刪除，
    /// 並可抽樣預覽與套用到目前欄位。
    /// </summary>
    public sealed class DataGeneratorDictionaryForm : Form
    {
        private readonly string directory;
        private readonly ListBox list;
        private readonly TextBox nameBox;
        private readonly TextBox contentBox;
        private readonly Label infoLabel;
        private readonly Button saveButton;
        private readonly Button deleteButton;
        private readonly Button applyButton;
        private string loadedName;

        public DataGeneratorDictionaryForm(string directory, string currentColumn)
        {
            this.directory = directory;
            Text = Localization.T("DataGen.Dictionary.Title");
            Width = 860;
            Height = 600;
            MinimumSize = new Size(640, 440);
            StartPosition = FormStartPosition.CenterParent;

            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            Button newButton = new Button { Text = Localization.T("DataGen.Dictionary.New"), AutoSize = true };
            Button importButton = new Button { Text = Localization.T("DataGen.Dictionary.Import"), AutoSize = true };
            FlowLayoutPanel listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            listButtons.Controls.AddRange(new Control[] { newButton, importButton });
            Panel left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(8) };
            left.Controls.Add(list);
            left.Controls.Add(listButtons);

            nameBox = new TextBox { Dock = DockStyle.Top };
            contentBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = UiKit.GetMonoFont(10f)
            };
            infoLabel = new Label { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(0, 4, 0, 0), AutoEllipsis = true };
            Label help = new Label { Dock = DockStyle.Top, Height = 38, Padding = new Padding(0, 4, 0, 0), ForeColor = ThemeManager.MutedTextColor, Text = Localization.T("DataGen.Dictionary.Help") };
            Label nameLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = Localization.T("DataGen.Dictionary.Name") };
            Panel right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 8, 8) };
            right.Controls.Add(contentBox);
            right.Controls.Add(help);
            right.Controls.Add(nameBox);
            right.Controls.Add(nameLabel);
            right.Controls.Add(infoLabel);

            saveButton = new Button { Text = Localization.T("DataGen.Dictionary.Save"), AutoSize = true };
            deleteButton = new Button { Text = Localization.T("DataGen.Dictionary.Delete"), AutoSize = true };
            Button sampleButton = new Button { Text = Localization.T("DataGen.Dictionary.Sample"), AutoSize = true };
            applyButton = new Button
            {
                Text = currentColumn == null ? Localization.T("DataGen.Dictionary.ApplyNoColumn") : Localization.Format("DataGen.Dictionary.Apply", currentColumn),
                AutoSize = true,
                Enabled = currentColumn != null
            };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.AddRange(new Control[] { closeButton, applyButton, sampleButton, deleteButton, saveButton });

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(buttons);
            CancelButton = closeButton;

            list.SelectedIndexChanged += (sender, args) => { if (list.SelectedItem != null) Select((string)list.SelectedItem); };
            newButton.Click += (sender, args) =>
            {
                list.ClearSelected();
                loadedName = null;
                nameBox.Text = string.Empty;
                nameBox.ReadOnly = false;
                contentBox.Text = string.Empty;
                contentBox.ReadOnly = false;
                infoLabel.Text = Localization.T("DataGen.Dictionary.NewHint");
                UpdateButtons();
                nameBox.Focus();
            };
            importButton.Click += (sender, args) => Import();
            saveButton.Click += (sender, args) => Guard(() => SaveCurrent());
            deleteButton.Click += (sender, args) => Guard(DeleteCurrent);
            sampleButton.Click += (sender, args) => Guard(ShowSample);
            applyButton.Click += (sender, args) => Guard(() =>
            {
                DataGeneratorDictionary dictionary = CurrentDictionary();
                AppliedDictionary = dictionary.Name;
                DialogResult = DialogResult.OK;
            });
            ThemeManager.ApplyTo(this);
            Reload(null);
        }

        /// <summary>按「套用到欄位」時選定的字典名稱。</summary>
        public string AppliedDictionary { get; private set; }

        public IList<string> Names { get { return list.Items.Cast<string>().ToList(); } }

        private void Reload(string select)
        {
            list.Items.Clear();
            foreach (string name in DataGeneratorDictionaryStore.List(directory)) list.Items.Add(name);
            int index = select == null ? 0 : list.Items.IndexOf(select);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, index);
        }

        public void Select(string name)
        {
            if (!Equals(list.SelectedItem, name) && list.Items.Contains(name))
            {
                list.SelectedItem = name;
                return;
            }
            bool builtIn = DataGeneratorDictionaryStore.IsBuiltIn(name);
            loadedName = name;
            nameBox.Text = name;
            nameBox.ReadOnly = builtIn;
            contentBox.ReadOnly = builtIn;
            try
            {
                DataGeneratorDictionary dictionary = DataGeneratorDictionaryStore.Load(directory, name);
                contentBox.Text = dictionary == null ? string.Empty : DataGeneratorDictionaryStore.Serialize(dictionary).Replace("\n", "\r\n");
                infoLabel.ForeColor = ThemeManager.TextColor;
                infoLabel.Text = dictionary == null ? string.Empty : Describe(dictionary) + (builtIn ? " " + Localization.T("DataGen.Dictionary.BuiltInHint") : string.Empty);
            }
            catch (InvalidOperationException exception)
            {
                contentBox.Text = string.Empty;
                infoLabel.ForeColor = ThemeManager.DangerColor;
                infoLabel.Text = exception.Message;
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool builtIn = DataGeneratorDictionaryStore.IsBuiltIn(loadedName);
            deleteButton.Enabled = loadedName != null && !builtIn;
            saveButton.Text = builtIn ? Localization.T("DataGen.Dictionary.SaveCopy") : Localization.T("DataGen.Dictionary.Save");
        }

        private static string Describe(DataGeneratorDictionary dictionary)
        {
            return Localization.Format("DataGen.Dictionary.Info", dictionary.Values.Count, dictionary.TotalWeight);
        }

        private DataGeneratorDictionary CurrentDictionary()
        {
            string name = nameBox.Text.Trim();
            if (DataGeneratorDictionaryStore.IsBuiltIn(name) && contentBox.ReadOnly) return DataGeneratorDictionaryStore.Load(directory, name);
            return DataGeneratorDictionaryStore.Parse(name.Length == 0 ? "?" : name, contentBox.Text);
        }

        /// <summary>儲存目前內容；內建字典改存成「名稱 副本」。也供測試直接呼叫。</summary>
        public DataGeneratorDictionary SaveCurrent(string name = null, string content = null)
        {
            if (name != null) nameBox.Text = name;
            if (content != null) contentBox.Text = content;
            string target = nameBox.Text.Trim();
            if (DataGeneratorDictionaryStore.IsBuiltIn(target))
            {
                target = UniqueCopyName(target);
            }
            if (loadedName != null && !DataGeneratorDictionaryStore.IsBuiltIn(loadedName) && !string.Equals(loadedName, target, StringComparison.Ordinal) &&
                DataGeneratorDictionaryStore.List(directory).Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.Exists", target));
            }
            DataGeneratorDictionary saved = DataGeneratorDictionaryStore.Save(directory, target, contentBox.Text);
            if (loadedName != null && !DataGeneratorDictionaryStore.IsBuiltIn(loadedName) && !string.Equals(loadedName, target, StringComparison.OrdinalIgnoreCase))
            {
                DataGeneratorDictionaryStore.Delete(directory, loadedName);
            }
            Reload(target);
            infoLabel.Text = Localization.Format("DataGen.Dictionary.Saved", target) + " " + Describe(saved);
            return saved;
        }

        private string UniqueCopyName(string builtIn)
        {
            List<string> existing = DataGeneratorDictionaryStore.List(directory);
            string candidate = builtIn + " copy";
            int suffix = 2;
            while (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidate = builtIn + " copy " + suffix++;
            return candidate;
        }

        private void DeleteCurrent()
        {
            if (loadedName == null || DataGeneratorDictionaryStore.IsBuiltIn(loadedName)) return;
            if (MessageBox.Show(this, Localization.Format("DataGen.Dictionary.ConfirmDelete", loadedName), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            DataGeneratorDictionaryStore.Delete(directory, loadedName);
            Reload(null);
        }

        private void ShowSample()
        {
            DataGeneratorDictionary dictionary = CurrentDictionary();
            Random random = new Random();
            string sample = string.Join(", ", Enumerable.Range(0, 12).Select(_ => dictionary.Pick(random)));
            infoLabel.ForeColor = ThemeManager.TextColor;
            infoLabel.Text = Describe(dictionary) + Environment.NewLine + Localization.Format("DataGen.Dictionary.SampleResult", sample);
        }

        private void Import()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = Localization.T("DataGen.Dictionary.ImportFilter") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Guard(() =>
                {
                    if (new FileInfo(dialog.FileName).Length > DataGeneratorDictionaryStore.MaximumFileBytes)
                    {
                        throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.TooLarge", Path.GetFileName(dialog.FileName), DataGeneratorDictionaryStore.MaximumFileBytes / (1024 * 1024)));
                    }
                    bool header = MessageBox.Show(this, Localization.T("DataGen.Dictionary.ImportHeader"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    string text = DataGeneratorDictionaryStore.ImportText(File.ReadAllText(dialog.FileName, Encoding.UTF8), header);
                    list.ClearSelected();
                    loadedName = null;
                    nameBox.ReadOnly = false;
                    contentBox.ReadOnly = false;
                    string name = new string(Path.GetFileNameWithoutExtension(dialog.FileName).Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ').Take(60).ToArray()).Trim();
                    nameBox.Text = name;
                    contentBox.Text = text.Replace("\n", "\r\n");
                    DataGeneratorDictionary parsed = DataGeneratorDictionaryStore.Parse(name.Length == 0 ? "?" : name, text);
                    infoLabel.ForeColor = ThemeManager.TextColor;
                    infoLabel.Text = Localization.T("DataGen.Dictionary.Imported") + " " + Describe(parsed);
                    UpdateButtons();
                });
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is IOException || exception is UnauthorizedAccessException)
            {
                infoLabel.ForeColor = ThemeManager.DangerColor;
                infoLabel.Text = exception.Message;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ViewColumnChooserForm : Form
    {
        private readonly TreeView categoryTree;
        private readonly CheckedListBox columnList;
        private string currentProvider;
        private string currentGroup;

        public string SelectedProvider => currentProvider;
        public string SelectedGroup => currentGroup;

        public ViewColumnChooserForm(string provider, string groupKey)
        {
            currentProvider = ViewColumnPreferenceService.NormalizeProvider(provider);
            currentGroup = ViewColumnPreferenceService.NormalizeGroup(groupKey);

            Text = Localization.T("View.ColumnChooserTitle");
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(620, 500);

            categoryTree = new TreeView
            {
                Dock = DockStyle.Left,
                Width = 176,
                HideSelection = false
            };
            categoryTree.AfterSelect += (s, e) => SelectCategory(e.Node);

            Panel contentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 10, 10) };
            Label titleLabel = new Label
            {
                Text = Localization.T("View.ColumnChooserColumns"),
                Dock = DockStyle.Top,
                Height = 24
            };

            columnList = new CheckedListBox
            {
                Dock = DockStyle.Left,
                Width = 238,
                CheckOnClick = true,
                IntegralHeight = false
            };

            FlowLayoutPanel actionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 166,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(8, 16, 0, 0),
                WrapContents = false
            };

            Button upButton = CreateActionButton(Localization.T("View.ColumnChooserMoveUp"));
            Button downButton = CreateActionButton(Localization.T("View.ColumnChooserMoveDown"));
            Button selectAllButton = CreateActionButton(Localization.T("View.ColumnChooserSelectAll"));
            Button unselectAllButton = CreateActionButton(Localization.T("View.ColumnChooserUnselectAll"));
            Button resetButton = CreateActionButton(Localization.T("View.ColumnChooserDefault"));

            upButton.Click += (s, e) => MoveSelectedColumn(-1);
            downButton.Click += (s, e) => MoveSelectedColumn(1);
            selectAllButton.Click += (s, e) => SetAllChecked(true);
            unselectAllButton.Click += (s, e) => SetAllChecked(false);
            resetButton.Click += (s, e) =>
            {
                // 只把「回到預設」記在暫存，按確定才真正寫檔；按取消要救得回來
                pendingSaves[BuildPendingKey(currentProvider, currentGroup)] = null;
                LoadColumns(currentProvider, currentGroup);
            };

            actionPanel.Controls.Add(upButton);
            actionPanel.Controls.Add(downButton);
            actionPanel.Controls.Add(selectAllButton);
            actionPanel.Controls.Add(unselectAllButton);
            actionPanel.Controls.Add(resetButton);

            Panel buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            Button okButton = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, Width = 78, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            Button cancelButton = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, Width = 78, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            okButton.Location = new Point(buttonPanel.Width - 174, 12);
            cancelButton.Location = new Point(buttonPanel.Width - 88, 12);
            buttonPanel.Resize += (s, e) =>
            {
                okButton.Location = new Point(buttonPanel.Width - 174, 12);
                cancelButton.Location = new Point(buttonPanel.Width - 88, 12);
            };
            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);

            contentPanel.Controls.Add(actionPanel);
            contentPanel.Controls.Add(columnList);
            contentPanel.Controls.Add(titleLabel);

            Controls.Add(contentPanel);
            Controls.Add(categoryTree);
            Controls.Add(buttonPanel);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            BuildCategoryTree();
            SelectInitialNode();
            ThemeManager.ApplyTo(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                SaveCurrentCategory();
                foreach (KeyValuePair<string, List<ViewColumnPreference>> entry in pendingSaves)
                {
                    string[] keyParts = entry.Key.Split('|');
                    if (entry.Value == null)
                    {
                        ViewColumnPreferenceService.Reset(keyParts[0], keyParts[1]);
                    }
                    else
                    {
                        ViewColumnPreferenceService.Save(keyParts[0], keyParts[1], entry.Value);
                    }
                }
            }
            base.OnFormClosing(e);
        }

        // 尚未按確定前的變更全部暫存在記憶體；value 為 null 代表「還原成預設」
        private readonly Dictionary<string, List<ViewColumnPreference>> pendingSaves =
            new Dictionary<string, List<ViewColumnPreference>>(StringComparer.OrdinalIgnoreCase);

        private static string BuildPendingKey(string provider, string groupKey)
        {
            return provider + "|" + groupKey;
        }

        private Button CreateActionButton(string text)
        {
            return new Button
            {
                Text = text,
                Width = 150,
                Height = 28,
                Margin = new Padding(0, 0, 0, 6)
            };
        }

        private void BuildCategoryTree()
        {
            categoryTree.Nodes.Clear();
            foreach (string provider in ViewColumnPreferenceService.Providers)
            {
                TreeNode providerNode = new TreeNode(ViewColumnPreferenceService.GetProviderDisplayName(provider))
                {
                    Tag = provider
                };

                foreach (string group in ViewColumnPreferenceService.Groups)
                {
                    providerNode.Nodes.Add(new TreeNode(ViewColumnPreferenceService.GetGroupDisplayName(group))
                    {
                        Tag = provider + "|" + group
                    });
                }

                categoryTree.Nodes.Add(providerNode);
                if (string.Equals(provider, currentProvider, StringComparison.OrdinalIgnoreCase)) providerNode.Expand();
            }
        }

        private void SelectInitialNode()
        {
            foreach (TreeNode providerNode in categoryTree.Nodes)
            {
                foreach (TreeNode groupNode in providerNode.Nodes)
                {
                    string[] parts = (groupNode.Tag as string ?? "").Split('|');
                    if (parts.Length == 2 &&
                        string.Equals(parts[0], currentProvider, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(parts[1], currentGroup, StringComparison.OrdinalIgnoreCase))
                    {
                        categoryTree.SelectedNode = groupNode;
                        groupNode.EnsureVisible();
                        return;
                    }
                }
            }
        }

        private void SelectCategory(TreeNode node)
        {
            if (node == null) return;
            string tag = node.Tag as string;
            if (string.IsNullOrWhiteSpace(tag) || !tag.Contains("|"))
            {
                if (node.Nodes.Count > 0)
                {
                    node.Expand();
                    categoryTree.SelectedNode = node.Nodes[0];
                }
                return;
            }

            SaveCurrentCategory();
            string[] parts = tag.Split('|');
            currentProvider = ViewColumnPreferenceService.NormalizeProvider(parts[0]);
            currentGroup = ViewColumnPreferenceService.NormalizeGroup(parts[1]);
            LoadColumns(currentProvider, currentGroup);
        }

        private void LoadColumns(string provider, string groupKey)
        {
            columnList.Items.Clear();
            List<ViewColumnPreference> pending;
            string pendingKey = BuildPendingKey(provider, groupKey);
            if (pendingSaves.TryGetValue(pendingKey, out pending) && pending != null)
            {
                // 有尚未落地的變更就顯示暫存版，切回來時才看得到剛才的勾選
                foreach (ViewColumnPreference pref in pending)
                {
                    columnList.Items.Add(pref, pref.Visible);
                }
                return;
            }
            if (pendingSaves.ContainsKey(pendingKey) && pending == null)
            {
                foreach (ViewColumnPreference pref in ViewColumnPreferenceService.LoadDefaults(provider, groupKey))
                {
                    columnList.Items.Add(pref, pref.Visible);
                }
                return;
            }
            foreach (ViewColumnPreference pref in ViewColumnPreferenceService.Load(provider, groupKey))
            {
                columnList.Items.Add(pref, pref.Visible);
            }
        }

        private void SaveCurrentCategory()
        {
            if (string.IsNullOrWhiteSpace(currentProvider) || string.IsNullOrWhiteSpace(currentGroup)) return;
            List<ViewColumnPreference> prefs = new List<ViewColumnPreference>();
            for (int i = 0; i < columnList.Items.Count; i++)
            {
                ViewColumnPreference item = columnList.Items[i] as ViewColumnPreference;
                if (item == null) continue;
                prefs.Add(new ViewColumnPreference
                {
                    Name = item.Name,
                    Visible = columnList.GetItemChecked(i)
                });
            }
            // 切分類時只進暫存；直接寫檔的話「取消」就救不回已切走的分類
            if (prefs.Count > 0) pendingSaves[BuildPendingKey(currentProvider, currentGroup)] = prefs;
        }

        private void MoveSelectedColumn(int offset)
        {
            int index = columnList.SelectedIndex;
            if (index < 0) return;
            int target = index + offset;
            if (target < 0 || target >= columnList.Items.Count) return;

            object item = columnList.Items[index];
            bool isChecked = columnList.GetItemChecked(index);
            columnList.Items.RemoveAt(index);
            columnList.Items.Insert(target, item);
            columnList.SetItemChecked(target, isChecked);
            columnList.SelectedIndex = target;
        }

        private void SetAllChecked(bool value)
        {
            for (int i = 0; i < columnList.Items.Count; i++)
            {
                columnList.SetItemChecked(i, value);
            }
        }
    }
}
